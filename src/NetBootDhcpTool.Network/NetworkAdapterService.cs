using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Network;

public sealed class NetworkAdapterService
{
    private readonly ILogger _logger;

    public NetworkAdapterService(ILogger logger)
    {
        _logger = logger;
    }

    public bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters(bool logAdapters = true)
    {
        var adapters = new List<NetworkAdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = ni.GetIPProperties();
                var ipv4Props = TryGetIPv4Properties(props, ni.Name);
                if (ipv4Props == null && TryFindNetAdapterIndex(ni.Name) <= 0) continue;

                var ip = props.UnicastAddresses.FirstOrDefault(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                var gateway = props.GatewayAddresses
                    .FirstOrDefault(x => x.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && x.Address.ToString() != "0.0.0.0")
                    ?.Address.ToString() ?? "";
                var dns = string.Join(", ", props.DnsAddresses.Where(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(x => x.ToString()));
                var info = new NetworkAdapterInfo
                {
                    Id = ni.Id,
                    Name = ni.Name,
                    Description = ni.Description,
                    InterfaceIndex = (ipv4Props?.Index ?? TryFindNetAdapterIndex(ni.Name)).ToString(),
                    MacAddress = FormatMac(ni.GetPhysicalAddress()),
                    IPv4Address = ip?.Address.ToString() ?? "",
                    SubnetMask = ip?.IPv4Mask?.ToString() ?? "",
                    Gateway = gateway,
                    Dns = dns,
                    Status = ni.OperationalStatus.ToString(),
                    IsWifi = IsWifiLike(ni.Name, ni.Description, ni.NetworkInterfaceType),
                    IsVirtual = IsVirtual(ni.Name, ni.Description)
                };
                adapters.Add(info);
                if (logAdapters) _logger.Info($"Adapter: {info.Name} {info.Description} IP={info.IPv4Address} MAC={info.MacAddress} Gateway={info.Gateway}");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Skip adapter {ni.Name}: {ex.Message}");
            }
        }
        return adapters;
    }

    public async Task RestartAdapterAsync(NetworkAdapterInfo adapter, bool allowAnyAdapter, CancellationToken ct = default)
    {
        EnsureControlTargetAdapter(adapter, allowAnyAdapter);
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{adapter.InterfaceIndex}}
$netAdapter = Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop
$wasDisabled = $netAdapter.Status -eq 'Disabled'
if ($wasDisabled) {
  Enable-NetAdapter -InterfaceIndex $idx -Confirm:$false -ErrorAction Stop
  Start-Sleep -Milliseconds 500
}
Disable-NetAdapter -InterfaceIndex $idx -Confirm:$false -ErrorAction Stop
Start-Sleep -Milliseconds 750
Enable-NetAdapter -InterfaceIndex $idx -Confirm:$false -ErrorAction Stop
[pscustomobject]@{ InterfaceIndex = $idx; WasDisabled = [bool]$wasDisabled; Status = (Get-NetAdapter -InterfaceIndex $idx -ErrorAction SilentlyContinue).Status } | ConvertTo-Json -Compress
""";
        await RunPowerShellAsync(script, $"PowerShell action restart adapter idx={adapter.InterfaceIndex} name={adapter.Name}", ct);
    }

    public async Task ChangeMacAddressAsync(NetworkAdapterInfo adapter, string macAddress, bool allowAnyAdapter, CancellationToken ct = default)
    {
        EnsureControlTargetAdapter(adapter, allowAnyAdapter);
        var normalized = NormalizeMacAddress(macAddress);
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{adapter.InterfaceIndex}}
$mac={{PsQuote(normalized)}}
$netAdapter = Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop
$netAdapter | Set-NetAdapter -MacAddress $mac -Confirm:$false -ErrorAction Stop
Start-Sleep -Milliseconds 500
[pscustomobject]@{ InterfaceIndex = $idx; RequestedMac = $mac; CurrentMac = (Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop).MacAddress } | ConvertTo-Json -Compress
""";
        await RunPowerShellAsync(script, $"PowerShell action change adapter MAC idx={adapter.InterfaceIndex} mac={normalized}", ct);
    }

    public Task RestoreMacAddressAsync(NetworkAdapterInfo adapter, string originalMacAddress, CancellationToken ct = default)
    {
        return ChangeMacAddressAsync(adapter, originalMacAddress, allowAnyAdapter: true, ct);
    }

    public static string NormalizeMacAddress(string value)
    {
        var compact = Regex.Replace(value ?? "", "[^0-9A-Fa-f]", "");
        if (compact.Length != 12 || !compact.All(Uri.IsHexDigit)) throw new FormatException("MAC 地址必须是 12 位十六进制字符");
        if (!byte.TryParse(compact[..2], System.Globalization.NumberStyles.HexNumber, null, out var first)) throw new FormatException("MAC 地址格式无效");
        if ((first & 1) != 0) throw new FormatException("MAC 地址不能是组播地址");
        return string.Join("-", Enumerable.Range(0, 6).Select(i => compact.Substring(i * 2, 2).ToUpperInvariant()));
    }

    public static string GenerateRandomMacAddress()
    {
        var bytes = new byte[6];
        Random.Shared.NextBytes(bytes);
        bytes[0] = (byte)((bytes[0] & 0xFC) | 0x02); // locally administered, unicast
        return string.Join("-", bytes.Select(x => x.ToString("X2")));
    }

    public async Task ApplyStaticIPv4Async(NetworkAdapterInfo adapter, string ip, string mask, string gateway, string dns, CancellationToken ct = default)
    {
        EnsureAllowedTargetAdapter(adapter);
        var prefix = IpNetwork.PrefixLength(IPAddress.Parse(mask));
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{adapter.InterfaceIndex}}
Set-NetIPInterface -InterfaceIndex $idx -Dhcp Disabled -ErrorAction SilentlyContinue
Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
New-NetIPAddress -InterfaceIndex $idx -IPAddress '{{ip}}' -PrefixLength {{prefix}} -ErrorAction Stop | Out-Null
Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric 9000 -ErrorAction Stop
Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses -ErrorAction SilentlyContinue
'OK'
""";
        await RunPowerShellAsync(script, "PowerShell action apply target adapter static IPv4", ct);
    }

    public async Task RestoreDhcpAsync(NetworkAdapterInfo adapter, CancellationToken ct = default)
    {
        EnsureAllowedTargetAdapter(adapter);
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{adapter.InterfaceIndex}}
Set-NetIPInterface -InterfaceIndex $idx -Dhcp Enabled -ErrorAction Stop
Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction SilentlyContinue
Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses -ErrorAction SilentlyContinue
'OK'
""";
        await RunPowerShellAsync(script, "PowerShell action restore target adapter DHCP", ct);
    }

    public async Task<AdapterIpv4Snapshot> CaptureIPv4ConfigAsync(NetworkAdapterInfo adapter, CancellationToken ct = default)
    {
        EnsureAllowedTargetAdapter(adapter);
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{adapter.InterfaceIndex}}
$ipif = Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction Stop
$ips = @(Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '169.254.*' } | Sort-Object SkipAsSource,IPAddress | ForEach-Object {
  [pscustomobject]@{ IpAddress = $_.IPAddress; PrefixLength = [int]$_.PrefixLength }
})
$routes = @(Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric,NextHop | ForEach-Object {
  [pscustomobject]@{ DestinationPrefix = $_.DestinationPrefix; NextHop = $_.NextHop; RouteMetric = [int]$_.RouteMetric; PolicyStore = [string]$_.PolicyStore }
})
$dns = @(Get-DnsClientServerAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses
[pscustomobject]@{
  DhcpEnabled = ($ipif.Dhcp -eq 'Enabled')
  Addresses = [object[]]$ips
  Routes = [object[]]$routes
  IpAddress = if ($ips.Count -gt 0) { $ips[0].IpAddress } else { '' }
  PrefixLength = if ($ips.Count -gt 0) { [int]$ips[0].PrefixLength } else { 24 }
  Gateway = if ($routes.Count -gt 0) { $routes[0].NextHop } else { '' }
  Dns = @($dns)
  AutomaticMetric = [bool]$ipif.AutomaticMetric
  InterfaceMetric = [int]$ipif.InterfaceMetric
} | ConvertTo-Json -Compress -Depth 5
""";
        var output = await RunPowerShellOutputAsync(script, "PowerShell action capture target adapter IPv4", ct, false);
        var snapshot = JsonSerializer.Deserialize<AdapterIpv4Snapshot>(output) ?? new AdapterIpv4Snapshot();
        snapshot.NormalizeLegacyFields();
        _logger.Info($"Captured adapter IPv4: idx={adapter.InterfaceIndex} dhcp={snapshot.DhcpEnabled} addresses={snapshot.Addresses.Count} routes={snapshot.Routes.Count} autoMetric={snapshot.AutomaticMetric} metric={snapshot.InterfaceMetric}");
        return snapshot;
    }

    public async Task RestoreIPv4ConfigAsync(NetworkAdapterInfo adapter, AdapterIpv4Snapshot snapshot, CancellationToken ct = default)
    {
        EnsureAllowedTargetAdapter(adapter);
        snapshot.NormalizeLegacyFields();
        var restoreDnsCommand = snapshot.Dns.Count == 0
            ? "Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses -ErrorAction Stop"
            : "Set-DnsClientServerAddress -InterfaceIndex $idx -ServerAddresses @("
              + string.Join(",", snapshot.Dns.Where(x => IPAddress.TryParse(x, out var dns) && dns.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(PsQuote))
              + ") -ErrorAction Stop";
        if (snapshot.DhcpEnabled || snapshot.Addresses.Count == 0)
        {
            var metricScript = snapshot.AutomaticMetric
                ? "Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction Stop"
                : $"Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {Math.Max(1, snapshot.InterfaceMetric)} -ErrorAction Stop";
            var dhcpScript = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{adapter.InterfaceIndex}}
Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Disabled -ErrorAction SilentlyContinue
Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Enabled -ErrorAction Stop
{{metricScript}}
{{restoreDnsCommand}}
'OK'
""";
            await RunPowerShellAsync(dhcpScript, "PowerShell action restore target adapter original DHCP", ct);
            return;
        }

        var metricRestore = snapshot.AutomaticMetric
            ? "Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction Stop"
            : $"Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {Math.Max(1, snapshot.InterfaceMetric)} -ErrorAction Stop";
        var addressCommands = string.Join(Environment.NewLine, snapshot.Addresses.Select(address =>
            $"New-NetIPAddress -InterfaceIndex $idx -IPAddress {PsQuote(address.IpAddress)} -PrefixLength {ValidatePrefix(address.PrefixLength)} -ErrorAction Stop | Out-Null"));
        var routeCommands = string.Join(Environment.NewLine, snapshot.Routes.Select(BuildRouteRestoreCommand));
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{adapter.InterfaceIndex}}
Set-NetIPInterface -InterfaceIndex $idx -Dhcp Disabled -ErrorAction SilentlyContinue
Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
{{addressCommands}}
{{routeCommands}}
{{metricRestore}}
{{restoreDnsCommand}}
'OK'
""";
        await RunPowerShellAsync(script, "PowerShell action restore target adapter original static IPv4", ct);
    }

    public async Task<WlanReadonlyState> LogReadonlyWlanStateAsync(string phase, CancellationToken ct = default)
    {
        var state = await GetReadonlyWlanStateAsync(ct);
        _logger.Info($"WLAN readonly state {phase}: connected={state.IsConnected} usbWired={state.HasUsbWiredAdapter} policyBlockedRecent={state.PolicyBlockedRecent} interface={state.InterfaceName} profile={state.ProfileName}");
        return state;
    }

    public async Task<DhcpFirewallRuleLease> EnsureDhcpFirewallRulesAsync(CancellationToken ct = default)
    {
        var programPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        var programPart = string.IsNullOrWhiteSpace(programPath) ? "" : $" -Program {PsQuote(programPath)}";
        var script = """
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$inName='NetBoot DHCP Tool DHCP In'
$outName='NetBoot DHCP Tool DHCP Out'
$group='NetBootDhcpTool'
$created=@()
if (-not (Get-NetFirewallRule -DisplayName $inName -ErrorAction SilentlyContinue)) {
  New-NetFirewallRule -DisplayName $inName -Group $group -Direction Inbound -Action Allow -Protocol UDP -LocalPort 67 -Profile Any__PROGRAM__ | Out-Null
  $created += $inName
}
if (-not (Get-NetFirewallRule -DisplayName $outName -ErrorAction SilentlyContinue)) {
  New-NetFirewallRule -DisplayName $outName -Group $group -Direction Outbound -Action Allow -Protocol UDP -RemotePort 68 -Profile Any__PROGRAM__ | Out-Null
  $created += $outName
}
[pscustomobject]@{ CreatedRuleNames = @($created) } | ConvertTo-Json -Compress
""".Replace("__PROGRAM__", programPart, StringComparison.Ordinal);
        var output = await RunPowerShellOutputAsync(script, "PowerShell action ensure DHCP firewall rules", ct, false);
        return JsonSerializer.Deserialize<DhcpFirewallRuleLease>(output, JsonStore.Options) ?? new DhcpFirewallRuleLease();
    }

    public Task RemoveDhcpFirewallRulesAsync(DhcpFirewallRuleLease? lease, CancellationToken ct = default)
    {
        if (lease == null || lease.CreatedRuleNames.Count == 0) return Task.CompletedTask;
        var names = string.Join(",", lease.CreatedRuleNames.Distinct(StringComparer.OrdinalIgnoreCase).Select(PsQuote));
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$group='NetBootDhcpTool'
$names=@({{names}})
foreach ($name in $names) {
  Get-NetFirewallRule -DisplayName $name -Group $group -ErrorAction SilentlyContinue | Remove-NetFirewallRule -Confirm:$false -ErrorAction Stop
}
'OK'
""";
        return RunPowerShellAsync(script, "PowerShell action remove DHCP firewall rules", ct);
    }

    private async Task<WlanReadonlyState> GetReadonlyWlanStateAsync(CancellationToken ct)
    {
        var script = """
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$wifiRegex = 'WLAN|Wi-?Fi|Wireless|无线|802\.11'
$wlanText = (netsh wlan show interfaces) -join "`n"
$isConnected = $wlanText -match '(?im)^\s*(State|状态)\s*:\s*(connected|已连接)'
$profile = ''
$name = ''
if ($wlanText -match '(?im)^\s*(Profile|配置文件)\s*:\s*(.+)$') { $profile = $Matches[2].Trim() }
if ($wlanText -match '(?im)^\s*(Name|名称)\s*:\s*(.+)$') { $name = $Matches[2].Trim() }
$usbWired = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {
  $_.Status -ne 'Disabled' -and
  $_.InterfaceDescription -match 'USB' -and
  $_.InterfaceDescription -notmatch $wifiRegex -and
  $_.Name -notmatch $wifiRegex
}).Count -gt 0
$since = (Get-Date).AddMinutes(-30)
$policyEvent = Get-WinEvent -FilterHashtable @{LogName='Microsoft-Windows-WLAN-AutoConfig/Operational'; StartTime=$since} -ErrorAction SilentlyContinue |
  Where-Object { $_.Message -match '策略禁止在该接口上自动连接|policy.*automatic.*connect|prevent.*automatic.*connect' } |
  Select-Object -First 1
[pscustomobject]@{
  IsConnected = [bool]$isConnected
  InterfaceName = $name
  ProfileName = $profile
  HasUsbWiredAdapter = [bool]$usbWired
  PolicyBlockedRecent = ($null -ne $policyEvent)
  PolicyBlockedMessage = if ($null -ne $policyEvent) { $policyEvent.Message } else { '' }
} | ConvertTo-Json -Compress
""";
        var output = await RunPowerShellOutputAsync(script, "PowerShell action read WLAN state", ct, false);
        return JsonSerializer.Deserialize<WlanReadonlyState>(output) ?? new WlanReadonlyState();
    }

    private async Task RunPowerShellAsync(string script, string summary, CancellationToken ct)
    {
        _ = await RunPowerShellOutputAsync(script, summary, ct, true);
    }

    private async Task<string> RunPowerShellOutputAsync(string script, string summary, CancellationToken ct, bool logOutput)
    {
        _logger.Info(summary);
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        var trimmedOutput = output.Trim();
        var trimmedError = error.Trim();
        if (process.ExitCode != 0)
        {
            var detail = SummarizePowerShellMessage(trimmedError);
            _logger.Warn($"{summary} failed: exit={process.ExitCode} detail={detail}");
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(trimmedError) ? $"PowerShell exit {process.ExitCode}" : trimmedError);
        }

        if (logOutput)
        {
            var detail = SummarizePowerShellMessage(trimmedOutput);
            _logger.Info($"{summary} result: exit={process.ExitCode} detail={detail}");
        }
        return trimmedOutput;
    }

    private static string SummarizePowerShellMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "OK";
        var normalized = string.Join(" | ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length > 240) normalized = normalized[..240] + "...";
        return normalized;
    }

    private static void EnsureAllowedTargetAdapter(NetworkAdapterInfo adapter)
    {
        if (string.IsNullOrWhiteSpace(adapter.InterfaceIndex)) throw new InvalidOperationException("InterfaceIndex missing");
        if (adapter.IsWifi || IsWifiLike(adapter.Name, adapter.Description, null)) throw new InvalidOperationException("Refusing to modify WLAN adapter");
    }

    private static void EnsureControlTargetAdapter(NetworkAdapterInfo adapter, bool allowAnyAdapter)
    {
        if (string.IsNullOrWhiteSpace(adapter.InterfaceIndex)) throw new InvalidOperationException("InterfaceIndex missing");
        if (allowAnyAdapter) return;
        if (adapter.IsWifi || IsWifiLike(adapter.Name, adapter.Description, null) || adapter.IsVirtual)
            throw new InvalidOperationException("设置当前禁止操作无线或虚拟网卡");
        if (!adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("设置当前禁止操作未启用的网卡");
    }

    private IPv4InterfaceProperties? TryGetIPv4Properties(IPInterfaceProperties props, string adapterName)
    {
        try
        {
            return props.GetIPv4Properties();
        }
        catch (NetworkInformationException ex)
        {
            _logger.Warn($"Adapter {adapterName} has no usable IPv4 properties: {ex.Message}");
            return null;
        }
    }

    private static int TryFindNetAdapterIndex(string name)
    {
        try
        {
            var all = NetworkInterface.GetAllNetworkInterfaces();
            var match = all.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return match?.GetIPProperties().GetIPv4Properties().Index ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsWifiLike(string name, string description, NetworkInterfaceType? type)
    {
        if (type == NetworkInterfaceType.Wireless80211) return true;
        var text = $"{name} {description}";
        return text.Contains("WLAN", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
            || text.Contains("WiFi", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Wireless", StringComparison.OrdinalIgnoreCase)
            || text.Contains("无线", StringComparison.OrdinalIgnoreCase)
            || text.Contains("802.11", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVirtual(string name, string description)
    {
        var text = (name + " " + description).ToLowerInvariant();
        string[] markers = ["vmware", "virtualbox", "hyper-v", "vpn", "tap", "wsl", "virtual", "loopback"];
        return markers.Any(text.Contains);
    }

    private static string FormatMac(PhysicalAddress mac)
    {
        var bytes = mac.GetAddressBytes();
        return bytes.Length == 0 ? "" : string.Join("-", bytes.Select(x => x.ToString("X2")));
    }

    private static string PsQuote(string value) => "'" + (value ?? "").Replace("'", "''") + "'";

    private static string BuildRouteRestoreCommand(AdapterRouteSnapshot route)
    {
        var destination = ValidateDestinationPrefix(route.DestinationPrefix);
        var nextHop = ValidateIpv4(route.NextHop, "route next hop");
        var metric = ValidateMetric(route.RouteMetric);
        return $"New-NetRoute -InterfaceIndex $idx -DestinationPrefix {PsQuote(destination)} -NextHop {PsQuote(nextHop)} -RouteMetric {metric} {PolicyStorePart(route.PolicyStore)} -ErrorAction Stop | Out-Null";
    }

    private static string ValidateIpv4(string value, string field)
    {
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidDataException($"Invalid {field}: {value}");
        return ip.ToString();
    }

    private static int ValidatePrefix(int prefix)
    {
        if (prefix is < 0 or > 32) throw new InvalidDataException($"Invalid IPv4 prefix length: {prefix}");
        return prefix;
    }

    private static int ValidateMetric(int metric)
    {
        if (metric is < 0 or > 65535) throw new InvalidDataException($"Invalid route metric: {metric}");
        return metric;
    }

    private static string ValidateDestinationPrefix(string value)
    {
        if (!value.Equals("0.0.0.0/0", StringComparison.Ordinal)) throw new InvalidDataException($"Unexpected captured default route: {value}");
        return value;
    }

    private static string PolicyStorePart(string value)
    {
        value ??= "";
        return value.Equals("ActiveStore", StringComparison.OrdinalIgnoreCase) || value.Equals("PersistentStore", StringComparison.OrdinalIgnoreCase)
            ? $"-PolicyStore {PsQuote(value)}"
            : "";
    }
}

public sealed class AdapterIpv4Snapshot
{
    public bool DhcpEnabled { get; set; }
    public List<AdapterIpv4AddressSnapshot> Addresses { get; set; } = [];
    public List<AdapterRouteSnapshot> Routes { get; set; } = [];
    public string IpAddress { get; set; } = "";
    public int PrefixLength { get; set; } = 24;
    public string Gateway { get; set; } = "";
    public List<string> Dns { get; set; } = [];
    public bool AutomaticMetric { get; set; } = true;
    public int InterfaceMetric { get; set; } = 0;

    public void NormalizeLegacyFields()
    {
        if (Addresses.Count == 0 && !string.IsNullOrWhiteSpace(IpAddress))
        {
            Addresses.Add(new AdapterIpv4AddressSnapshot { IpAddress = IpAddress, PrefixLength = PrefixLength });
        }
        if (Routes.Count == 0 && !string.IsNullOrWhiteSpace(Gateway))
        {
            Routes.Add(new AdapterRouteSnapshot { NextHop = Gateway });
        }
        if (string.IsNullOrWhiteSpace(IpAddress) && Addresses.Count > 0)
        {
            IpAddress = Addresses[0].IpAddress;
            PrefixLength = Addresses[0].PrefixLength;
        }
        if (string.IsNullOrWhiteSpace(Gateway) && Routes.Count > 0) Gateway = Routes[0].NextHop;
    }

    public string DisplayText
    {
        get
        {
            var mode = DhcpEnabled ? "DHCP" : "Static";
            var dns = Dns.Count == 0 ? "" : " DNS=" + string.Join(",", Dns);
            return $"{mode} IP={IpAddress} Prefix={PrefixLength} Gateway={Gateway} AutoMetric={AutomaticMetric} Metric={InterfaceMetric}{dns}";
        }
    }
}

public sealed class DhcpFirewallRuleLease
{
    public List<string> CreatedRuleNames { get; set; } = [];
}

public sealed class WlanReadonlyState
{
    public bool IsConnected { get; set; }
    public string InterfaceName { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public bool HasUsbWiredAdapter { get; set; }
    public bool PolicyBlockedRecent { get; set; }
    public string PolicyBlockedMessage { get; set; } = "";
}
