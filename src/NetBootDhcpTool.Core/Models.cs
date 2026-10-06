using System.Text.Json.Serialization;

namespace NetBootDhcpTool.Core;

public sealed class AppSettings
{
    public string Language { get; set; } = "auto";
    public int PingConcurrency { get; set; } = 64;
    public int PingTimeoutMs { get; set; } = 800;
    public int HttpTimeoutMs { get; set; } = 1000;
    public bool AllowDhcpOnAdapterWithGateway { get; set; }
    public bool AllowRestartOnAnyAdapter { get; set; } = true;
    public bool AllowMacChangeOnAnyAdapter { get; set; } = true;
    public bool RestoreIpOnDhcpStop { get; set; } = true;
    public bool DetectExistingDhcpBeforeStart { get; set; } = true;
    public bool DarkTheme { get; set; }
    public bool SafetyOnboardingCompleted { get; set; }
    public bool RedactSupportPackage { get; set; } = true;
    public double WindowWidth { get; set; } = 1240;
    public double WindowHeight { get; set; } = 800;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public string WindowState { get; set; } = "Normal";
    public string LastTab { get; set; } = "TabDhcp";
    public bool LogPanelExpanded { get; set; }
    public bool LogAutoScroll { get; set; } = true;
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
    [JsonIgnore]
    public string Password { get; set; } = "";
    /// <summary>
    /// Plaintext password for a manufacturer-published factory-default preset.
    /// Personal favorites continue to use ProtectedPassword on disk.
    /// </summary>
    public string PublicPassword { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";
    public bool IsPublicDefault { get; set; }
    public bool PreferHttps { get; set; }
    public string MemoryText { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public string AdapterMac { get; set; } = "";
    public string LocalIp { get; set; } = "";
    public string SubnetMask { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string Dns { get; set; } = "";
    public string TargetIp { get; set; } = "";
    public List<FavoriteField> CustomFields { get; set; } = [];
    [JsonIgnore]
    public string CustomFieldsSummary => string.Join("; ", CustomFields.Where(x => !string.IsNullOrWhiteSpace(x.Name)).Select(x => string.IsNullOrWhiteSpace(x.Value) ? x.Name : $"{x.Name}={x.Value}"));
    [JsonIgnore]
    public bool PasswordUnavailable { get; set; }
    [JsonIgnore]
    public bool HasPassword => !string.IsNullOrWhiteSpace(Password) || !string.IsNullOrWhiteSpace(ProtectedPassword);
    [JsonIgnore]
    public bool HasUsablePassword => !PasswordUnavailable && !string.IsNullOrWhiteSpace(Password);
    [JsonIgnore]
    public string PasswordDisplay => PasswordUnavailable ? "Unavailable / 需重新输入" : HasUsablePassword ? "••••••" : "";
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
    public string AdapterId { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public string AdapterMac { get; set; } = "";
    public DateTime CapturedAt { get; set; } = DateTime.Now;
    public bool DhcpEnabled { get; set; }
    public string IpAddress { get; set; } = "";
    public int PrefixLength { get; set; } = 24;
    public string Gateway { get; set; } = "";
    public List<string> Dns { get; set; } = [];
    public AdapterDnsMode DnsMode { get; set; }
    public bool? AdapterEnabled { get; set; }
    public List<AdapterIpv4AddressSnapshot> Addresses { get; set; } = [];
    public List<AdapterRouteSnapshot> Routes { get; set; } = [];
    public bool AutomaticMetric { get; set; } = true;
    public int InterfaceMetric { get; set; }
}

public enum AdapterDnsMode
{
    Unknown,
    Automatic,
    Static
}

public sealed class AdapterIpv4AddressSnapshot
{
    public string IpAddress { get; set; } = "";
    public int PrefixLength { get; set; } = 24;
    public bool SkipAsSource { get; set; }
}

public sealed class AdapterRouteSnapshot
{
    public string DestinationPrefix { get; set; } = "0.0.0.0/0";
    public string NextHop { get; set; } = "0.0.0.0";
    public int RouteMetric { get; set; } = 0;
    public string PolicyStore { get; set; } = "ActiveStore";
    public string Protocol { get; set; } = "";
}

public sealed class StaticRouteRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DestinationPrefix { get; set; } = "";
    public string AddressFamily { get; set; } = "";
    public string AdapterId { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public string AdapterMac { get; set; } = "";
    public string NextHop { get; set; } = "";
    public int RouteMetric { get; set; } = 1;
    public int InterfaceMetric { get; set; }
    [JsonIgnore] public string Status { get; set; } = "Not applied / 未应用";
    [JsonIgnore] public string NextHopDisplay => string.IsNullOrWhiteSpace(NextHop) ? "Direct / 直连" : NextHop;
    [JsonIgnore] public string AddressFamilyDisplay => string.IsNullOrWhiteSpace(AddressFamily)
        ? (DestinationPrefix.Contains(':') ? "IPv6" : "IPv4")
        : AddressFamily.Equals("InterNetwork", StringComparison.OrdinalIgnoreCase) || AddressFamily.Equals("IPv4", StringComparison.OrdinalIgnoreCase)
            ? "IPv4"
            : AddressFamily.Equals("InterNetworkV6", StringComparison.OrdinalIgnoreCase) || AddressFamily.Equals("IPv6", StringComparison.OrdinalIgnoreCase)
                ? "IPv6"
                : AddressFamily;
    [JsonIgnore] public string PriorityDisplay => RouteMetric <= 1 ? "Auto / 自动" : RouteMetric.ToString();
    [JsonIgnore] public string EffectiveMetricDisplay => InterfaceMetric > 0 ? $"{RouteMetric} + {InterfaceMetric} = {RouteMetric + InterfaceMetric}" : RouteMetric.ToString();
}

public sealed class AppliedStaticRoute
{
    public string RuleId { get; set; } = "";
    public string DestinationPrefix { get; set; } = "";
    public string AddressFamily { get; set; } = "IPv4";
    public string AdapterId { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public string AdapterMac { get; set; } = "";
    public int InterfaceIndex { get; set; }
    public string NextHop { get; set; } = "0.0.0.0";
    public int RouteMetric { get; set; }
    public string PolicyStore { get; set; } = "ActiveStore";
    public string InstanceId { get; set; } = "";
    // Missing in pre-intent journal formats defaults to verified ownership for compatibility.
    public bool OwnershipVerified { get; set; } = true;
}

public sealed class OperationHistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Time { get; set; } = DateTime.Now;
    public string Type { get; set; } = "";
    public string Scope { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
    public long DurationMs { get; set; }
    public bool RollbackAvailable { get; set; }

    [JsonIgnore]
    public string DurationDisplay => DurationMs <= 0 ? "-" : $"{DurationMs} ms";

    [JsonIgnore]
    public string RollbackDisplay => RollbackAvailable ? "Available / 可回滚" : "-";
}

public sealed class NetworkProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public DefaultDhcpSettings Dhcp { get; set; } = new();
    public string ManualIp { get; set; } = "";
    public string ManualMask { get; set; } = "";
    public string ManualTargetIp { get; set; } = "";
    public List<StaticRouteRule> Routes { get; set; } = [];
    public List<AdapterAddressDraft> Addresses { get; set; } = [];
    public bool DhcpPreserveAddresses { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public string RouteSummary => Routes.Count == 0 ? "No routes / 无路由" : $"{Routes.Count} route(s) / {Routes.Count} 条路由";

    [JsonIgnore]
    public string UpdatedDisplay => UpdatedAt.ToString("yyyy-MM-dd HH:mm");
}

public sealed class AdapterMacBackup
{
    public string InterfaceIndex { get; set; } = "";
    public string AdapterId { get; set; } = "";
    public string AdapterName { get; set; } = "";
    public string OriginalMacAddress { get; set; } = "";
    public bool RestoreOnExit { get; set; } = true;
    public DateTime CapturedAt { get; set; } = DateTime.Now;
}

public sealed class ScanResult
{
    private string _statusOverride = "";
    [JsonIgnore]
    public PeerConnectivity Connectivity { get; } = new();
    public string ConfiguredLocalIp { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public bool PingOk { get; set; }
    public long LatencyMs { get; set; }
    public string MacAddress { get; set; } = "";
    public string Hostname { get; set; } = "";
    public bool HttpOk { get; set; }
    public bool HttpsOk { get; set; }
    public string StatusText => Connectivity.LastCheckedAt.HasValue || Connectivity.State == "Stopped"
        ? Connectivity.Text : !string.IsNullOrWhiteSpace(_statusOverride) ? _statusOverride : PingOk ? "Connected / 已联通" : "Offline / 已离线";
    public string WebText => HttpOk && HttpsOk ? "HTTP, HTTPS" : HttpOk ? "HTTP" : HttpsOk ? "HTTPS" : "";
    public DateTime LastSeen { get; set; } = DateTime.MinValue;
    public string LastSeenText => LastSeen == DateTime.MinValue ? "" : LastSeen.ToString("MM-dd HH:mm:ss");
    public string Remark { get; set; } = "";
    public void SetWaitingStatus() => _statusOverride = "Checking / 待探测";
    public void ClearStatusOverride() => _statusOverride = "";
}

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(List<FavoriteConfig>))]
[JsonSerializable(typeof(List<FavoriteField>))]
[JsonSerializable(typeof(List<AppliedStaticRoute>))]
[JsonSerializable(typeof(List<OperationHistoryItem>))]
[JsonSerializable(typeof(List<NetworkProfile>))]
[JsonSerializable(typeof(List<AdapterMacBackup>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
public partial class NetBootJsonContext : JsonSerializerContext;
