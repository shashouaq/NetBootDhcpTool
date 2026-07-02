using System.Net;
using System.Net.Sockets;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Dhcp;

public sealed class DhcpServer : IDisposable
{
    private readonly ILogger _logger;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private DhcpServerSettings? _settings;
    private DhcpLeaseManager? _leases;

    public DhcpServer(ILogger logger)
    {
        _logger = logger;
    }

    public event Action<DhcpLease>? LeaseChanged;
    public bool IsRunning => _udp != null;

    public Task StartAsync(DhcpServerSettings settings)
    {
        if (_udp != null) return Task.CompletedTask;
        _settings = settings;
        _leases = new DhcpLeaseManager(settings);
        _cts = new CancellationTokenSource();
        _udp = new UdpClient();
        _udp.EnableBroadcast = true;
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 67));
        _logger.Info($"DHCP start: {settings.ServerIp} pool={settings.PoolStart}-{settings.PoolEnd}");
        _ = Task.Run(() => LoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (_udp == null) return;
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _logger.Info("DHCP stop");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null && _settings != null && _leases != null)
        {
            try
            {
                var received = await _udp.ReceiveAsync(ct);
                _logger.Info($"DHCP UDP received: from={received.RemoteEndPoint} bytes={received.Buffer.Length}");
                var packet = DhcpPacketParser.Parse(received.Buffer);
                _logger.Info($"DHCP packet: type={packet.MessageType} mac={packet.MacAddress} host={packet.Hostname}");
                if (packet.MessageType is DhcpMessageType.Discover or DhcpMessageType.Request)
                {
                    var lease = _leases.Allocate(packet.MacAddress, packet.Hostname);
                    var type = packet.MessageType == DhcpMessageType.Discover ? DhcpMessageType.Offer : DhcpMessageType.Ack;
                    lease.Time = DateTime.Now;
                    lease.Status = type.ToString();
                    var reply = DhcpPacketParser.BuildReply(packet, _settings, IPAddress.Parse(lease.IpAddress), type);
                    var subnetBroadcast = IpNetwork.BroadcastAddress(_settings.ServerIp, _settings.SubnetMask);
                    await SendReplyAsync(reply, IPAddress.Broadcast, ct);
                    if (!subnetBroadcast.Equals(IPAddress.Broadcast))
                    {
                        await SendReplyAsync(reply, subnetBroadcast, ct);
                    }
                    _logger.Info($"DHCP {type}: {lease.MacAddress} {lease.IpAddress} {lease.Hostname} targets=255.255.255.255,{subnetBroadcast}");
                    LeaseChanged?.Invoke(lease);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Error("DHCP loop failed", ex);
            }
        }
    }

    private async Task SendReplyAsync(byte[] reply, IPAddress target, CancellationToken ct)
    {
        if (_udp == null) return;
        var endpoint = new IPEndPoint(target, 68);
        await _udp.SendAsync(reply, endpoint, ct);
        _logger.Info($"DHCP reply sent: target={endpoint}");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
