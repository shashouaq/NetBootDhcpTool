namespace NetBootDhcpTool.Core;

public interface ILogger
{
    event Action<string>? LineWritten;
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
}

public sealed class FileLogger : ILogger
{
    private readonly AppPaths _paths;
    private readonly object _sync = new();

    public FileLogger(AppPaths paths)
    {
        _paths = paths;
        _paths.Ensure();
    }

    public event Action<string>? LineWritten;

    public void Info(string message) => Write("INFO", message, null);
    public void Warn(string message) => Write("WARN", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        message = Explain(message);
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (exception != null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_sync)
        {
            var path = Path.Combine(_paths.LogsDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
            EnsureUtf8Bom(path);
            File.AppendAllText(path, line + Environment.NewLine, System.Text.Encoding.UTF8);
        }

        LineWritten?.Invoke(line);
    }

    private static void EnsureUtf8Bom(string path)
    {
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "", new System.Text.UTF8Encoding(true));
    }

    private static string Explain(string message)
    {
        if (message.Contains(" / ", StringComparison.Ordinal)) return message;

        var pairs = new (string Prefix, string Text)[]
        {
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
        };

        foreach (var (prefix, text) in pairs)
        {
            if (message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return text + message[prefix.Length..];
            }
        }

        if (message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return message + " / 超时，请检查客户端网卡、网线、交换机、IP 获取状态和本机防火墙";
        }

        if (message.Contains("error", StringComparison.OrdinalIgnoreCase) || message.Contains("failed", StringComparison.OrdinalIgnoreCase))
        {
            return message + " / 失败，请查看后续异常和操作系统返回信息";
        }

        return message;
    }
}
