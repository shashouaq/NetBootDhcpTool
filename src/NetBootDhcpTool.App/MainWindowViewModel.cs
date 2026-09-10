using System.Collections.ObjectModel;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Dhcp;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.App;

/// <summary>
/// UI-owned state container. MainWindow remains the event coordinator while
/// collections used by XAML bindings are kept out of the window code-behind.
/// </summary>
public sealed class MainWindowViewModel
{
    public ObservableCollection<NetworkAdapterInfo> Adapters { get; } = [];
    public ObservableCollection<ScanResult> ScanResults { get; } = [];
    public ObservableCollection<FavoriteConfig> Favorites { get; } = [];
    public ObservableCollection<DhcpLease> Leases { get; } = [];
    public ObservableCollection<AdapterIpHistoryItem> AdapterIpHistory { get; } = [];
    public ObservableCollection<StaticRouteRule> StaticRoutes { get; } = [];
    public ObservableCollection<StaticRouteRule> CurrentStaticRoutes { get; } = [];
    public ObservableCollection<OperationHistoryItem> OperationHistory { get; } = [];
    public ObservableCollection<NetworkProfile> Profiles { get; } = [];
}
