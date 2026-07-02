using System.Net;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Dhcp;

public sealed class DhcpLeaseManager
{
    private readonly Dictionary<string, DhcpLease> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly DhcpServerSettings _settings;

    public DhcpLeaseManager(DhcpServerSettings settings)
    {
        _settings = settings;
        Validate();
    }

    public IReadOnlyCollection<DhcpLease> Leases => _leases.Values.ToList();

    public DhcpLease? GetLease(string mac) => _leases.TryGetValue(mac, out var lease) && lease.LeaseEnd > DateTime.Now ? lease : null;

    public DhcpLease Allocate(string mac, string hostname)
    {
        var existing = GetLease(mac);
        if (existing != null)
        {
            existing.Time = DateTime.Now;
            existing.Hostname = string.IsNullOrWhiteSpace(hostname) ? existing.Hostname : hostname;
            existing.LeaseEnd = DateTime.Now.AddSeconds(_settings.LeaseSeconds);
            return existing;
        }

        var used = _leases.Values.Where(x => x.LeaseEnd > DateTime.Now).Select(x => x.IpAddress).ToHashSet();
        var start = IpNetwork.ToUInt32(_settings.PoolStart);
        var end = IpNetwork.ToUInt32(_settings.PoolEnd);
        for (var value = start; value <= end; value++)
        {
            var ip = IpNetwork.FromUInt32(value);
            var text = ip.ToString();
            if (text == _settings.ServerIp.ToString()) continue;
            if (used.Contains(text)) continue;
            var lease = new DhcpLease
            {
                MacAddress = mac,
                IpAddress = text,
                Hostname = hostname,
                LeaseStart = DateTime.Now,
                LeaseEnd = DateTime.Now.AddSeconds(_settings.LeaseSeconds),
                Time = DateTime.Now,
                Status = "Active"
            };
            _leases[mac] = lease;
            return lease;
        }

        throw new InvalidOperationException("DHCP address pool exhausted");
    }

    public void Validate()
    {
        if (!IpNetwork.SameSubnet(_settings.ServerIp, _settings.PoolStart, _settings.SubnetMask)) throw new InvalidOperationException("Pool start is outside server subnet");
        if (!IpNetwork.SameSubnet(_settings.ServerIp, _settings.PoolEnd, _settings.SubnetMask)) throw new InvalidOperationException("Pool end is outside server subnet");
        if (IpNetwork.ToUInt32(_settings.PoolStart) > IpNetwork.ToUInt32(_settings.PoolEnd)) throw new InvalidOperationException("Pool start is greater than pool end");
        if (!IpNetwork.IsUsableHost(_settings.PoolStart, _settings.ServerIp, _settings.SubnetMask)) throw new InvalidOperationException("Pool start is not usable");
        if (!IpNetwork.IsUsableHost(_settings.PoolEnd, _settings.ServerIp, _settings.SubnetMask)) throw new InvalidOperationException("Pool end is not usable");
    }
}
