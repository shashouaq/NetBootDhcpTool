using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Network;

public delegate Task<string> PowerShellScriptExecutor(string script, string summary, CancellationToken cancellationToken, bool logOutput);

public sealed class NetworkAdapterService
{
    public const int DhcpHostAdapterMetric = 9000;

    private readonly ILogger _logger;
    private readonly PowerShellScriptExecutor? _powerShellScriptExecutor;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public NetworkAdapterService(
        ILogger logger,
        PowerShellScriptExecutor? powerShellScriptExecutor = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _logger = logger;
        _powerShellScriptExecutor = powerShellScriptExecutor;
        _delayAsync = delayAsync ?? Task.Delay;
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
                    LinkSpeedMbps = ni.Speed > 0 ? (long)Math.Round(ni.Speed / 1_000_000d) : 0,
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

    public async Task<AdapterRestartResult> RestartAdapterAsync(NetworkAdapterInfo adapter, bool allowAnyAdapter, CancellationToken ct = default, bool? expectedEnabledBefore = null)
    {
        EnsureControlTargetAdapter(adapter, allowAnyAdapter);
        var originalStatus = await GetAdapterStatusAsync(adapter, ct);
        var wasDisabled = IsAdministrativeStateDisabled(originalStatus);
        if (expectedEnabledBefore.HasValue && expectedEnabledBefore.Value != !wasDisabled)
            throw new InvalidOperationException("The adapter administrative state changed after its recovery snapshot was saved. No restart was attempted.");
        var mutationAttempted = false;
        try
        {
            if (wasDisabled)
            {
                mutationAttempted = true;
                await SetAdapterAdministrativeStateAsync(adapter, enabled: true, ct);
                await _delayAsync(TimeSpan.FromMilliseconds(500), ct);
            }

            ct.ThrowIfCancellationRequested();
            mutationAttempted = true;
            await SetAdapterAdministrativeStateAsync(adapter, enabled: false, ct);
            await _delayAsync(TimeSpan.FromMilliseconds(750), ct);
            ct.ThrowIfCancellationRequested();
            await SetAdapterAdministrativeStateAsync(adapter, enabled: true, ct);
            var finalStatus = await GetAdapterStatusAsync(adapter, ct);
            if (IsAdministrativeStateDisabled(finalStatus))
                throw new InvalidOperationException($"Adapter {adapter.InterfaceIndex} remains disabled after restart.");
            return new AdapterRestartResult(wasDisabled, finalStatus);
        }
        catch (Exception operationError)
        {
            if (!mutationAttempted) throw;
            try
            {
                using var compensationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                await SetAdapterAdministrativeStateAsync(adapter, enabled: !wasDisabled, compensationTimeout.Token);
                var restoredStatus = await GetAdapterStatusAsync(adapter, compensationTimeout.Token);
                var restoredDisabled = IsAdministrativeStateDisabled(restoredStatus);
                if (restoredDisabled != wasDisabled)
                    throw new InvalidOperationException($"Adapter administrative-state restore verification failed: expectedDisabled={wasDisabled}, actualStatus={restoredStatus}.");
            }
            catch (Exception compensationError)
            {
                _logger.Error($"Adapter restart failed and original administrative state could not be verified: idx={adapter.InterfaceIndex} name={adapter.Name}", compensationError);
                throw new AggregateException("Adapter restart failed; restoring its previous enabled/disabled state also failed. Check the adapter state before retrying.", operationError, compensationError);
            }
            throw;
        }
    }

    public async Task<bool> IsAdapterEnabledAsync(NetworkAdapterInfo adapter, CancellationToken ct = default)
    {
        EnsureInterfaceIndex(adapter);
        return !IsAdministrativeStateDisabled(await GetAdapterStatusAsync(adapter, ct));
    }

    public async Task EnsureAdapterEnabledAsync(NetworkAdapterInfo adapter, bool allowAnyAdapter, CancellationToken ct = default)
    {
        EnsureControlTargetAdapter(adapter, allowAnyAdapter);
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
{{BuildAdapterLookup(adapter)}}
if ($netAdapter.AdminStatus -ne 'Up') {
  Enable-NetAdapter -InputObject $netAdapter -Confirm:$false -ErrorAction Stop
}
$finalAdapter = Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop
if ($finalAdapter.AdminStatus -ne 'Up') { throw "Adapter $idx remains administratively disabled after cleanup." }
[pscustomobject]@{ InterfaceIndex = $idx; AdminStatus = $finalAdapter.AdminStatus } | ConvertTo-Json -Compress
""";
        await RunPowerShellAsync(script, $"PowerShell cleanup ensure adapter enabled idx={adapter.InterfaceIndex} name={adapter.Name}", ct);
    }

    private async Task<string> GetAdapterStatusAsync(NetworkAdapterInfo adapter, CancellationToken ct)
    {
        var script = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8" + Environment.NewLine
            + "$OutputEncoding = [Console]::OutputEncoding" + Environment.NewLine
            + BuildAdapterLookup(adapter) + Environment.NewLine
            + "[string]$netAdapter.AdminStatus";
        var status = (await RunPowerShellOutputAsync(script, $"PowerShell read adapter administrative status idx={adapter.InterfaceIndex}", ct, false)).Trim();
        if (string.IsNullOrWhiteSpace(status)) throw new InvalidDataException("PowerShell returned no adapter status.");
        return status;
    }

    private Task SetAdapterAdministrativeStateAsync(NetworkAdapterInfo adapter, bool enabled, CancellationToken ct)
    {
        var targetAdminStatus = enabled ? "Up" : "Down";
        var transition = enabled
            ? "if ($netAdapter.AdminStatus -ne 'Up') { Enable-NetAdapter -InputObject $netAdapter -Confirm:$false -ErrorAction Stop }"
            : "if ($netAdapter.AdminStatus -ne 'Down') { Disable-NetAdapter -InputObject $netAdapter -Confirm:$false -ErrorAction Stop }";
        var script = "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8" + Environment.NewLine
            + "$OutputEncoding = [Console]::OutputEncoding" + Environment.NewLine
            + BuildAdapterLookup(adapter) + Environment.NewLine
            + transition + Environment.NewLine
            + "$finalAdapter = Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop" + Environment.NewLine
            + $"if ($finalAdapter.AdminStatus -ne '{targetAdminStatus}') {{ throw \"Adapter $idx administrative state did not become {targetAdminStatus}; current state is $($finalAdapter.AdminStatus).\" }}" + Environment.NewLine
            + "[string]$finalAdapter.AdminStatus";
        var action = enabled ? "enabled" : "disabled";
        return RunPowerShellAsync(script, $"PowerShell set adapter administrative state {action} idx={adapter.InterfaceIndex}", ct);
    }

    public async Task ChangeMacAddressAsync(NetworkAdapterInfo adapter, string macAddress, bool allowAnyAdapter, CancellationToken ct = default)
    {
        EnsureControlTargetAdapter(adapter, allowAnyAdapter);
        var normalized = NormalizeMacAddress(macAddress);
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$mac={{PsQuote(normalized)}}
{{BuildAdapterLookup(adapter)}}
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
        var parsedIp = ValidateIpv4(ip, "adapter IP");
        var parsedMask = ValidateIpv4(mask, "subnet mask");
        var prefix = IpNetwork.PrefixLength(IPAddress.Parse(parsedMask));
        var expectedMask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        if (IpNetwork.ToUInt32(IPAddress.Parse(parsedMask)) != expectedMask)
            throw new InvalidDataException($"Invalid subnet mask: {mask}");
        ValidateOptionalIpv4(gateway, "gateway");
        ValidateOptionalIpv4(dns, "DNS");
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
{{BuildAdapterLookup(adapter)}}
Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Disabled -ErrorAction Stop
foreach ($address in @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object { [int]$_.InterfaceIndex -eq $idx })) {
  $address | Remove-NetIPAddress -Confirm:$false -ErrorAction Stop
}
foreach ($route in @(Get-NetRoute -AddressFamily IPv4 -ErrorAction Stop | Where-Object { [int]$_.InterfaceIndex -eq $idx -and [string]$_.DestinationPrefix -eq '0.0.0.0/0' })) {
  $route | Remove-NetRoute -Confirm:$false -ErrorAction Stop
}
New-NetIPAddress -InterfaceIndex $idx -IPAddress {{PsQuote(parsedIp)}} -PrefixLength {{prefix}} -ErrorAction Stop | Out-Null
Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {{DhcpHostAdapterMetric}} -ErrorAction Stop
Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses -ErrorAction Stop
'OK'
""";
        await RunPowerShellAsync(script, "PowerShell action apply target adapter static IPv4", ct);
    }

    public async Task RunWithIPv4RecoveryAsync(
        NetworkAdapterInfo adapter,
        AdapterIpv4Snapshot originalSnapshot,
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken operationToken,
        Action? onRestored = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(originalSnapshot);
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrWhiteSpace(adapter.Id)) throw new InvalidOperationException("A stable adapter ID is required before a recoverable IPv4 change.");
        originalSnapshot.NormalizeLegacyFields();
        if (originalSnapshot.DnsMode == AdapterDnsMode.Unknown || !originalSnapshot.AdapterEnabled.HasValue)
            throw new InvalidDataException("The original adapter snapshot is incomplete; no IPv4 change was attempted.");

        try
        {
            // Mark the operation as potentially mutating before entering the system command.
            // A PowerShell command can change Windows state and then fail before returning.
            await operation(operationToken);
        }
        catch (Exception operationError)
        {
            try
            {
                using var compensation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await RestoreIPv4ConfigAsync(adapter, originalSnapshot, compensation.Token);
                var restored = await CaptureIPv4ConfigAsync(adapter, compensation.Token);
                if (!AdapterIpv4SnapshotComparer.Equivalent(originalSnapshot, restored))
                    throw new InvalidOperationException("IPv4 compensation readback did not match the saved adapter snapshot.");
                onRestored?.Invoke();
                _logger.Warn($"{operationName} failed after its write attempt; the original adapter snapshot was restored and verified");
            }
            catch (Exception compensationError)
            {
                _logger.Error($"{operationName} failed and IPv4 compensation could not be verified; recovery data must remain pending", compensationError);
                throw new AggregateException($"{operationName} failed and compensation also failed. The saved recovery record must be retained.", operationError, compensationError);
            }

            ExceptionDispatchInfo.Capture(operationError).Throw();
            throw;
        }
    }

    public async Task RunWithMacRecoveryAsync(
        NetworkAdapterInfo adapter,
        string originalMacAddress,
        Func<CancellationToken, Task> operation,
        Func<string, CancellationToken, Task> verifyRestored,
        string operationName,
        CancellationToken operationToken,
        Action? onRestored = null,
        Action<Exception>? onRecoveryRequired = null)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(verifyRestored);
        if (string.IsNullOrWhiteSpace(adapter.Id)) throw new InvalidOperationException("A stable adapter ID is required before a recoverable MAC change.");
        var normalizedOriginal = NormalizeMacAddress(originalMacAddress);

        try
        {
            // A failed PowerShell response can follow a successful Set-NetAdapter write.
            await operation(operationToken);
        }
        catch (Exception operationError)
        {
            try
            {
                using var compensation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await RestoreMacAddressAsync(adapter, normalizedOriginal, compensation.Token);
                await verifyRestored(normalizedOriginal, compensation.Token);
                onRestored?.Invoke();
                _logger.Warn($"{operationName} failed after its write attempt; the original MAC was restored and verified");
            }
            catch (Exception compensationError)
            {
                try { onRecoveryRequired?.Invoke(compensationError); }
                catch (Exception recordError)
                {
                    compensationError = new AggregateException("MAC restoration failed and its recovery record could not be updated.", compensationError, recordError);
                }
                _logger.Error($"{operationName} failed and MAC compensation could not be verified; recovery data must remain pending", compensationError);
                throw new AggregateException($"{operationName} failed and MAC compensation also failed. The saved recovery record must be retained.", operationError, compensationError);
            }

            ExceptionDispatchInfo.Capture(operationError).Throw();
            throw;
        }
    }

    public async Task RestoreDhcpAsync(NetworkAdapterInfo adapter, CancellationToken ct = default)
    {
        EnsureAllowedTargetAdapter(adapter);
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
{{BuildAdapterLookup(adapter)}}
Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Enabled -ErrorAction Stop
Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction Stop
Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses -ErrorAction Stop
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
{{BuildAdapterLookup(adapter)}}
$ipif = Get-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction Stop
$ips = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object { [int]$_.InterfaceIndex -eq $idx -and $_.IPAddress -notlike '169.254.*' } | Sort-Object SkipAsSource,IPAddress | ForEach-Object {
  [pscustomobject]@{ IpAddress = $_.IPAddress; PrefixLength = [int]$_.PrefixLength; SkipAsSource = [bool]$_.SkipAsSource }
})
$routes = @(Get-NetRoute -AddressFamily IPv4 -ErrorAction Stop | Where-Object { [int]$_.InterfaceIndex -eq $idx -and [string]$_.DestinationPrefix -eq '0.0.0.0/0' } | Sort-Object RouteMetric,NextHop | ForEach-Object {
  [pscustomobject]@{ DestinationPrefix = $_.DestinationPrefix; NextHop = $_.NextHop; RouteMetric = [int]$_.RouteMetric; PolicyStore = [string]$_.PolicyStore; Protocol = [string]$_.Protocol }
})
$dns = @(Get-DnsClientServerAddress -InterfaceIndex $idx -AddressFamily IPv4 -ErrorAction Stop).ServerAddresses
$dnsMode = 0
try {
  $adapterGuid = [guid]$netAdapter.InterfaceGuid
  $tcpipKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\' + $adapterGuid.ToString('B')
  $nameServer = (Get-ItemProperty -LiteralPath $tcpipKey -ErrorAction Stop).NameServer
  $dnsMode = if ([string]::IsNullOrWhiteSpace([string]$nameServer)) { 1 } else { 2 }
} catch { $dnsMode = 0 }
[pscustomobject]@{
  DhcpEnabled = ($ipif.Dhcp -eq 'Enabled')
  Addresses = [object[]]$ips
  Routes = [object[]]$routes
  IpAddress = if ($ips.Count -gt 0) { $ips[0].IpAddress } else { '' }
  PrefixLength = if ($ips.Count -gt 0) { [int]$ips[0].PrefixLength } else { 24 }
  Gateway = if ($routes.Count -gt 0) { $routes[0].NextHop } else { '' }
  Dns = @($dns)
  DnsMode = [int]$dnsMode
  AdapterEnabled = [bool]($netAdapter.AdminStatus -eq 'Up')
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
        var restoreDnsCommand = BuildRestoreDnsCommand(snapshot);
        var restoreAdminCommand = BuildRestoreAdministrativeStateCommand(snapshot.AdapterEnabled);
        var staticRoutes = GetRoutesToRestore(snapshot);
        var routeCommands = string.Join(Environment.NewLine, staticRoutes.Select(BuildRouteRestoreCommand));
        if (snapshot.DhcpEnabled)
        {
            if (snapshot.Routes.Any(x => string.IsNullOrWhiteSpace(x.Protocol)))
                throw new InvalidDataException("This older adapter backup does not identify which default routes were assigned by DHCP. It was preserved; use a fresh snapshot before retrying.");
            var metricScript = snapshot.AutomaticMetric
                ? "Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction Stop"
                : $"Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {Math.Max(1, snapshot.InterfaceMetric)} -ErrorAction Stop";
            var dhcpScript = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
{{BuildAdapterLookup(adapter)}}
Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Disabled -ErrorAction Stop
foreach ($address in @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object { [int]$_.InterfaceIndex -eq $idx })) {
  $address | Remove-NetIPAddress -Confirm:$false -ErrorAction Stop
}
foreach ($route in @(Get-NetRoute -AddressFamily IPv4 -ErrorAction Stop | Where-Object { [int]$_.InterfaceIndex -eq $idx -and [string]$_.DestinationPrefix -eq '0.0.0.0/0' })) {
  $route | Remove-NetRoute -Confirm:$false -ErrorAction Stop
}
Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Enabled -ErrorAction Stop
{{metricScript}}
{{restoreDnsCommand}}
{{routeCommands}}
{{restoreAdminCommand}}
'OK'
""";
            await RunPowerShellAsync(dhcpScript, "PowerShell action restore target adapter original DHCP", ct);
            return;
        }

        var metricRestore = snapshot.AutomaticMetric
            ? "Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Enabled -ErrorAction Stop"
            : $"Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {Math.Max(1, snapshot.InterfaceMetric)} -ErrorAction Stop";
        var addressCommands = string.Join(Environment.NewLine, snapshot.Addresses.Select(address =>
            $"New-NetIPAddress -InterfaceIndex $idx -IPAddress {PsQuote(address.IpAddress)} -PrefixLength {ValidatePrefix(address.PrefixLength)} -SkipAsSource ${address.SkipAsSource.ToString().ToLowerInvariant()} -ErrorAction Stop | Out-Null"));
        var staticRouteCommands = string.Join(Environment.NewLine, snapshot.Routes.Select(BuildRouteRestoreCommand));
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
{{BuildAdapterLookup(adapter)}}
Set-NetIPInterface -InterfaceIndex $idx -AddressFamily IPv4 -Dhcp Disabled -ErrorAction Stop
foreach ($address in @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object { [int]$_.InterfaceIndex -eq $idx })) {
  $address | Remove-NetIPAddress -Confirm:$false -ErrorAction Stop
}
foreach ($route in @(Get-NetRoute -AddressFamily IPv4 -ErrorAction Stop | Where-Object { [int]$_.InterfaceIndex -eq $idx -and [string]$_.DestinationPrefix -eq '0.0.0.0/0' })) {
  $route | Remove-NetRoute -Confirm:$false -ErrorAction Stop
}
{{addressCommands}}
{{staticRouteCommands}}
{{metricRestore}}
{{restoreDnsCommand}}
{{restoreAdminCommand}}
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

    public Task<DhcpFirewallRuleLease> EnsureDhcpFirewallRulesAsync(string interfaceAlias, CancellationToken ct = default) =>
        EnsureDhcpFirewallRulesAsync(interfaceAlias, null, ct);

    public async Task<DhcpFirewallRuleLease> EnsureDhcpFirewallRulesAsync(
        string interfaceAlias,
        Action<DhcpFirewallRuleLease>? onLeaseChanged,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(interfaceAlias)) throw new ArgumentException("A selected adapter alias is required to scope the DHCP firewall rules.", nameof(interfaceAlias));
        var programPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrWhiteSpace(programPath)) throw new InvalidOperationException("The application path could not be determined; DHCP firewall rules were not created.");

        var lease = new DhcpFirewallRuleLease
        {
            LeaseId = Guid.NewGuid().ToString("N"),
            InterfaceAlias = interfaceAlias,
            ProgramPath = Path.GetFullPath(programPath),
            CreatedAt = DateTimeOffset.Now
        };
        lease.Rules.Add(CreateDhcpFirewallRuleRecord(lease, "Inbound", localPort: "67"));
        lease.Rules.Add(CreateDhcpFirewallRuleRecord(lease, "Outbound", remotePort: "68"));

        // The durable callback must succeed before the first system mutation.
        onLeaseChanged?.Invoke(lease);
        foreach (var rule in lease.Rules)
        {
            ct.ThrowIfCancellationRequested();
            var script = BuildCreateDhcpFirewallRuleScript(rule);
            var output = await RunPowerShellOutputAsync(script, $"PowerShell action create DHCP firewall rule {rule.Name}", ct, false);
            var readback = JsonSerializer.Deserialize<DhcpFirewallRuleReadback>(output, JsonStore.Options)
                ?? throw new InvalidDataException("Firewall rule readback was empty.");
            if (!string.Equals(readback.Name, rule.Name, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(readback.InstanceId))
                throw new InvalidDataException("Firewall rule readback did not prove the requested rule identity.");
            rule.InstanceId = readback.InstanceId;
            rule.OwnershipVerified = true;
            rule.VerifiedAt = DateTimeOffset.Now;
            onLeaseChanged?.Invoke(lease);
        }
        return lease;
    }

    public Task RemoveDhcpFirewallRulesAsync(DhcpFirewallRuleLease? lease, CancellationToken ct = default) =>
        RemoveDhcpFirewallRulesAsync(lease, null, ct);

    public async Task RemoveDhcpFirewallRulesAsync(
        DhcpFirewallRuleLease? lease,
        Action<DhcpFirewallRuleLease>? onLeaseChanged,
        CancellationToken ct = default)
    {
        if (lease == null || lease.Rules.Count == 0) return;
        foreach (var rule in lease.Rules.ToList())
        {
            ct.ThrowIfCancellationRequested();
            var script = BuildRemoveDhcpFirewallRuleScript(rule);
            await RunPowerShellAsync(script, $"PowerShell action remove owned DHCP firewall rule {rule.Name}", ct);
            lease.Rules.Remove(rule);
            onLeaseChanged?.Invoke(lease);
        }
    }

    private static DhcpFirewallRuleRecord CreateDhcpFirewallRuleRecord(DhcpFirewallRuleLease lease, string direction, string localPort = "Any", string remotePort = "Any")
    {
        var ruleId = Guid.NewGuid().ToString("N");
        var description = $"NetBootDhcpTool Owner={lease.LeaseId} Rule={ruleId}";
        return new DhcpFirewallRuleRecord
        {
            Name = $"NetBootDhcpTool-{ruleId}",
            DisplayName = $"NetBoot DHCP Tool {direction} {ruleId[..8]}",
            Description = description,
            Group = "NetBootDhcpTool",
            Direction = direction,
            Protocol = "UDP",
            LocalPort = localPort,
            RemotePort = remotePort,
            InterfaceAlias = lease.InterfaceAlias,
            ProgramPath = lease.ProgramPath,
            LeaseId = lease.LeaseId
        };
    }

    private static string BuildCreateDhcpFirewallRuleScript(DhcpFirewallRuleRecord rule)
    {
        var localPort = rule.LocalPort == "Any" ? "" : $" -LocalPort {PsQuote(rule.LocalPort)}";
        var remotePort = rule.RemotePort == "Any" ? "" : $" -RemotePort {PsQuote(rule.RemotePort)}";
        var expectedLocal = PsQuote(rule.LocalPort);
        var expectedRemote = PsQuote(rule.RemotePort);
        return $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$name={{PsQuote(rule.Name)}}; $display={{PsQuote(rule.DisplayName)}}; $description={{PsQuote(rule.Description)}}
$group={{PsQuote(rule.Group)}}; $direction={{PsQuote(rule.Direction)}}; $interface={{PsQuote(rule.InterfaceAlias)}}; $program={{PsQuote(rule.ProgramPath)}}
$localPort={{expectedLocal}}; $remotePort={{expectedRemote}}; $owner={{PsQuote(rule.LeaseId)}}
if (@(Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Firewall rule identity collision; no existing rule was adopted.' }
$createArgs=@{ Name=$name; DisplayName=$display; Description=$description; Group=$group; Direction=$direction; Action='Allow'; Protocol='UDP'; Profile='Any'; Program=$program; InterfaceAlias=$interface; ErrorAction='Stop' }
if ($localPort -ne 'Any') { $createArgs.LocalPort=$localPort }
if ($remotePort -ne 'Any') { $createArgs.RemotePort=$remotePort }
New-NetFirewallRule @createArgs | Out-Null
$rules=@(Get-NetFirewallRule -Name $name -ErrorAction Stop)
if ($rules.Count -ne 1) { throw 'Firewall rule readback did not return exactly one rule.' }
$rule=$rules[0]
$port=@(Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop)
$app=@(Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop)
$iface=@(Get-NetFirewallInterfaceFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop)
$actualLocal=if ($port.Count -eq 1) { (@($port[0].LocalPort) -join ',') } else { '' }
$actualRemote=if ($port.Count -eq 1) { (@($port[0].RemotePort) -join ',') } else { '' }
$protocol=if ($port.Count -eq 1) { [string]$port[0].Protocol } else { '' }
$actualProgram=if ($app.Count -eq 1) { [string]$app[0].Program } else { '' }
$actualInterfaces=@()
if ($iface.Count -eq 1) { $actualInterfaces=@($iface[0].InterfaceAlias) }
$protocolOk=($protocol -ieq 'UDP' -or $protocol -eq '17')
$valid=$rule.Name -ceq $name -and $rule.DisplayName -ceq $display -and $rule.Description -ceq $description -and $rule.Group -ceq $group -and
  [string]$rule.Direction -ieq $direction -and [string]$rule.Action -ieq 'Allow' -and [bool]$rule.Enabled -and [string]$rule.Profile -ieq 'Any' -and $protocolOk -and
  $actualLocal -ceq $localPort -and $actualRemote -ceq $remotePort -and $actualProgram -ieq $program -and
  $actualInterfaces.Count -eq 1 -and $actualInterfaces[0] -ieq $interface
if (-not $valid) { throw 'Firewall rule readback did not match the requested owner and scope.' }
[pscustomobject]@{ Name=[string]$rule.Name; InstanceId=[string]$rule.InstanceID } | ConvertTo-Json -Compress
""";
    }

    private static string BuildRemoveDhcpFirewallRuleScript(DhcpFirewallRuleRecord rule) => $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$name={{PsQuote(rule.Name)}}; $display={{PsQuote(rule.DisplayName)}}; $description={{PsQuote(rule.Description)}}
$group={{PsQuote(rule.Group)}}; $direction={{PsQuote(rule.Direction)}}; $interface={{PsQuote(rule.InterfaceAlias)}}; $program={{PsQuote(rule.ProgramPath)}}
$localPort={{PsQuote(rule.LocalPort)}}; $remotePort={{PsQuote(rule.RemotePort)}}; $leaseId={{PsQuote(rule.LeaseId)}}; $instanceId={{PsQuote(rule.InstanceId)}}
$rules=@(Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue)
if ($rules.Count -eq 0) { 'OK'; exit 0 }
if ($rules.Count -ne 1) { throw 'Firewall rule identity is ambiguous; journal retained.' }
$rule=$rules[0]
$port=@(Get-NetFirewallPortFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop)
$app=@(Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop)
$iface=@(Get-NetFirewallInterfaceFilter -AssociatedNetFirewallRule $rule -ErrorAction Stop)
$actualLocal=if ($port.Count -eq 1) { (@($port[0].LocalPort) -join ',') } else { '' }
$actualRemote=if ($port.Count -eq 1) { (@($port[0].RemotePort) -join ',') } else { '' }
$protocol=if ($port.Count -eq 1) { [string]$port[0].Protocol } else { '' }
$actualProgram=if ($app.Count -eq 1) { [string]$app[0].Program } else { '' }
$actualInterfaces=@()
if ($iface.Count -eq 1) { $actualInterfaces=@($iface[0].InterfaceAlias) }
$protocolOk=($protocol -ieq 'UDP' -or $protocol -eq '17')
$identityOk=($rule.Name -ceq $name -and $rule.DisplayName -ceq $display -and $rule.Description -ceq $description -and $rule.Description -match [regex]::Escape("Owner=$leaseId") -and $rule.Group -ceq $group)
$instanceOk=([string]::IsNullOrWhiteSpace($instanceId) -or [string]$rule.InstanceID -ieq $instanceId)
$valid=$identityOk -and $instanceOk -and [string]$rule.Direction -ieq $direction -and [string]$rule.Action -ieq 'Allow' -and [bool]$rule.Enabled -and [string]$rule.Profile -ieq 'Any' -and $protocolOk -and
  $actualLocal -ceq $localPort -and $actualRemote -ceq $remotePort -and $actualProgram -ieq $program -and
  $actualInterfaces.Count -eq 1 -and $actualInterfaces[0] -ieq $interface
if (-not $valid) { throw 'Firewall rule ownership or attributes changed; journal retained.' }
$rule | Remove-NetFirewallRule -Confirm:$false -ErrorAction Stop
if (@(Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Firewall rule still exists after removal; journal retained.' }
'OK'
""";

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
        if (_powerShellScriptExecutor is not null)
        {
            var testOutput = await _powerShellScriptExecutor(script, summary, ct, logOutput);
            if (logOutput) _logger.Info($"{summary} result: exit=0 detail={SummarizePowerShellMessage(testOutput.Trim())}");
            return testOutput;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var operationToken = timeoutCts.Token;
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ResolvePowerShellPath(),
            Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        process.Start();
        try
        {
            var (output, error) = await PowerShellProcessOutput.ReadStandardStreamsAsync(process, operationToken);
            await process.WaitForExitAsync(operationToken);

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
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            _logger.Warn($"{summary} aborted or timed out");
            throw;
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            throw;
        }
    }

    private static string SummarizePowerShellMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "OK";
        var normalized = string.Join(" | ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length > 240) normalized = normalized[..240] + "...";
        return normalized;
    }

    private static string ResolvePowerShellPath()
    {
        var systemPowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(systemPowerShell)) throw new InvalidOperationException("Windows PowerShell was not found / 未找到 Windows PowerShell");
        return systemPowerShell;
    }

    private static void EnsureAllowedTargetAdapter(NetworkAdapterInfo adapter)
    {
        EnsureInterfaceIndex(adapter);
        if (adapter.IsWifi || IsWifiLike(adapter.Name, adapter.Description, null)) throw new InvalidOperationException("Refusing to modify WLAN adapter");
    }

    private static void EnsureControlTargetAdapter(NetworkAdapterInfo adapter, bool allowAnyAdapter)
    {
        EnsureInterfaceIndex(adapter);
        if (allowAnyAdapter) return;
        if (adapter.IsWifi || IsWifiLike(adapter.Name, adapter.Description, null) || adapter.IsVirtual)
            throw new InvalidOperationException("设置当前禁止操作无线或虚拟网卡");
        if (!adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("设置当前禁止操作未启用的网卡");
    }

    private static void EnsureInterfaceIndex(NetworkAdapterInfo adapter)
    {
        if (!int.TryParse(adapter.InterfaceIndex, out var index) || index <= 0)
            throw new InvalidOperationException("InterfaceIndex is invalid");
    }

    private static string BuildAdapterLookup(NetworkAdapterInfo adapter)
    {
        EnsureInterfaceIndex(adapter);
        if (!Guid.TryParse(adapter.Id, out var adapterGuid))
            throw new InvalidOperationException("A stable adapter identity is required before modifying or restoring this adapter.");
        return $"$idx={int.Parse(adapter.InterfaceIndex, System.Globalization.CultureInfo.InvariantCulture)}{Environment.NewLine}"
            + $"$expectedGuid=[guid]{PsQuote(adapterGuid.ToString("B"))}{Environment.NewLine}"
            + "$netAdapter = Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop" + Environment.NewLine
            + "if ([guid]$netAdapter.InterfaceGuid -ne $expectedGuid) { throw \"The interface index now belongs to a different adapter.\" }";
    }

    private static string BuildRestoreDnsCommand(AdapterIpv4Snapshot snapshot)
    {
        return snapshot.DnsMode switch
        {
            AdapterDnsMode.Automatic => "Set-DnsClientServerAddress -InterfaceIndex $idx -ResetServerAddresses -ErrorAction Stop",
            AdapterDnsMode.Static when snapshot.Dns.Count > 0 && snapshot.Dns.All(IsValidIpv4) =>
                "Set-DnsClientServerAddress -InterfaceIndex $idx -ServerAddresses @("
                + string.Join(",", snapshot.Dns.Select(PsQuote)) + ") -ErrorAction Stop",
            AdapterDnsMode.Static => throw new InvalidDataException("The saved manual DNS configuration is empty or invalid; the backup was preserved and no changes were made."),
            _ => throw new InvalidDataException("This older adapter backup does not record whether DNS was automatic or manual. It was preserved and cannot be restored safely.")
        };
    }

    private static bool IsValidIpv4(string value) =>
        IPAddress.TryParse(value, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    private static IReadOnlyList<AdapterRouteSnapshot> GetRoutesToRestore(AdapterIpv4Snapshot snapshot) =>
        snapshot.DhcpEnabled
            ? snapshot.Routes.Where(x => !x.Protocol.Equals("Dhcp", StringComparison.OrdinalIgnoreCase)).ToArray()
            : snapshot.Routes;

    private static string BuildRestoreAdministrativeStateCommand(bool? enabled) => enabled switch
    {
        true => "$currentAdapter = Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop; if ($currentAdapter.AdminStatus -ne 'Up') { Enable-NetAdapter -InputObject $currentAdapter -Confirm:$false -ErrorAction Stop }; $restoredAdapter = Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop; if ($restoredAdapter.AdminStatus -ne 'Up') { throw \"Adapter $idx was not restored to administrative state Up.\" }",
        false => "$currentAdapter = Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop; if ($currentAdapter.AdminStatus -ne 'Down') { Disable-NetAdapter -InputObject $currentAdapter -Confirm:$false -ErrorAction Stop }; $restoredAdapter = Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop; if ($restoredAdapter.AdminStatus -ne 'Down') { throw \"Adapter $idx was not restored to administrative state Down.\" }",
        null => ""
    };

    private static bool IsAdministrativeStateDisabled(string status) =>
        status.Equals("Down", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Disabled", StringComparison.OrdinalIgnoreCase);

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

    private static void ValidateOptionalIpv4(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        _ = ValidateIpv4(value, field);
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
    public AdapterDnsMode DnsMode { get; set; }
    public bool? AdapterEnabled { get; set; }
    public bool AutomaticMetric { get; set; } = true;
    public int InterfaceMetric { get; set; } = 0;

    public void NormalizeLegacyFields()
    {
        Addresses ??= [];
        Routes ??= [];
        Dns ??= [];
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

public sealed record AdapterRestartResult(bool WasDisabledBefore, string FinalStatus);

public sealed class DhcpFirewallRuleLease
{
    public string LeaseId { get; set; } = "";
    public string InterfaceAlias { get; set; } = "";
    public string ProgramPath { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<DhcpFirewallRuleRecord> Rules { get; set; } = [];
}

public sealed class DhcpFirewallRuleRecord
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Group { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Protocol { get; set; } = "UDP";
    public string LocalPort { get; set; } = "Any";
    public string RemotePort { get; set; } = "Any";
    public string InterfaceAlias { get; set; } = "";
    public string ProgramPath { get; set; } = "";
    public string LeaseId { get; set; } = "";
    public string InstanceId { get; set; } = "";
    public bool OwnershipVerified { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

public sealed class DhcpFirewallRuleReadback
{
    public string Name { get; set; } = "";
    public string InstanceId { get; set; } = "";
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
