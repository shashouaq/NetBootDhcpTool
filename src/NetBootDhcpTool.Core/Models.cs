using System.Text.Json.Serialization;

namespace NetBootDhcpTool.Core;

public sealed class AppSettings
{
    public string Language { get; set; } = "auto";
    public int PingConcurrency { get; set; } = 64;
    public int PingTimeoutMs { get; set; } = 800;
    public int HttpTimeoutMs { get; set; } = 1000;
    public bool AllowDhcpOnWifi { get; set; }
    public bool AllowDhcpOnAdapterWithGateway { get; set; }
    public bool RestoreIpOnDhcpStop { get; set; } = true;
    public bool DetectExistingDhcpBeforeStart { get; set; } = true;
    public DefaultDhcpSettings DefaultDhcp { get; set; } = new();
}

public sealed class DefaultDhcpSettings
{
    public string ServerIp { get; set; } = "192.168.100.1";
    public string SubnetMask { get; set; } = "255.255.255.0";
    public string PoolStart { get; set; } = "192.168.100.100";
    public string PoolEnd { get; set; } = "192.168.100.200";
    public string Gateway { get; set; } = "";
    public string Dns { get; set; } = "";
    public int LeaseSeconds { get; set; } = 3600;
}

public sealed class FavoriteConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string DeviceNumber { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string RemarkName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string MemoryText { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public string AdapterMac { get; set; } = "";
    public string LocalIp { get; set; } = "";
    public string SubnetMask { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string Dns { get; set; } = "";
    public string TargetIp { get; set; } = "";
    public List<FavoriteField> CustomFields { get; set; } = [];
    public string CustomFieldsSummary => string.Join("; ", CustomFields.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => string.IsNullOrWhiteSpace(x.Value) ? x.Name : $"{x.Name}={x.Value}"));
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public DateTime? LastUsedAt { get; set; }
}

public sealed class FavoriteField
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class AdapterIpHistoryItem
{
    public string CurrentIp { get; set; } = "";
    public string PreviousIp { get; set; } = "";
    public DateTime ChangedAt { get; set; } = DateTime.Now;
    public string DisplayText => $"{ChangedAt:HH:mm:ss}  {PreviousIp}  →  {CurrentIp}";
}

public sealed class AdapterConfigBackup
{
    public string InterfaceIndex { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public bool DhcpEnabled { get; set; }
    public string IpAddress { get; set; } = "";
    public int PrefixLength { get; set; } = 24;
    public string Gateway { get; set; } = "";
    public List<string> Dns { get; set; } = [];
    public bool AutomaticMetric { get; set; } = true;
    public int InterfaceMetric { get; set; }
}

public sealed class OperationHistoryItem
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Type { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class ScanResult
{
    private string _statusOverride = "";
    public string ConfiguredLocalIp { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public bool PingOk { get; set; }
    public long LatencyMs { get; set; }
    public string MacAddress { get; set; } = "";
    public string Hostname { get; set; } = "";
    public bool HttpOk { get; set; }
    public bool HttpsOk { get; set; }
    public string StatusText => !string.IsNullOrWhiteSpace(_statusOverride) ? _statusOverride : PingOk ? "Online / 在线" : "Offline / 离线";
    public string WebText => HttpOk && HttpsOk ? "HTTP, HTTPS" : HttpOk ? "HTTP" : HttpsOk ? "HTTPS" : "";
    public DateTime LastSeen { get; set; } = DateTime.Now;
    public string Remark { get; set; } = "";
    public void SetWaitingStatus() => _statusOverride = "Assigned / 已配置";
    public void ClearStatusOverride() => _statusOverride = "";
}

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<FavoriteConfig>))]
[JsonSerializable(typeof(List<FavoriteField>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class NetBootJsonContext : JsonSerializerContext;
