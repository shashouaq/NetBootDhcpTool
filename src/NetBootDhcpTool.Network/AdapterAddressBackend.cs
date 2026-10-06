using System.Text.Json;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Network;

public sealed class AddressObservation
{
    public string IpAddress { get; set; } = "";
    public int PrefixLength { get; set; }
    public bool SkipAsSource { get; set; }
    public string AddressState { get; set; } = "";
    public string PrefixOrigin { get; set; } = "";
    public bool Persistent { get; set; }
}

public interface IAdapterAddressBackend
{
    NetworkAdapterInfo Resolve(string adapterId);
    Task<AdapterIpv4Snapshot> CaptureAsync(NetworkAdapterInfo adapter, CancellationToken ct);
    Task<List<AddressObservation>> ReadAsync(NetworkAdapterInfo adapter, CancellationToken ct);
    Task<bool> ReadCoexistenceAsync(NetworkAdapterInfo adapter, CancellationToken ct);
    Task SetCoexistenceAsync(NetworkAdapterInfo adapter, bool enabled, CancellationToken ct);
    Task ValidateAppendAsync(NetworkAdapterInfo adapter, OwnedAdapterAddress address, CancellationToken ct) => Task.CompletedTask;
    Task AddAsync(NetworkAdapterInfo adapter, OwnedAdapterAddress address, CancellationToken ct);
    Task RemoveAsync(NetworkAdapterInfo adapter, OwnedAdapterAddress address, CancellationToken ct);
}

public sealed class WindowsAdapterAddressBackend(NetworkAdapterService service) : IAdapterAddressBackend
{
    public NetworkAdapterInfo Resolve(string adapterId)
    {
        var matches = service.GetAdapters(false).Where(x => x.Id.Equals(adapterId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Adapter identity unavailable/ambiguous / 网卡身份不可用或不唯一");
        var adapter = matches[0];
        if (adapter.IsWifi || adapter.IsVirtual || !adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use an active physical Ethernet adapter / 请使用已连接的物理以太网卡");
        return adapter;
    }
    public Task<AdapterIpv4Snapshot> CaptureAsync(NetworkAdapterInfo adapter, CancellationToken ct) => service.CaptureIPv4ConfigAsync(adapter, ct);
    public Task<List<AddressObservation>> ReadAsync(NetworkAdapterInfo adapter, CancellationToken ct) => service.ReadAddressesAsync(adapter, ct);
    public Task<bool> ReadCoexistenceAsync(NetworkAdapterInfo adapter, CancellationToken ct) => service.ReadAddressCoexistenceAsync(adapter, ct);
    public Task SetCoexistenceAsync(NetworkAdapterInfo adapter, bool enabled, CancellationToken ct) => service.SetAddressCoexistenceAsync(adapter, enabled, ct);
    public Task ValidateAppendAsync(NetworkAdapterInfo adapter, OwnedAdapterAddress address, CancellationToken ct) => service.ValidateAddressAppendAsync(adapter, address, ct);
    public Task AddAsync(NetworkAdapterInfo adapter, OwnedAdapterAddress address, CancellationToken ct) => service.AddAddressAsync(adapter, address, ct);
    public Task RemoveAsync(NetworkAdapterInfo adapter, OwnedAdapterAddress address, CancellationToken ct) => service.RemoveAddressAsync(adapter, address, ct);
}

public sealed partial class NetworkAdapterService
{
    public async Task<List<AddressObservation>> ReadAddressesAsync(NetworkAdapterInfo adapter, CancellationToken ct)
    {
        var output = await AddressCommandAsync(adapter, """
$persistent = @(Get-NetIPAddress -AddressFamily IPv4 -PolicyStore PersistentStore -ErrorAction Stop | Where-Object { [int]$_.InterfaceIndex -eq $idx })
$items = @(Get-NetIPAddress -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop | Where-Object { [int]$_.InterfaceIndex -eq $idx } | ForEach-Object {
  $current = $_
  [pscustomobject]@{ IpAddress=[string]$current.IPAddress; PrefixLength=[int]$current.PrefixLength; SkipAsSource=[bool]$current.SkipAsSource; AddressState=[string]$current.AddressState; PrefixOrigin=[string]$current.PrefixOrigin; Persistent=[bool](@($persistent | Where-Object { $_.IPAddress -eq $current.IPAddress }).Count -gt 0) }
})
ConvertTo-Json -InputObject @($items) -Depth 4 -Compress
""", "read adapter address list", ct);
        return JsonSerializer.Deserialize<List<AddressObservation>>(output) ?? throw new InvalidDataException("Missing address readback / 地址读回缺失");
    }

    public async Task<bool> ReadAddressCoexistenceAsync(NetworkAdapterInfo adapter, CancellationToken ct)
    {
        var output = await AddressCommandAsync(adapter, """
$text = (& netsh interface ipv4 show interface "interface=$idx" level=verbose | Out-String)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read DHCP/static coexistence / 无法读取 DHCP 共存状态' }
if ($text -notmatch '(?im)^\s*DHCP/Static IP coexistence\s*:\s*(enabled|disabled)\s*$') { throw 'Coexistence state not recognized on this Windows language/build; no write allowed / 当前系统共存状态无法可靠识别，禁止写入' }
if ($Matches[1] -eq 'enabled') { 'true' } else { 'false' }
""", "read DHCP/static coexistence", ct);
        return bool.Parse(output.Trim());
    }

    public Task SetAddressCoexistenceAsync(NetworkAdapterInfo adapter, bool enabled, CancellationToken ct) => AddressCommandAsync(adapter,
        $"& netsh interface ipv4 set interface \"interface=$idx\" dhcpstaticipcoexistence={(enabled ? "enabled" : "disabled")} store=active\nif ($LASTEXITCODE -ne 0) {{ throw 'DHCP coexistence change failed / DHCP 共存设置失败' }}", "set DHCP/static coexistence", ct);

    public Task ValidateAddressAppendAsync(NetworkAdapterInfo adapter, OwnedAdapterAddress address, CancellationToken ct) => AddressCommandAsync(adapter,
        BuildAppendRouteGuard(address) + "\n'OK'", "validate additional address routes", ct);

    private static string BuildAppendRouteGuard(OwnedAdapterAddress address)
    {
        var ip = System.Net.IPAddress.Parse(ValidateIpv4(address.IpAddress, "additional address"));
        var prefix = ValidatePrefix(address.PrefixLength);
        if (prefix == 0) throw new InvalidDataException("Default prefix not allowed / 禁止默认前缀");
        var mask = uint.MaxValue << (32 - prefix);
        var first = IpNetwork.ToUInt32(ip) & mask;
        var last = first | ~mask;
        return $$"""
foreach ($route in @(Get-NetRoute -AddressFamily IPv4 -ErrorAction Stop)) {
  $parts = ([string]$route.DestinationPrefix).Split('/')
  $length = [int]$parts[1]
  if ($length -eq 0) { continue }
  $bytes = ([System.Net.IPAddress]::Parse($parts[0])).GetAddressBytes()
  $value = ([uint64]$bytes[0] -shl 24) -bor ([uint64]$bytes[1] -shl 16) -bor ([uint64]$bytes[2] -shl 8) -bor [uint64]$bytes[3]
  $mask = ([uint64]4294967295 -shl (32 - $length)) -band [uint64]4294967295
  $first = $value -band $mask
  $last = $first -bor ([uint64]4294967295 -bxor $mask)
  if ([uint64]{{first}} -le $last -and $first -le [uint64]{{last}}) {
    if ([int]$route.InterfaceIndex -ne $idx -or ($length -eq {{prefix}} -and [string]$route.NextHop -ne '0.0.0.0')) {
      throw 'Additional address subnet overlaps another interface/path / 追加地址网段与其他网卡或已有路径重叠'
    }
  }
}
""";
    }

    public Task AddAddressAsync(NetworkAdapterInfo adapter, OwnedAdapterAddress address, CancellationToken ct) => AddressCommandAsync(adapter, BuildAppendRouteGuard(address) + "\n" + $$"""
if (@(Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction Stop | Where-Object { $_.IPAddress -eq {{PsQuote(address.IpAddress)}} }).Count -ne 0) { throw 'Address changed after planning / 规划后地址发生变化' }
& netsh interface ipv4 add address "name=$idx" address={{PsQuote(address.IpAddress)}} mask={{PsQuote(IpNetwork.FromUInt32(address.PrefixLength == 0 ? 0u : uint.MaxValue << (32 - address.PrefixLength)).ToString())}} store=active skipassource=false
if ($LASTEXITCODE -ne 0) { throw 'Address append failed / 追加地址失败' }
""", "append temporary IPv4 address", ct);

    public Task RemoveAddressAsync(NetworkAdapterInfo adapter, OwnedAdapterAddress address, CancellationToken ct) => AddressCommandAsync(adapter, $$"""
$items = @(Get-NetIPAddress -AddressFamily IPv4 -PolicyStore ActiveStore -ErrorAction Stop | Where-Object { [int]$_.InterfaceIndex -eq $idx } | Where-Object { $_.IPAddress -eq {{PsQuote(address.IpAddress)}} })
$persistent = @(Get-NetIPAddress -AddressFamily IPv4 -PolicyStore PersistentStore -ErrorAction Stop | Where-Object { [int]$_.InterfaceIndex -eq $idx } | Where-Object { $_.IPAddress -eq {{PsQuote(address.IpAddress)}} })
if ($items.Count -gt 1 -or $persistent.Count -gt 0) { throw 'Address ownership changed / 地址归属发生变化' }
if ($items.Count -eq 1) {
  if ([int]$items[0].PrefixLength -ne {{address.PrefixLength}} -or [bool]$items[0].SkipAsSource -ne $false -or [string]$items[0].PrefixOrigin -ne 'Manual') { throw 'Address properties changed / 地址属性发生变化' }
  $items[0] | Remove-NetIPAddress -Confirm:$false -ErrorAction Stop
}
""", "remove owned temporary IPv4 address", ct);

    private Task<string> AddressCommandAsync(NetworkAdapterInfo adapter, string commands, string summary, CancellationToken ct)
    {
        EnsureAllowedTargetAdapter(adapter);
        return RunPowerShellOutputAsync(BuildAdapterLookup(adapter) + Environment.NewLine + commands, "PowerShell action " + summary, ct, false);
    }
}
