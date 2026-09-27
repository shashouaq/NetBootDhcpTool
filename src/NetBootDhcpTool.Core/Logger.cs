using System.Diagnostics;
using System.Threading.Channels;

namespace NetBootDhcpTool.Core;

public interface ILogger
{
    event Action<string>? LineWritten;
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

public sealed class FileLogger : ILogger, IDisposable
{
    private static readonly (string Prefix, string Text)[] Explanations =
    [
        ("Application start", "Application start / 程序启动"),
        ("Application exit", "Application exit / 程序退出"),
        ("Administrator=", "Administrator privilege / 管理员权限 = "),
        ("MainWindow ready", "Main window ready / 主窗口就绪"),
        ("UI adapter list loaded", "UI adapter list loaded / 界面网卡列表已加载"),
        ("Adapter:", "Adapter / 网卡:"),
        ("Adapter selected", "Adapter selected / 已选择网卡"),
        ("PowerShell action", "PowerShell action / PowerShell 操作"),
        ("DHCP start", "DHCP start / DHCP 服务启动"),
        ("DHCP stop", "DHCP stop / DHCP 服务停止"),
        ("DHCP UDP received", "DHCP UDP received / 收到 DHCP UDP 数据"),
        ("DHCP packet", "DHCP packet / DHCP 报文"),
        ("DHCP Offer", "DHCP Offer / 已发送 DHCP 地址提供"),
        ("DHCP Ack", "DHCP Ack / 已确认 DHCP 地址分配"),
        ("DHCP reply sent", "DHCP reply sent / DHCP 回复已发出"),
        ("Existing DHCP detection sent Discover", "Existing DHCP detection sent Discover / 已发送现有 DHCP 探测包"),
        ("Existing DHCP detection timeout", "Existing DHCP detection timeout / 现有 DHCP 探测超时"),
        ("UI lease added", "UI lease added / 界面新增租约"),
        ("UI lease updated", "UI lease updated / 界面刷新租约"),
        ("UI lease probe", "UI lease probe / 租约连通性探测"),
        ("Favorite added", "Favorite added / 已加入收藏"),
        ("Favorite manually added", "Favorite manually added / 手动新增收藏"),
        ("Favorite saved", "Favorite saved / 已保存收藏"),
        ("Favorite deleted", "Favorite deleted / 已删除收藏"),
        ("Settings saved", "Settings saved / 设置已保存"),
        ("WLAN readonly state", "WLAN readonly state / WLAN 只读状态"),
        ("Isolated network confirmation missing", "Isolated network confirmation missing / 未勾选隔离调试网络确认"),
        ("Captured adapter IPv4", "Captured adapter IPv4 / 已记录网卡原始 IPv4 配置"),
        ("Adapter restored to original config", "Adapter restored to original config / 已恢复网卡原始配置"),
        ("Window closing cleanup completed", "Window closing cleanup completed / 窗口关闭清理完成")
    ];

    private readonly object _sync = new();
    private readonly Stream _stream;
    private readonly StreamWriter _writer;
    private readonly Channel<string> _notifications = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });
    private readonly Task _notificationPump;
    private bool _disposed;

    public FileLogger(AppPaths paths) : this(paths, streamFactory: null) { }

    internal FileLogger(AppPaths paths, Func<string, Stream>? streamFactory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.Ensure();
        SessionLogPath = Path.Combine(paths.LogsDirectory, $"run-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
        // Support-package readers may open the active log for read while this handle keeps writing.
        _stream = streamFactory?.Invoke(SessionLogPath)
            ?? new FileStream(SessionLogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
        _writer = new StreamWriter(_stream, new System.Text.UTF8Encoding(true), 4096, leaveOpen: true) { AutoFlush = true };
        _notificationPump = Task.Run(DispatchNotificationsAsync);
    }

    public event Action<string>? LineWritten;
    public string SessionLogPath { get; }

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message) => Write("WARN", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public void Flush()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.Flush();
            _stream.Flush();
        }
    }

    public void Dispose()
    {
        Exception? failure = null;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _writer.Flush();
                _stream.Flush();
            }
            catch (Exception ex) { failure = ex; }
            try { _writer.Dispose(); }
            catch (Exception ex) { failure = Combine(failure, ex); }
            try { _stream.Dispose(); }
            catch (Exception ex) { failure = Combine(failure, ex); }
            _notifications.Writer.TryComplete();
        }

        try { _notificationPump.GetAwaiter().GetResult(); }
        catch (Exception ex) { failure = Combine(failure, ex); }
        if (failure != null)
        {
            Trace.WriteLine($"Log writer shutdown failed: {failure}");
            throw new IOException("The session log could not be flushed cleanly; the writer was closed.", failure);
        }
    }

    private void Write(string level, string message, Exception? exception)
    {
        ArgumentNullException.ThrowIfNull(message);
        message = Explain(message);
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (exception != null) line += Environment.NewLine + exception;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _writer.WriteLine(line);
            // Enqueue while holding the file lock so observers see precisely the same order as the file.
            if (!_notifications.Writer.TryWrite(line))
                Trace.WriteLine("Log observer notification could not be queued.");
        }
    }

    private async Task DispatchNotificationsAsync()
    {
        await foreach (var line in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var handlers = LineWritten;
            if (handlers == null) continue;
            foreach (Action<string> handler in handlers.GetInvocationList())
            {
                try { handler(line); }
                catch (Exception ex) { Trace.WriteLine($"Log observer failed: {ex}"); }
            }
        }
    }

    private static string Explain(string message)
    {
        if (message.Contains(" / ", StringComparison.Ordinal)) return message;
        foreach (var (prefix, text) in Explanations)
        {
            if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return text + message[prefix.Length..];
        }
        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return message + " / 超时，请检查客户端网卡、网线、交换机、IP 获取状态和本机防火墙";
        if (message.Contains("error", StringComparison.OrdinalIgnoreCase) || message.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return message + " / 失败，请查看后续异常和操作系统返回信息";
        return message;
    }

    private static Exception Combine(Exception? first, Exception next) => first == null
        ? next
        : new AggregateException(first, next);
}
