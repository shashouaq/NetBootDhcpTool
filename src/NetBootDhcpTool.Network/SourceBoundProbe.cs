using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Network;

public sealed record ProbeSource(string AdapterId, int InterfaceIndex, IPAddress LocalAddress)
{
    public void Validate(IPAddress target)
    {
        if (LocalAddress.AddressFamily != AddressFamily.InterNetwork || target.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("IPv4 source/target required / 源地址和目标必须为 IPv4");
        var adapter = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(x => x.Id.Equals(AdapterId, StringComparison.OrdinalIgnoreCase));
        if (adapter is null || adapter.OperationalStatus != OperationalStatus.Up
            || adapter.GetIPProperties().GetIPv4Properties()?.Index != InterfaceIndex
            || !adapter.GetIPProperties().UnicastAddresses.Any(x => x.Address.Equals(LocalAddress) && x.DuplicateAddressDetectionState == DuplicateAddressDetectionState.Preferred))
            throw new InvalidOperationException("Probe source is unavailable or changed / 探测源地址不可用或已变化");
        var sockaddr = new byte[16]; sockaddr[0] = 2;
        target.GetAddressBytes().CopyTo(sockaddr, 4);
        var error = GetBestInterfaceEx(sockaddr, out var routeIndex);
        if (error != 0 || routeIndex != InterfaceIndex)
            throw new InvalidOperationException("Target route uses another interface / 目标路由指向其他网卡");
    }

    public HttpMessageHandler CreateHttpHandler() => new SocketsHttpHandler
    {
        UseProxy = false, UseCookies = false, AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromSeconds(15),
        ConnectCallback = async (context, ct) =>
        {
            var target = IPAddress.Parse(context.DnsEndPoint.Host);
            Validate(target);
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(InterfaceIndex));
                socket.Bind(new IPEndPoint(LocalAddress, 0));
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    };

    public async Task<long?> PingAsync(IPAddress target, int timeoutMs, CancellationToken ct)
    {
        Validate(target);
        ct.ThrowIfCancellationRequested();
        using var handle = IcmpCreateFile();
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        using var completed = new ManualResetEvent(false);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = ThreadPool.RegisterWaitForSingleObject(completed, (_, _) => completion.TrySetResult(), null, -1, true);
        var request = Marshal.AllocHGlobal(16);
        var reply = Marshal.AllocHGlobal(256);
        try
        {
            Marshal.Copy(new byte[16], 0, request, 16);
            var count = IcmpSendEcho2Ex(handle, completed.SafeWaitHandle, IntPtr.Zero, IntPtr.Zero,
                BitConverter.ToUInt32(LocalAddress.GetAddressBytes()), BitConverter.ToUInt32(target.GetAddressBytes()),
                request, 16, IntPtr.Zero, reply, 256, (uint)Math.Clamp(timeoutMs, 1, 3000));
            var error = count == 0 ? Marshal.GetLastWin32Error() : 0;
            // Native buffers must remain alive until completion, including when the caller cancels.
            if (count > 0 || error == 997) await completion.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            Validate(target);
            return (count > 0 || error == 997) && Marshal.ReadInt32(reply, 4) == 0
                ? unchecked((uint)Marshal.ReadInt32(reply, 8)) : null;
        }
        finally { wait.Unregister(null); Marshal.FreeHGlobal(request); Marshal.FreeHGlobal(reply); }
    }

    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterfaceEx(byte[] destination, out int bestInterface);
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern IcmpHandle IcmpCreateFile();
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint IcmpSendEcho2Ex(IcmpHandle handle, SafeWaitHandle evt, IntPtr apc, IntPtr context,
        uint source, uint destination, IntPtr request, ushort size, IntPtr options, IntPtr reply, uint replySize, uint timeout);
    [DllImport("iphlpapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IcmpCloseHandle(IntPtr handle);
    private sealed class IcmpHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public IcmpHandle() : base(true) { }
        protected override bool ReleaseHandle() => IcmpCloseHandle(handle);
    }
}

public sealed class SourceBoundProbe : IScanTargetProbe, IAsyncDisposable
{
    private readonly ProbeSource _source;
    private readonly HttpProbeService _web;
    public SourceBoundProbe(ProbeSource source) { _source = source; _web = new HttpProbeService(source.CreateHttpHandler); }
    public async Task<ScanResult?> ProbeAsync(IPAddress ip, int pingTimeoutMs, int httpTimeoutMs, CancellationToken ct)
    {
        _source.Validate(ip);
        var pingTask = _source.PingAsync(ip, pingTimeoutMs, ct);
        var webTask = _web.ProbeAsync(ip.ToString(), httpTimeoutMs, ct);
        await Task.WhenAll(pingTask, webTask).ConfigureAwait(false);
        _source.Validate(ip);
        var latency = await pingTask.ConfigureAwait(false);
        var web = await webTask.ConfigureAwait(false);
        if (!latency.HasValue && !web.http && !web.https) return null;
        var result = new ScanResult { ConfiguredLocalIp = _source.LocalAddress.ToString(), IpAddress = ip.ToString(),
            PingOk = latency.HasValue, LatencyMs = latency ?? -1, HttpOk = web.http, HttpsOk = web.https, LastSeen = DateTime.Now };
        result.Connectivity.ObserveReachability(result.LatencyMs, web.http || web.https, result.LastSeen);
        return result;
    }
    public ValueTask DisposeAsync() => _web.DisposeAsync();
}
