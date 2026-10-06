using System.ComponentModel;

namespace NetBootDhcpTool.Core;

/// <summary>Fresh probe observations; independent of address assignment and cached web details.</summary>
public sealed class PeerConnectivity : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public string State { get; private set; } = "Pending";
    public DateTime? LastCheckedAt { get; private set; }
    public DateTime? ChangedAt { get; private set; }
    public string Text => State switch
    {
        "Online" => "Connected / 已联通",
        "Offline" => "Offline / 已离线",
        "Stopped" => "Stopped / 已停止监测",
        _ => "Checking / 待探测"
    };
    public string Help => "Connectivity uses fresh probe responses. Source-bound multi-IP probes also accept a fresh HTTP/HTTPS response; cached web details never prove current connectivity.\n连通状态使用本次探测应答；多 IP 指定源地址探测也认可本次 HTTP/HTTPS 应答，历史网页结果不作为当前在线依据。";

    public void Observe(long latencyMs, DateTime checkedAt)
        => ObserveReachability(latencyMs, false, checkedAt);

    public void ObserveReachability(long latencyMs, bool freshWebResponse, DateTime checkedAt)
    {
        if (State == "Stopped") return;
        LastCheckedAt = checkedAt;
        ChangeState(latencyMs >= 0 || freshWebResponse ? "Online" : "Offline", checkedAt);
        PropertyChanged?.Invoke(this, new(nameof(LastCheckedAt)));
    }

    public void Stop(DateTime stoppedAt) => ChangeState("Stopped", stoppedAt);

    public void Reset()
    {
        State = "Pending";
        LastCheckedAt = ChangedAt = null;
        PropertyChanged?.Invoke(this, new(null));
    }

    private void ChangeState(string state, DateTime at)
    {
        if (State == state) return;
        State = state;
        ChangedAt = at;
        PropertyChanged?.Invoke(this, new(nameof(State)));
        PropertyChanged?.Invoke(this, new(nameof(Text)));
        PropertyChanged?.Invoke(this, new(nameof(ChangedAt)));
    }
}
