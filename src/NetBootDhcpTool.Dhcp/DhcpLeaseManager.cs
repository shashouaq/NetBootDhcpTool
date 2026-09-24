using System.Net;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Dhcp;

public sealed class DhcpLeaseManager
{
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DeclineHold = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DhcpLeaseBinding> _bindings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DhcpPendingOffer> _offers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DhcpDeclinedAddress> _declined = new(StringComparer.OrdinalIgnoreCase);
    private readonly DhcpServerSettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly Action<DhcpLeaseTableSnapshot>? _persist;
    private readonly ILogger? _logger;
    private readonly object _sync = new();

    public DhcpLeaseManager(
        DhcpServerSettings settings,
        IEnumerable<DhcpLeaseBinding>? restoredBindings = null,
        IEnumerable<DhcpDeclinedAddress>? restoredDeclined = null,
        TimeProvider? timeProvider = null,
        Action<DhcpLeaseTableSnapshot>? persist = null,
        ILogger? logger = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _persist = persist;
        _logger = logger;
        Validate();
        LoadRestoredState(restoredBindings ?? [], restoredDeclined ?? [], _timeProvider.GetUtcNow());
    }

    public event Action<DhcpLease>? LeaseChanged;
    public event Action<DhcpLeaseTableSnapshot>? TableChanged;

    public IReadOnlyCollection<DhcpLease> Leases
    {
        get
        {
            lock (_sync)
            {
                var now = _timeProvider.GetUtcNow();
                return _bindings.Values.Where(x => x.LeaseEnd > now).Select(x => ToUiLease(x, "Assigned", isActive: true)).ToList();
            }
        }
    }

    public DhcpLeaseDecision Process(DhcpPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            SweepExpired(now);
            if (packet.Op != 1 || packet.HType != 1 || packet.HLen != 6 || packet.Hops > 16 || !packet.GiAddr.Equals(IPAddress.Any))
                return DhcpLeaseDecision.Ignore;

            var clientKey = packet.ClientKey;
            return packet.MessageType switch
            {
                DhcpMessageType.Discover => ProcessDiscover(packet, clientKey, now),
                DhcpMessageType.Request => ProcessRequest(packet, clientKey, now),
                DhcpMessageType.Release => ProcessRelease(packet, clientKey),
                DhcpMessageType.Decline => ProcessDecline(packet, clientKey, now),
                _ => DhcpLeaseDecision.Ignore
            };
        }
    }

    public void SweepExpired()
    {
        lock (_sync) SweepExpired(_timeProvider.GetUtcNow());
    }

    private DhcpLeaseDecision ProcessDiscover(DhcpPacket packet, string clientKey, DateTimeOffset now)
    {
        if (packet.ServerIdentifier != null || !packet.CiAddr.Equals(IPAddress.Any)) return DhcpLeaseDecision.Ignore;
        _offers.Remove(clientKey);
        var active = GetUnexpiredBinding(clientKey, now);
        var offeredIp = active != null
            ? IPAddress.Parse(active.IpAddress)
            : AllocateOfferAddress(clientKey, now);
        if (offeredIp == null) return DhcpLeaseDecision.Ignore;
        _offers[clientKey] = new DhcpPendingOffer(clientKey, offeredIp.ToString(), now + OfferLifetime);
        return new DhcpLeaseDecision(DhcpMessageType.Offer, offeredIp);
    }

    private DhcpLeaseDecision ProcessRequest(DhcpPacket packet, string clientKey, DateTimeOffset now)
    {
        if (packet.ServerIdentifier != null)
        {
            if (!packet.ServerIdentifier.Equals(_settings.ServerIp))
            {
                _offers.Remove(clientKey);
                return DhcpLeaseDecision.Ignore;
            }
            if (packet.RequestedIp == null || !packet.CiAddr.Equals(IPAddress.Any))
                return new DhcpLeaseDecision(DhcpMessageType.Nak, IPAddress.Any);

            var active = GetUnexpiredBinding(clientKey, now);
            var sameActiveAddress = active != null && active.IpAddress.Equals(packet.RequestedIp.ToString(), StringComparison.OrdinalIgnoreCase);
            var hasOffer = _offers.TryGetValue(clientKey, out var offer)
                && offer.ExpiresAt > now
                && offer.IpAddress.Equals(packet.RequestedIp.ToString(), StringComparison.OrdinalIgnoreCase);
            if ((!sameActiveAddress && !hasOffer) || !IsAddressInPool(packet.RequestedIp) || !IsAddressAvailable(packet.RequestedIp, clientKey, now))
                return new DhcpLeaseDecision(DhcpMessageType.Nak, IPAddress.Any);
            return CommitLease(packet, clientKey, packet.RequestedIp, active, now);
        }

        if (packet.RequestedIp != null && packet.CiAddr.Equals(IPAddress.Any))
        {
            var binding = GetUnexpiredBinding(clientKey, now);
            if (binding != null && binding.IpAddress.Equals(packet.RequestedIp.ToString(), StringComparison.OrdinalIgnoreCase)
                && IsAddressInPool(packet.RequestedIp))
                return CommitLease(packet, clientKey, packet.RequestedIp, binding, now);
            return IpNetwork.SameSubnet(_settings.ServerIp, packet.RequestedIp, _settings.SubnetMask)
                ? new DhcpLeaseDecision(DhcpMessageType.Nak, IPAddress.Any)
                : DhcpLeaseDecision.Ignore;
        }

        if (packet.RequestedIp == null && !packet.CiAddr.Equals(IPAddress.Any))
        {
            var binding = GetUnexpiredBinding(clientKey, now);
            if (binding != null && binding.IpAddress.Equals(packet.CiAddr.ToString(), StringComparison.OrdinalIgnoreCase))
                return CommitLease(packet, clientKey, packet.CiAddr, binding, now);
            return IpNetwork.SameSubnet(_settings.ServerIp, packet.CiAddr, _settings.SubnetMask)
                ? new DhcpLeaseDecision(DhcpMessageType.Nak, IPAddress.Any)
                : DhcpLeaseDecision.Ignore;
        }

        return DhcpLeaseDecision.Ignore;
    }

    private DhcpLeaseDecision ProcessRelease(DhcpPacket packet, string clientKey)
    {
        if (packet.ServerIdentifier == null || !packet.ServerIdentifier.Equals(_settings.ServerIp)
            || packet.RequestedIp != null || packet.CiAddr.Equals(IPAddress.Any))
            return DhcpLeaseDecision.Ignore;
        if (!_bindings.TryGetValue(clientKey, out var binding)
            || !binding.IpAddress.Equals(packet.CiAddr.ToString(), StringComparison.OrdinalIgnoreCase))
            return DhcpLeaseDecision.Ignore;

        var removed = Clone(binding);
        _bindings.Remove(clientKey);
        _offers.Remove(clientKey);
        try
        {
            CommitTable();
        }
        catch
        {
            _bindings[clientKey] = binding;
            throw;
        }
        PublishLease(removed, "Released", isActive: false);
        return DhcpLeaseDecision.Ignore;
    }

    private DhcpLeaseDecision ProcessDecline(DhcpPacket packet, string clientKey, DateTimeOffset now)
    {
        if (packet.ServerIdentifier == null || !packet.ServerIdentifier.Equals(_settings.ServerIp)
            || packet.RequestedIp == null || !packet.CiAddr.Equals(IPAddress.Any)
            || !IsAddressInPool(packet.RequestedIp))
            return DhcpLeaseDecision.Ignore;

        var ip = packet.RequestedIp.ToString();
        if (_declined.TryGetValue(ip, out var existingDecline))
            return existingDecline.ClientKey.Equals(clientKey, StringComparison.OrdinalIgnoreCase)
                ? DhcpLeaseDecision.Ignore
                : DhcpLeaseDecision.Ignore;
        var binding = GetUnexpiredBinding(clientKey, now);
        var hasBinding = binding != null && binding.IpAddress.Equals(ip, StringComparison.OrdinalIgnoreCase);
        var hasOffer = _offers.TryGetValue(clientKey, out var offer)
            && offer.ExpiresAt > now
            && offer.IpAddress.Equals(ip, StringComparison.OrdinalIgnoreCase);
        if (!hasBinding && !hasOffer) return DhcpLeaseDecision.Ignore;

        var previousBinding = binding == null ? null : Clone(binding);
        if (hasBinding) _bindings.Remove(clientKey);
        _offers.Remove(clientKey);
        _declined[ip] = new DhcpDeclinedAddress { IpAddress = ip, ClientKey = clientKey, ExpiresAt = now + DeclineHold };
        try
        {
            CommitTable();
        }
        catch
        {
            if (previousBinding != null) _bindings[clientKey] = previousBinding;
            _declined.Remove(ip);
            throw;
        }
        if (previousBinding != null) PublishLease(previousBinding, "Declined", isActive: false);
        _logger?.Warn($"DHCP address declined and quarantined: client={clientKey} ip={ip} holdUntil={now + DeclineHold:O}");
        return DhcpLeaseDecision.Ignore;
    }

    private DhcpLeaseDecision CommitLease(DhcpPacket packet, string clientKey, IPAddress ip, DhcpLeaseBinding? previous, DateTimeOffset now)
    {
        if (!IsAddressInPool(ip) || !IsAddressAvailable(ip, clientKey, now))
            return new DhcpLeaseDecision(DhcpMessageType.Nak, IPAddress.Any);
        var binding = new DhcpLeaseBinding
        {
            ClientKey = clientKey,
            MacAddress = packet.MacAddress,
            ClientIdentifierHex = packet.ClientIdentifier is { Length: > 0 } id ? Convert.ToHexString(id) : "",
            IpAddress = ip.ToString(),
            Hostname = string.IsNullOrWhiteSpace(packet.Hostname) ? previous?.Hostname ?? "" : packet.Hostname,
            LeaseStart = now,
            LeaseEnd = now.AddSeconds(_settings.LeaseSeconds)
        };
        _bindings[clientKey] = binding;
        _offers.Remove(clientKey);
        try
        {
            CommitTable();
        }
        catch
        {
            if (previous == null) _bindings.Remove(clientKey);
            else _bindings[clientKey] = previous;
            throw;
        }
        var lease = ToUiLease(binding, "Assigned", isActive: true);
        LeaseChanged?.Invoke(lease);
        return new DhcpLeaseDecision(DhcpMessageType.Ack, ip, lease);
    }

    private IPAddress? AllocateOfferAddress(string clientKey, DateTimeOffset now)
    {
        var start = IpNetwork.ToUInt32(_settings.PoolStart);
        var end = IpNetwork.ToUInt32(_settings.PoolEnd);
        for (var value = start; ; value++)
        {
            var candidate = IpNetwork.FromUInt32(value);
            if (IsAddressAvailable(candidate, clientKey, now)) return candidate;
            if (value == end) break;
        }
        _logger?.Warn($"DHCP address pool exhausted for client={clientKey}");
        return null;
    }

    private bool IsAddressAvailable(IPAddress address, string clientKey, DateTimeOffset now)
    {
        var text = address.ToString();
        if (_bindings.Values.Any(x => x.LeaseEnd > now && !x.ClientKey.Equals(clientKey, StringComparison.OrdinalIgnoreCase)
            && x.IpAddress.Equals(text, StringComparison.OrdinalIgnoreCase))) return false;
        if (_offers.Values.Any(x => x.ExpiresAt > now && !x.ClientKey.Equals(clientKey, StringComparison.OrdinalIgnoreCase)
            && x.IpAddress.Equals(text, StringComparison.OrdinalIgnoreCase))) return false;
        if (_declined.TryGetValue(text, out var declined) && declined.ExpiresAt > now) return false;
        return true;
    }

    private bool IsAddressInPool(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var value = IpNetwork.ToUInt32(address);
        return value >= IpNetwork.ToUInt32(_settings.PoolStart)
            && value <= IpNetwork.ToUInt32(_settings.PoolEnd)
            && !address.Equals(_settings.ServerIp)
            && IpNetwork.IsUsableHost(address, _settings.ServerIp, _settings.SubnetMask);
    }

    private DhcpLeaseBinding? GetUnexpiredBinding(string clientKey, DateTimeOffset now) =>
        _bindings.TryGetValue(clientKey, out var binding) && binding.LeaseEnd > now ? binding : null;

    private void SweepExpired(DateTimeOffset now)
    {
        var expiredBindings = _bindings.Values.Where(x => x.LeaseEnd <= now).Select(Clone).ToList();
        var expiredDeclines = _declined.Values.Where(x => x.ExpiresAt <= now).Select(Clone).ToList();
        foreach (var item in expiredBindings) _bindings.Remove(item.ClientKey);
        foreach (var item in expiredDeclines) _declined.Remove(item.IpAddress);
        foreach (var key in _offers.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToList()) _offers.Remove(key);
        if (expiredBindings.Count == 0 && expiredDeclines.Count == 0) return;
        try
        {
            CommitTable();
        }
        catch
        {
            foreach (var item in expiredBindings) _bindings[item.ClientKey] = item;
            foreach (var item in expiredDeclines) _declined[item.IpAddress] = item;
            throw;
        }
        foreach (var binding in expiredBindings) PublishLease(binding, "Expired", isActive: false);
    }

    private void LoadRestoredState(IEnumerable<DhcpLeaseBinding> bindings, IEnumerable<DhcpDeclinedAddress> declined, DateTimeOffset now)
    {
        foreach (var binding in bindings.Where(x => x.LeaseEnd > now))
        {
            if (string.IsNullOrWhiteSpace(binding.ClientKey) || !IsAddressInPool(IPAddress.Parse(binding.IpAddress)))
                throw new InvalidDataException("Persisted DHCP lease journal contains a binding outside the configured scope.");
            if (_bindings.ContainsKey(binding.ClientKey) || _bindings.Values.Any(x => x.IpAddress.Equals(binding.IpAddress, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Persisted DHCP lease journal contains duplicate client or address bindings.");
            if (!ClientKeyMatchesBinding(binding))
                throw new InvalidDataException("Persisted DHCP lease identity does not match its client ID or hardware address.");
            _bindings.Add(binding.ClientKey, Clone(binding));
        }
        foreach (var item in declined.Where(x => x.ExpiresAt > now))
        {
            if (!IPAddress.TryParse(item.IpAddress, out var address) || !IsAddressInPool(address) || string.IsNullOrWhiteSpace(item.ClientKey))
                throw new InvalidDataException("Persisted DHCP conflict journal contains an invalid address or client identity.");
            if (_declined.ContainsKey(item.IpAddress) || _bindings.Values.Any(x => x.IpAddress.Equals(item.IpAddress, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Persisted DHCP conflict journal conflicts with an active binding.");
            _declined.Add(item.IpAddress, Clone(item));
        }
    }

    private bool ClientKeyMatchesBinding(DhcpLeaseBinding binding)
    {
        if (!string.IsNullOrWhiteSpace(binding.ClientIdentifierHex))
        {
            try { return ("ID:" + Convert.ToHexString(Convert.FromHexString(binding.ClientIdentifierHex))).Equals(binding.ClientKey, StringComparison.OrdinalIgnoreCase); }
            catch (FormatException) { return false; }
        }
        return ("MAC:" + binding.MacAddress).Equals(binding.ClientKey, StringComparison.OrdinalIgnoreCase);
    }

    private void CommitTable()
    {
        var now = _timeProvider.GetUtcNow();
        var snapshot = new DhcpLeaseTableSnapshot
        {
            Bindings = _bindings.Values.Where(x => x.LeaseEnd > now).Select(Clone).ToList(),
            DeclinedAddresses = _declined.Values.Where(x => x.ExpiresAt > now).Select(Clone).ToList()
        };
        _persist?.Invoke(snapshot);
        TableChanged?.Invoke(snapshot);
    }

    private void PublishLease(DhcpLeaseBinding binding, string status, bool isActive)
    {
        var lease = ToUiLease(binding, status, isActive);
        LeaseChanged?.Invoke(lease);
    }

    private static DhcpLease ToUiLease(DhcpLeaseBinding binding, string status, bool isActive) => new()
    {
        ClientKey = binding.ClientKey,
        MacAddress = binding.MacAddress,
        ClientIdentifier = binding.ClientIdentifierHex,
        IpAddress = binding.IpAddress,
        Hostname = binding.Hostname,
        LeaseStart = binding.LeaseStart.LocalDateTime,
        LeaseEnd = binding.LeaseEnd.LocalDateTime,
        Status = status,
        IsActiveLease = isActive
    };

    private void Validate()
    {
        if (_settings.LeaseSeconds < 1) throw new InvalidOperationException("DHCP lease duration must be positive.");
        if (_settings.ServerIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || _settings.SubnetMask.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || _settings.PoolStart.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || _settings.PoolEnd.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("DHCP server, mask, and pool must use IPv4.");
        if (!IpNetwork.SameSubnet(_settings.ServerIp, _settings.PoolStart, _settings.SubnetMask)) throw new InvalidOperationException("Pool start is outside server subnet");
        if (!IpNetwork.SameSubnet(_settings.ServerIp, _settings.PoolEnd, _settings.SubnetMask)) throw new InvalidOperationException("Pool end is outside server subnet");
        if (IpNetwork.ToUInt32(_settings.PoolStart) > IpNetwork.ToUInt32(_settings.PoolEnd)) throw new InvalidOperationException("Pool start is greater than pool end");
        if (!IpNetwork.IsUsableHost(_settings.PoolStart, _settings.ServerIp, _settings.SubnetMask)) throw new InvalidOperationException("Pool start is not usable");
        if (!IpNetwork.IsUsableHost(_settings.PoolEnd, _settings.ServerIp, _settings.SubnetMask)) throw new InvalidOperationException("Pool end is not usable");
        if (IpNetwork.ToUInt32(_settings.ServerIp) >= IpNetwork.ToUInt32(_settings.PoolStart)
            && IpNetwork.ToUInt32(_settings.ServerIp) <= IpNetwork.ToUInt32(_settings.PoolEnd))
            throw new InvalidOperationException("DHCP pool cannot contain the server address.");
    }

    private static DhcpLeaseBinding Clone(DhcpLeaseBinding source) => new()
    {
        ClientKey = source.ClientKey,
        MacAddress = source.MacAddress,
        ClientIdentifierHex = source.ClientIdentifierHex,
        IpAddress = source.IpAddress,
        Hostname = source.Hostname,
        LeaseStart = source.LeaseStart,
        LeaseEnd = source.LeaseEnd
    };

    private static DhcpDeclinedAddress Clone(DhcpDeclinedAddress source) => new()
    {
        IpAddress = source.IpAddress,
        ClientKey = source.ClientKey,
        ExpiresAt = source.ExpiresAt
    };

    private sealed record DhcpPendingOffer(string ClientKey, string IpAddress, DateTimeOffset ExpiresAt);
}
