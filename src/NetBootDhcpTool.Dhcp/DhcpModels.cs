using System.Net;
using System.ComponentModel;
using System.Runtime.CompilerServices;

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
    private DateTime _time = DateTime.Now;
    private string _macAddress = "";
    private string _ipAddress = "";
    private string _hostname = "";
    private DateTime _leaseStart = DateTime.Now;
    private DateTime _leaseEnd = DateTime.Now.AddHours(1);
    private string _status = "Assigned";
    private bool _httpOk;
    private bool _httpsOk;
    private long _pingLatencyMs = -1;
    private string _remark = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    public DateTime Time { get => _time; set => Set(ref _time, value); }
    public string MacAddress { get => _macAddress; set => Set(ref _macAddress, value); }
    public string IpAddress { get => _ipAddress; set => Set(ref _ipAddress, value); }
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
    public string StatusText => Status switch
    {
        "Online" => "Online / 在线",
        "Offline" => "Offline / 离线",
        "Assigned" => "Assigned / 已分配",
        _ => $"{Status} / 状态"
    };
    public string StatusHelp =>
        "Assigned / 已分配: DHCP lease was assigned, waiting for ping confirmation.\n" +
        "Online / 在线: Device is reachable by ping.\n" +
        "Offline / 离线: Lease exists but ping failed. Check client power, cable, adapter state, and IP configuration.";
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
