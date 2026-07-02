using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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

    public IReadOnlyList<NetworkAdapterInfo> GetAdapters()
    {
        var adapters = new List<NetworkAdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.Description.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)) continue;

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
                _logger.Info($"Adapter: {info.Name} {info.Description} IP={info.IPv4Address} MAC={info.MacAddress} Gateway={info.Gateway}");
            }
            catch (Exception ex)
            {
                _logger.Warn($"Skip adapter {ni.Name}: {ex.Message}");
            }
        }
        return adapters;
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
$ips = @(Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '169.254.*' } | Select-Object -First 1)
$routes = @(Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric | Select-Object -First 1)
$dns = @(Get-DnsClientServerAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue).ServerAddresses
[pscustomobject]@{
  DhcpEnabled = ($ipif.Dhcp -eq 'Enabled')
  IpAddress = if ($ips.Count -gt 0) { $ips[0].IPAddress } else { '' }
  PrefixLength = if ($ips.Count -gt 0) { [int]$ips[0].PrefixLength } else { 24 }
  Gateway = if ($routes.Count -gt 0) { $routes[0].NextHop } else { '' }
  Dns = @($dns)
  AutomaticMetric = [bool]$ipif.AutomaticMetric
  InterfaceMetric = [int]$ipif.InterfaceMetric
} | ConvertTo-Json -Compress
""";
        var output = await RunPowerShellOutputAsync(script, "PowerShell action capture target adapter IPv4", ct, false);
        var snapshot = JsonSerializer.Deserialize<AdapterIpv4Snapshot>(output) ?? new AdapterIpv4Snapshot();
        _logger.Info($"Captured adapter IPv4: idx={adapter.InterfaceIndex} dhcp={snapshot.DhcpEnabled} ip={snapshot.IpAddress} gateway={snapshot.Gateway} autoMetric={snapshot.AutomaticMetric} metric={snapshot.InterfaceMetric}");
        return snapshot;
    }

    public async Task RestoreIPv4ConfigAsync(NetworkAdapterInfo adapter, AdapterIpv4Snapshot snapshot, CancellationToken ct = default)
    {
        EnsureAllowedTargetAdapter(adapter);
        var restoreDnsCommand = snapshot.Dns.Count == 0
            ? "Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses -ErrorAction SilentlyContinue"
            : "Set-DnsClientServerAddress -InterfaceIndex $idx -ServerAddresses @(" + string.Join(",", snapshot.Dns.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => "'" + x.Trim().Replace("'", "''") + "'")) + ") -ErrorAction SilentlyContinue";
        if (snapshot.DhcpEnabled || string.IsNullOrWhiteSpace(snapshot.IpAddress))
        {
            var metricScript = snapshot.AutomaticMetric
                ? "Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction SilentlyContinue"
                : $"Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {Math.Max(1, snapshot.InterfaceMetric)} -ErrorAction SilentlyContinue";
            var dhcpScript = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{adapter.InterfaceIndex}}
Set-NetIPInterface -InterfaceIndex $idx -Dhcp Enabled -ErrorAction Stop
{{metricScript}}
{{restoreDnsCommand}}
'OK'
""";
            await RunPowerShellAsync(dhcpScript, "PowerShell action restore target adapter original DHCP", ct);
            return;
        }

        var metricRestore = snapshot.AutomaticMetric
            ? "Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction SilentlyContinue"
            : $"Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {Math.Max(1, snapshot.InterfaceMetric)} -ErrorAction SilentlyContinue";
        var gatewayPart = string.IsNullOrWhiteSpace(snapshot.Gateway) || snapshot.Gateway.Trim().Equals(snapshot.IpAddress.Trim(), StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"-DefaultGateway '{snapshot.Gateway}'";
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{adapter.InterfaceIndex}}
Set-NetIPInterface -InterfaceIndex $idx -Dhcp Disabled -ErrorAction SilentlyContinue
Get-NetIPAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction SilentlyContinue | Remove-NetIPAddress -Confirm:$false -ErrorAction SilentlyContinue
Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
New-NetIPAddress -InterfaceIndex $idx -IPAddress '{{snapshot.IpAddress}}' -PrefixLength {{snapshot.PrefixLength}} {{gatewayPart}} -ErrorAction Stop | Out-Null
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

    public Task EnsureDhcpFirewallRulesAsync(CancellationToken ct = default)
    {
        var script = """
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$inName='NetBoot DHCP Tool DHCP In'
$outName='NetBoot DHCP Tool DHCP Out'
if (-not (Get-NetFirewallRule -DisplayName $inName -ErrorAction SilentlyContinue)) {
  New-NetFirewallRule -DisplayName $inName -Direction Inbound -Action Allow -Protocol UDP -LocalPort 67 -Profile Any | Out-Null
}
if (-not (Get-NetFirewallRule -DisplayName $outName -ErrorAction SilentlyContinue)) {
  New-NetFirewallRule -DisplayName $outName -Direction Outbound -Action Allow -Protocol UDP -RemotePort 68 -Profile Any | Out-Null
}
'OK'
""";
        return RunPowerShellAsync(script, "PowerShell action ensure DHCP firewall rules", ct);
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
}

public sealed class AdapterIpv4Snapshot
{
    public bool DhcpEnabled { get; set; }
    public string IpAddress { get; set; } = "";
    public int PrefixLength { get; set; } = 24;
    public string Gateway { get; set; } = "";
    public List<string> Dns { get; set; } = [];
    public bool AutomaticMetric { get; set; } = true;
    public int InterfaceMetric { get; set; } = 0;

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

public sealed class WlanReadonlyState
{
    public bool IsConnected { get; set; }
    public string InterfaceName { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public bool HasUsbWiredAdapter { get; set; }
    public bool PolicyBlockedRecent { get; set; }
    public string PolicyBlockedMessage { get; set; } = "";
}
