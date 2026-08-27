using System.Net;
using System.Net.Sockets;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Dhcp;

public sealed class DhcpServer : IDisposable
{
    private readonly ILogger _logger;
    private readonly int _listenPort;
    private readonly int _clientPort;
    private readonly IPAddress? _replyAddress;
    private readonly object _sync = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public DhcpServer(ILogger logger, int listenPort = 67, int clientPort = 68, IPAddress? replyAddress = null)
    {
        _logger = logger;
        if (listenPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(listenPort));
        if (clientPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(clientPort));
        _listenPort = listenPort;
        _clientPort = clientPort;
        _replyAddress = replyAddress;
    }

    public event Action<DhcpLease>? LeaseChanged;
    public bool IsRunning => _udp != null;
    public int ListenPort
    {
        get
        {
            lock (_sync)
            {
                return (_udp?.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0;
            }
        }
    }

    public Task StartAsync(DhcpServerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            if (_udp != null) return Task.CompletedTask;

            var leases = new DhcpLeaseManager(settings);
            var udp = new UdpClient();
            var cts = new CancellationTokenSource();
            try
            {
                udp.EnableBroadcast = true;
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, _listenPort));
                _udp = udp;
                _cts = cts;
                _loopTask = Task.Run(() => LoopAsync(udp, cts.Token, settings, leases));
            }
            catch
            {
                udp.Dispose();
                cts.Dispose();
                _udp = null;
                _cts = null;
                _loopTask = null;
                throw;
            }
        }
        _logger.Info($"DHCP start: {settings.ServerIp} pool={settings.PoolStart}-{settings.PoolEnd}");
        return Task.CompletedTask;
    }

    public void Stop()
    {
        UdpClient? udp;
        CancellationTokenSource? cts;
        Task? loop;
        lock (_sync)
        {
            udp = _udp;
            cts = _cts;
            loop = _loopTask;
            _udp = null;
            _cts = null;
            _loopTask = null;
        }
        if (udp == null) return;
        cts?.Cancel();
        udp.Dispose();
        if (cts != null)
        {
            _ = (loop ?? Task.CompletedTask).ContinueWith(_ => cts.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        _logger.Info("DHCP stop");
    }

    private async Task LoopAsync(UdpClient udp, CancellationToken ct, DhcpServerSettings settings, DhcpLeaseManager leases)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var received = await udp.ReceiveAsync(ct);
                _logger.Info($"DHCP UDP received: from={received.RemoteEndPoint} bytes={received.Buffer.Length}");
                var packet = DhcpPacketParser.Parse(received.Buffer);
                _logger.Info($"DHCP packet: type={packet.MessageType} mac={packet.MacAddress} host={packet.Hostname}");
                if (packet.MessageType is DhcpMessageType.Discover or DhcpMessageType.Request)
                {
                    var lease = leases.Allocate(packet.MacAddress, packet.Hostname);
                    var type = packet.MessageType == DhcpMessageType.Discover ? DhcpMessageType.Offer : DhcpMessageType.Ack;
                    lease.Time = DateTime.Now;
                    lease.Status = type.ToString();
                    var reply = DhcpPacketParser.BuildReply(packet, settings, IPAddress.Parse(lease.IpAddress), type);
                    var replyTarget = _replyAddress ?? IPAddress.Broadcast;
                    await SendReplyAsync(udp, reply, replyTarget, ct);
                    var subnetBroadcast = IpNetwork.BroadcastAddress(settings.ServerIp, settings.SubnetMask);
                    if (_replyAddress == null && !subnetBroadcast.Equals(IPAddress.Broadcast))
                    {
                        await SendReplyAsync(udp, reply, subnetBroadcast, ct);
                    }
                    _logger.Info($"DHCP {type}: {lease.MacAddress} {lease.IpAddress} {lease.Hostname} targets=255.255.255.255,{subnetBroadcast}");
                    LeaseChanged?.Invoke(lease);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                _logger.Error("DHCP loop failed", ex);
            }
        }
    }

    private async Task SendReplyAsync(UdpClient udp, byte[] reply, IPAddress target, CancellationToken ct)
    {
        var endpoint = new IPEndPoint(target, _clientPort);
        await udp.SendAsync(reply, endpoint, ct);
        _logger.Info($"DHCP reply sent: target={endpoint}");
    }

    public void Dispose()
    {
        Stop();
    }
}
