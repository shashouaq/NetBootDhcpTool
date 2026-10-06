using System.Net;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Dhcp;

public enum DhcpMessageType : byte
{
    Discover = 1,
    Offer = 2,
    Request = 3,
    Decline = 4,
    Ack = 5,
    Nak = 6,
    Release = 7,
    Inform = 8
}

public sealed class DhcpServerSettings
{
    public string AdapterId { get; set; } = "";
    public int InterfaceIndex { get; set; }
    public IPAddress ServerIp { get; set; } = IPAddress.Parse("192.168.100.1");
    public IPAddress SubnetMask { get; set; } = IPAddress.Parse("255.255.255.0");
    public IPAddress PoolStart { get; set; } = IPAddress.Parse("192.168.100.100");
    public IPAddress PoolEnd { get; set; } = IPAddress.Parse("192.168.100.200");
    public IPAddress? Gateway { get; set; }
    public IPAddress? Dns { get; set; }
    public int LeaseSeconds { get; set; } = 3600;
}

public sealed class DhcpLease : INotifyPropertyChanged
{
    private DateTime _time = DateTime.MinValue;
    private string _macAddress = "";
    private string _ipAddress = "";
    private string _clientKey = "";
    private string _clientIdentifier = "";
    private string _hostname = "";
    private DateTime _leaseStart = DateTime.Now;
    private DateTime _leaseEnd = DateTime.Now.AddHours(1);
    private string _status = "Assigned";
    private bool _isActiveLease = true;
    private bool _isCurrentSession = true;
    private string _sessionId = "";
    private bool _httpOk;
    private bool _httpsOk;
    private long _pingLatencyMs = -1;
    private string _remark = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    [JsonIgnore]
    public PeerConnectivity Connectivity { get; } = new();
    public DateTime Time { get => _time; set => Set(ref _time, value); }
    public string MacAddress { get => _macAddress; set => Set(ref _macAddress, value); }
    public string IpAddress { get => _ipAddress; set => Set(ref _ipAddress, value); }
    public string ClientKey { get => _clientKey; set => Set(ref _clientKey, value); }
    public string ClientIdentifier { get => _clientIdentifier; set => Set(ref _clientIdentifier, value); }
    public string Hostname { get => _hostname; set => Set(ref _hostname, value); }
    public DateTime LeaseStart { get => _leaseStart; set => Set(ref _leaseStart, value); }
    public DateTime LeaseEnd { get => _leaseEnd; set => Set(ref _leaseEnd, value); }
    public string Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusHelp));
        }
    }
    public bool IsActiveLease { get => _isActiveLease; set { if (Set(ref _isActiveLease, value) && !value) Connectivity.Stop(DateTime.Now); } }
    public bool IsCurrentSession { get => _isCurrentSession; set { if (Set(ref _isCurrentSession, value)) { OnPropertyChanged(nameof(SessionText)); if (!value) Connectivity.Stop(DateTime.Now); } } }
    public string SessionId { get => _sessionId; set => Set(ref _sessionId, value); }
    public string SessionText => IsCurrentSession ? "Current / 当前会话" : "History / 历史";
    public string StatusText => Status switch
    {
        "Online" => "Online / 在线",
        "Offline" => "Offline / 离线",
        "Assigned" => "Assigned / 已分配",
        "Released" => "Released / 已释放",
        "Declined" => "Declined / 地址冲突",
        "Expired" => "Expired / 已过期",
        _ => $"{Status} / 状态"
    };
    public string StatusHelp =>
        "Assigned / 已分配: Address assignment does not prove connectivity; see the Connectivity column / 地址已分配，当前连通情况请看连通状态列。\n" +
        "Released / 已释放: Client released this binding.\n" +
        "Declined / 地址冲突: Client reported an address conflict and the address is quarantined.\n" +
        "Expired / 已过期: Lease time ended. Ping reachability is shown separately.";
    public bool HttpOk { get => _httpOk; set => Set(ref _httpOk, value); }
    public bool HttpsOk { get => _httpsOk; set => Set(ref _httpsOk, value); }
    public long PingLatencyMs { get => _pingLatencyMs; set => Set(ref _pingLatencyMs, value); }
    public string Remark { get => _remark; set => Set(ref _remark, value); }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class DhcpLeaseBinding
{
    public string ClientKey { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string ClientIdentifierHex { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Hostname { get; set; } = "";
    public DateTimeOffset LeaseStart { get; set; }
    public DateTimeOffset LeaseEnd { get; set; }
}

public sealed class DhcpDeclinedAddress
{
    public string IpAddress { get; set; } = "";
    public string ClientKey { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class DhcpLeaseTableSnapshot
{
    public List<DhcpLeaseBinding> Bindings { get; set; } = [];
    public List<DhcpDeclinedAddress> DeclinedAddresses { get; set; } = [];
}

public sealed class DhcpLeaseScopeState
{
    public string AdapterId { get; set; } = "";
    public string ServerIp { get; set; } = "";
    public string SubnetMask { get; set; } = "";
    public string PoolStart { get; set; } = "";
    public string PoolEnd { get; set; } = "";
    public List<DhcpLeaseBinding> Bindings { get; set; } = [];
    public List<DhcpDeclinedAddress> DeclinedAddresses { get; set; } = [];
}

public sealed class DhcpLeaseJournal
{
    public List<DhcpLeaseScopeState> Scopes { get; set; } = [];
}

public sealed record DhcpLeaseDecision(DhcpMessageType? ResponseType, IPAddress? Address, DhcpLease? ChangedLease = null)
{
    public static DhcpLeaseDecision Ignore { get; } = new(null, null);
}
