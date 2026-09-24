using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Dhcp;

public sealed class DhcpServer : IDisposable
{
    private const SocketOptionName IpInterfaceList = (SocketOptionName)28;
    private const SocketOptionName IpAddInterfaceToList = (SocketOptionName)29;
    private const SocketOptionName IpUnicastInterface = (SocketOptionName)31;
    private readonly ILogger _logger;
    private readonly int _listenPort;
    private readonly int _clientPort;
    private readonly IPAddress? _replyAddress;
    private readonly bool _allowUnscopedTestBinding;
    private readonly object _sync = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Task? _expirationTask;

    public DhcpServer(ILogger logger, int listenPort = 67, int clientPort = 68, IPAddress? replyAddress = null, bool allowUnscopedTestBinding = false)
    {
        _logger = logger;
        if (listenPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(listenPort));
        if (clientPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(clientPort));
        _listenPort = listenPort;
        _clientPort = clientPort;
        _replyAddress = replyAddress;
        _allowUnscopedTestBinding = allowUnscopedTestBinding;
    }

    public event Action<DhcpLease>? LeaseChanged;
    public event Action<DhcpLeaseTableSnapshot>? LeaseTableChanged;
    public event Action<string>? StoppedUnexpectedly;
    public bool IsRunning { get { lock (_sync) return _udp != null; } }
    public int ListenPort
    {
        get
        {
            lock (_sync) return (_udp?.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0;
        }
    }
    internal IPEndPoint? BoundEndpoint
    {
        get
        {
            lock (_sync) return _udp?.Client.LocalEndPoint as IPEndPoint;
        }
    }

    public Task StartAsync(
        DhcpServerSettings settings,
        IEnumerable<DhcpLeaseBinding>? restoredBindings = null,
        IEnumerable<DhcpDeclinedAddress>? restoredDeclined = null,
        Action<DhcpLeaseTableSnapshot>? persistLeaseTable = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        DhcpLeaseManager leases;
        lock (_sync)
        {
            if (_udp != null) return Task.CompletedTask;
            if (settings.InterfaceIndex <= 0 || string.IsNullOrWhiteSpace(settings.AdapterId))
            {
                if (!_allowUnscopedTestBinding)
                    throw new InvalidOperationException("DHCP requires a stable selected adapter ID and interface index.");
            }
            else
            {
                ValidateSelectedAdapter(settings);
            }

            leases = new DhcpLeaseManager(settings, restoredBindings, restoredDeclined, persist: persistLeaseTable, logger: _logger);
            leases.LeaseChanged += lease => LeaseChanged?.Invoke(lease);
            leases.TableChanged += snapshot => LeaseTableChanged?.Invoke(snapshot);
            var udp = new UdpClient();
            var cts = new CancellationTokenSource();
            try
            {
                udp.EnableBroadcast = true;
                udp.Client.ExclusiveAddressUse = true;
                udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                if (settings.InterfaceIndex > 0)
                    ConfigureInterfaceScope(udp.Client, settings.InterfaceIndex);
                udp.Client.Bind(new IPEndPoint(settings.ServerIp, _listenPort));
                _udp = udp;
                _cts = cts;
                _loopTask = Task.Run(() => LoopAsync(udp, cts.Token, settings, leases));
                _expirationTask = Task.Run(() => ExpireLeasesAsync(leases, cts.Token));
            }
            catch
            {
                udp.Dispose();
                cts.Dispose();
                _udp = null;
                _cts = null;
                _loopTask = null;
                _expirationTask = null;
                throw;
            }
        }
        _logger.Info($"DHCP start: adapterId={settings.AdapterId} interfaceIndex={settings.InterfaceIndex} serverIp={settings.ServerIp} pool={settings.PoolStart}-{settings.PoolEnd} activeLeases={leases.Leases.Count}");
        foreach (var lease in leases.Leases) LeaseChanged?.Invoke(lease);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        UdpClient? udp;
        CancellationTokenSource? cts;
        Task? loop;
        Task? expiration;
        lock (_sync)
        {
            udp = _udp;
            cts = _cts;
            loop = _loopTask;
            expiration = _expirationTask;
            _udp = null;
            _cts = null;
            _loopTask = null;
            _expirationTask = null;
        }
        if (udp == null) return;
        cts?.Cancel();
        udp.Dispose();
        if (cts != null)
        {
            _ = Task.WhenAll(loop ?? Task.CompletedTask, expiration ?? Task.CompletedTask)
                .ContinueWith(_ => cts.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        _logger.Info("DHCP stop");
    }

    private async Task LoopAsync(UdpClient udp, CancellationToken ct, DhcpServerSettings settings, DhcpLeaseManager leases)
    {
        var receiveBuffer = new byte[ushort.MaxValue];
        EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var received = await udp.Client.ReceiveMessageFromAsync(receiveBuffer.AsMemory(), SocketFlags.None, remoteEndpoint, ct);
                var incomingInterface = received.PacketInformation.Interface;
                if (settings.InterfaceIndex > 0 && !IsTargetInterface(settings.InterfaceIndex, incomingInterface))
                {
                    _logger.Warn($"DHCP UDP ignored: reason=non-target-interface expected={settings.InterfaceIndex} actual={incomingInterface} from={received.RemoteEndPoint}");
                    continue;
                }
                try
                {
                    if (settings.InterfaceIndex > 0) ValidateSelectedAdapter(settings);
                }
                catch (Exception identityError)
                {
                    StopForAdapterLoss($"The selected DHCP adapter identity or address changed: {identityError.Message}", identityError);
                    return;
                }

                var buffer = receiveBuffer.AsSpan(0, received.ReceivedBytes).ToArray();
                _logger.Info($"DHCP UDP received: from={received.RemoteEndPoint} interfaceIndex={incomingInterface} localAddress={received.PacketInformation.Address} bytes={buffer.Length}");
                DhcpPacket packet;
                try
                {
                    packet = DhcpPacketParser.Parse(buffer);
                }
                catch (InvalidDataException ex)
                {
                    _logger.Warn($"DHCP UDP ignored: malformed or unsupported packet from={received.RemoteEndPoint}: {ex.Message}");
                    continue;
                }
                if (packet.Op != 1) continue;
                _logger.Info($"DHCP packet: type={packet.MessageType} client={packet.ClientKey} mac={packet.MacAddress} host={packet.Hostname}");
                var decision = leases.Process(packet);
                if (decision.ResponseType is not { } replyType || decision.Address is not { } replyAddress) continue;

                var reply = DhcpPacketParser.BuildReply(packet, settings, replyAddress, replyType);
                var replyTarget = _replyAddress ?? ResolveReplyTarget(packet, replyType);
                await SendReplyAsync(udp, reply, replyTarget, settings, ct);
                var subnetBroadcast = IpNetwork.BroadcastAddress(settings.ServerIp, settings.SubnetMask);
                if (_replyAddress == null && replyTarget.Equals(IPAddress.Broadcast) && !subnetBroadcast.Equals(IPAddress.Broadcast))
                    await SendReplyAsync(udp, reply, subnetBroadcast, settings, ct);
                _logger.Info($"DHCP {replyType}: client={packet.ClientKey} address={replyAddress} target={replyTarget}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { break; }
            catch (IOException ex)
            {
                if (ct.IsCancellationRequested) break;
                StopUnexpectedly("DHCP lease journal persistence failed; the service stopped before acknowledging the request.", ex);
                return;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                _logger.Error("DHCP loop packet processing failed; receive loop remains active", ex);
            }
        }
    }

    private async Task ExpireLeasesAsync(DhcpLeaseManager leases, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) leases.SweepExpired();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) _logger.Error("DHCP lease expiry sweep failed; existing lease records remain retained", ex);
        }
    }

    private static IPAddress ResolveReplyTarget(DhcpPacket request, DhcpMessageType responseType) =>
        responseType == DhcpMessageType.Ack && !request.CiAddr.Equals(IPAddress.Any)
            ? request.CiAddr
            : IPAddress.Broadcast;

    private async Task SendReplyAsync(UdpClient udp, byte[] reply, IPAddress target, DhcpServerSettings settings, CancellationToken ct)
    {
        if (settings.InterfaceIndex > 0)
        {
            try { ValidateSelectedAdapter(settings); }
            catch (Exception identityError)
            {
                StopForAdapterLoss($"The selected DHCP adapter identity or address changed before replying: {identityError.Message}", identityError);
                throw new OperationCanceledException("The selected DHCP adapter is no longer valid.", identityError, ct);
            }
        }
        var endpoint = new IPEndPoint(target, _clientPort);
        await udp.Client.SendToAsync(reply.AsMemory(), SocketFlags.None, endpoint, ct);
        _logger.Info($"DHCP reply sent: target={endpoint} interfaceIndex={settings.InterfaceIndex}");
    }

    private void StopForAdapterLoss(string reason, Exception exception)
    {
        StopUnexpectedly("The selected DHCP adapter was lost: " + reason, exception);
    }

    private void StopUnexpectedly(string reason, Exception exception)
    {
        _logger.Error("DHCP stopped unexpectedly: " + reason, exception);
        Stop();
        StoppedUnexpectedly?.Invoke(reason);
    }

    public static bool IsTargetInterface(int selectedInterfaceIndex, int packetInterfaceIndex) =>
        selectedInterfaceIndex > 0 && packetInterfaceIndex == selectedInterfaceIndex;

    internal static void ConfigureInterfaceScope(Socket socket, int interfaceIndex)
    {
        ArgumentNullException.ThrowIfNull(socket);
        if (interfaceIndex <= 0) throw new ArgumentOutOfRangeException(nameof(interfaceIndex));

        socket.SetSocketOption(SocketOptionLevel.IP, IpInterfaceList, true);
        socket.SetSocketOption(SocketOptionLevel.IP, IpAddInterfaceToList, interfaceIndex);
        socket.SetSocketOption(SocketOptionLevel.IP, IpUnicastInterface, IPAddress.HostToNetworkOrder(interfaceIndex));
    }

    private static void ValidateSelectedAdapter(DhcpServerSettings settings)
    {
        if (settings.InterfaceIndex <= 0 || string.IsNullOrWhiteSpace(settings.AdapterId))
            throw new InvalidOperationException("DHCP selected adapter identity is invalid.");

        var matches = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => string.Equals(x.Id, settings.AdapterId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException(matches.Count == 0
                ? "The selected DHCP adapter is unavailable."
                : "The selected DHCP adapter identity is ambiguous.");

        var adapter = matches[0];
        if (adapter.OperationalStatus != OperationalStatus.Up)
            throw new InvalidOperationException("The selected DHCP adapter is not up.");

        var properties = adapter.GetIPProperties();
        var ipv4 = properties.GetIPv4Properties() ?? throw new InvalidOperationException("The selected adapter has no IPv4 interface.");
        if (ipv4.Index != settings.InterfaceIndex)
            throw new InvalidOperationException("The selected DHCP adapter interface index changed.");
        if (!properties.UnicastAddresses.Any(x => x.Address.AddressFamily == AddressFamily.InterNetwork && x.Address.Equals(settings.ServerIp)))
            throw new InvalidOperationException("The selected DHCP server IPv4 address is no longer assigned to that adapter.");
    }

    public void Dispose() => Stop();
}
