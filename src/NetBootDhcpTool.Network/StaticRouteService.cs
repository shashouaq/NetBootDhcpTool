using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Network;

public sealed class StaticRouteTarget
{
    public StaticRouteTarget(StaticRouteRule rule, NetworkAdapterInfo adapter, ValidatedStaticRoute route)
    {
        Rule = rule;
        Adapter = adapter;
        Route = route;
    }

    public StaticRouteRule Rule { get; }
    public NetworkAdapterInfo Adapter { get; }
    public ValidatedStaticRoute Route { get; }
}

public sealed class StaticRouteApplyResult
{
    public StaticRouteApplyResult(StaticRouteRule rule, bool created, string statusText, AppliedStaticRoute? applied)
    {
        Rule = rule;
        Created = created;
        StatusText = statusText;
        Applied = applied;
    }

    public StaticRouteRule Rule { get; }
    public bool Created { get; }
    public string StatusText { get; }
    public AppliedStaticRoute? Applied { get; }
}

public sealed class StaticRouteService
{
    private readonly ILogger _logger;

    public StaticRouteService(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<StaticRouteApplyResult>> ApplyAsync(IReadOnlyList<StaticRouteTarget> targets, CancellationToken ct = default)
    {
        if (targets.Count == 0) throw new InvalidOperationException("No static routes to apply / 没有可应用的静态路由");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            EnsureApplyTarget(target.Adapter);
            var key = RouteKey(target.Route.DestinationPrefix, target.Adapter.InterfaceIndex, target.Route.NextHop);
            if (!seen.Add(key)) throw new InvalidOperationException($"Duplicate route target: {target.Route.DestinationPrefix} on {target.Adapter.Name} / 重复路由目标：{target.Route.DestinationPrefix} 指向 {target.Adapter.Name}");
        }

        var created = new List<(AppliedStaticRoute Route, NetworkAdapterInfo Adapter)>();
        var results = new List<StaticRouteApplyResult>();
        try
        {
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();
                var existing = await GetExactRoutesAsync(target.Adapter.InterfaceIndex, target.Route, ct);
                if (existing.Count > 0)
                {
                    if (existing.All(IsSystemManaged))
                    {
                        results.Add(new StaticRouteApplyResult(target.Rule, false, "Already present / 已存在（系统路由）", null));
                        _logger.Info($"Static route already present: {target.Route.DestinationPrefix} via {target.Adapter.Name}");
                        continue;
                    }

                    throw new InvalidOperationException($"An existing route matches {target.Route.DestinationPrefix} on {target.Adapter.Name}; it was not changed. / 已存在匹配路由，未修改：{target.Route.DestinationPrefix} - {target.Adapter.Name}");
                }

                var applied = await CreateRouteAsync(target, ct);
                created.Add((applied, target.Adapter));
                results.Add(new StaticRouteApplyResult(target.Rule, true, "Applied / 已应用", applied));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Static route batch failed; rolling back {created.Count} route(s) / 静态路由批处理失败，正在回滚 {created.Count} 条路由");
            foreach (var item in created.AsEnumerable().Reverse())
            {
                try
                {
                    await RemoveAsync(item.Route, item.Adapter, CancellationToken.None);
                }
                catch (Exception rollbackEx)
                {
                    _logger.Error("Static route rollback failed", rollbackEx);
                }
            }

            throw new InvalidOperationException("Static route batch failed and was rolled back where possible / 静态路由批处理失败，已尽力回滚", ex);
        }
    }

    public async Task RemoveAsync(AppliedStaticRoute route, NetworkAdapterInfo adapter, CancellationToken ct = default)
    {
        EnsureCleanupTarget(route, adapter);
        var instanceId = PsQuote(route.InstanceId ?? "");
        var destination = PsQuote(route.DestinationPrefix);
        var nextHop = PsQuote(route.NextHop);
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{route.InterfaceIndex}}
$destination={{destination}}
$nextHop={{nextHop}}
$instanceId={{instanceId}}
$matches = @(Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix $destination -ErrorAction SilentlyContinue |
  Where-Object { [string]$_.NextHop -eq $nextHop -and [int]$_.RouteMetric -eq {{route.RouteMetric}} })
$selected = $matches
if (-not [string]::IsNullOrWhiteSpace($instanceId)) {
  $byInstance = @($matches | Where-Object { [string]$_.InstanceId -eq $instanceId })
  if ($byInstance.Count -gt 0) { $selected = $byInstance }
}
if ($selected.Count -gt 0) { $selected | Remove-NetRoute -Confirm:$false -ErrorAction Stop }
$remaining = @(Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix $destination -ErrorAction SilentlyContinue |
  Where-Object { [string]$_.NextHop -eq $nextHop -and [int]$_.RouteMetric -eq {{route.RouteMetric}} })
if ($remaining.Count -gt 0) { throw 'Route still exists after removal' }
'OK'
""";
        await RunPowerShellAsync(script, $"PowerShell action remove static route {route.DestinationPrefix} on {adapter.Name}", ct);
    }

    public async Task<bool> ExistsAsync(AppliedStaticRoute route, NetworkAdapterInfo adapter, CancellationToken ct = default)
    {
        EnsureCleanupTarget(route, adapter);
        var validated = new ValidatedStaticRoute(route.DestinationPrefix, route.NextHop, route.RouteMetric, route.DestinationPrefix.EndsWith("/0", StringComparison.Ordinal));
        return (await GetExactRoutesAsync(adapter.InterfaceIndex, validated, ct)).Count > 0;
    }

    private async Task<AppliedStaticRoute> CreateRouteAsync(StaticRouteTarget target, CancellationToken ct)
    {
        var destination = PsQuote(target.Route.DestinationPrefix);
        var nextHop = PsQuote(target.Route.NextHop);
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{target.Adapter.InterfaceIndex}}
$destination={{destination}}
$nextHop={{nextHop}}
New-NetRoute -DestinationPrefix $destination -InterfaceIndex $idx -AddressFamily IPv4 -NextHop $nextHop -RouteMetric {{target.Route.RouteMetric}} -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
$route = @(Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix $destination -ErrorAction Stop |
  Where-Object { [string]$_.NextHop -eq $nextHop -and [int]$_.RouteMetric -eq {{target.Route.RouteMetric}} } |
  ForEach-Object {
    [pscustomobject]@{
      DestinationPrefix = [string]$_.DestinationPrefix
      NextHop = [string]$_.NextHop
      RouteMetric = [int]$_.RouteMetric
      InterfaceIndex = [int]$_.InterfaceIndex
      InterfaceAlias = [string]$_.InterfaceAlias
      PolicyStore = if ($_.PolicyStore) { [string]$_.PolicyStore } elseif ($_.Store) { [string]$_.Store } else { '' }
      Protocol = [string]$_.Protocol
      InstanceId = [string]$_.InstanceId
    }
  } | Select-Object -First 1)
if ($route.Count -eq 0) { throw 'Created route could not be verified' }
ConvertTo-Json -InputObject $route[0] -Compress -Depth 4
""";
        var output = await RunPowerShellOutputAsync(script, $"PowerShell action create static route {target.Route.DestinationPrefix} on {target.Adapter.Name}", ct, true);
        var snapshot = ParseRoutes(output).FirstOrDefault() ?? throw new InvalidOperationException("Created route could not be parsed / 创建的路由无法解析");
        var applied = new AppliedStaticRoute
        {
            RuleId = target.Rule.Id,
            DestinationPrefix = target.Route.DestinationPrefix,
            AdapterId = target.Adapter.Id,
            AdapterName = target.Adapter.Name,
            AdapterMac = target.Adapter.MacAddress,
            InterfaceIndex = target.Adapter.InterfaceIndex is { } index && int.TryParse(index, out var parsedIndex) ? parsedIndex : 0,
            NextHop = target.Route.NextHop,
            RouteMetric = target.Route.RouteMetric,
            PolicyStore = string.IsNullOrWhiteSpace(snapshot.PolicyStore) ? "ActiveStore" : snapshot.PolicyStore,
            InstanceId = snapshot.InstanceId
        };
        _logger.Info($"Static route created: {applied.DestinationPrefix} nextHop={applied.NextHop} adapter={applied.AdapterName} metric={applied.RouteMetric}");
        return applied;
    }

    private async Task<List<RouteSnapshot>> GetExactRoutesAsync(string interfaceIndex, ValidatedStaticRoute route, CancellationToken ct)
    {
        if (!int.TryParse(interfaceIndex, out var index) || index <= 0) throw new InvalidOperationException("Invalid adapter InterfaceIndex / 网卡 InterfaceIndex 无效");
        var destination = PsQuote(route.DestinationPrefix);
        var nextHop = PsQuote(route.NextHop);
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{index}}
$destination={{destination}}
$nextHop={{nextHop}}
$routes = @(Get-NetRoute -InterfaceIndex $idx -AddressFamily IPv4 -DestinationPrefix $destination -ErrorAction SilentlyContinue |
  Where-Object { [string]$_.NextHop -eq $nextHop } |
  ForEach-Object {
    [pscustomobject]@{
      DestinationPrefix = [string]$_.DestinationPrefix
      NextHop = [string]$_.NextHop
      RouteMetric = [int]$_.RouteMetric
      InterfaceIndex = [int]$_.InterfaceIndex
      InterfaceAlias = [string]$_.InterfaceAlias
      PolicyStore = if ($_.PolicyStore) { [string]$_.PolicyStore } elseif ($_.Store) { [string]$_.Store } else { '' }
      Protocol = [string]$_.Protocol
      InstanceId = [string]$_.InstanceId
    }
  })
ConvertTo-Json -InputObject $routes -Compress -Depth 4
""";
        var output = await RunPowerShellOutputAsync(script, $"PowerShell action inspect static route {route.DestinationPrefix}", ct, false);
        return ParseRoutes(output);
    }

    private static bool IsSystemManaged(RouteSnapshot route)
    {
        return route.Protocol.Equals("Local", StringComparison.OrdinalIgnoreCase)
            || route.Protocol.Equals("Dhcp", StringComparison.OrdinalIgnoreCase)
            || route.Protocol.Equals("Kernel", StringComparison.OrdinalIgnoreCase);
    }

    private static string RouteKey(string destination, string interfaceIndex, string nextHop) => $"{destination}|{interfaceIndex}|{nextHop}";

    private static void EnsureApplyTarget(NetworkAdapterInfo adapter)
    {
        if (!int.TryParse(adapter.InterfaceIndex, out var index) || index <= 0) throw new InvalidOperationException("InterfaceIndex missing / 缺少网卡 InterfaceIndex");
        if (adapter.IsWifi || IsWifiLike(adapter.Name, adapter.Description)) throw new InvalidOperationException("Refusing to modify WLAN adapter / 拒绝修改 WLAN 网卡");
        if (adapter.IsVirtual || IsVirtual(adapter.Name, adapter.Description)) throw new InvalidOperationException("Refusing to modify virtual adapter / 拒绝修改虚拟网卡");
        if (!adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The selected adapter is disconnected / 当前选定网卡未连接");
    }

    private static void EnsureCleanupTarget(AppliedStaticRoute route, NetworkAdapterInfo adapter)
    {
        if (route.InterfaceIndex <= 0 || !int.TryParse(adapter.InterfaceIndex, out var index) || index != route.InterfaceIndex) throw new InvalidOperationException("Adapter identity changed; route cleanup skipped / 网卡身份已变化，跳过路由清理");
        if (!string.IsNullOrWhiteSpace(route.AdapterId) && !route.AdapterId.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Adapter identity changed; route cleanup skipped / 网卡身份已变化，跳过路由清理");
        if (adapter.IsWifi || IsWifiLike(adapter.Name, adapter.Description) || adapter.IsVirtual || IsVirtual(adapter.Name, adapter.Description)) throw new InvalidOperationException("Refusing to modify unsafe adapter / 拒绝修改不安全网卡");
    }

    private static bool IsWifiLike(string name, string description)
    {
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
        var text = $"{name} {description}".ToLowerInvariant();
        string[] markers = ["vmware", "virtualbox", "hyper-v", "vpn", "tap", "wsl", "virtual", "loopback"];
        return markers.Any(text.Contains);
    }

    private static string PsQuote(string value) => "'" + (value ?? "").Replace("'", "''") + "'";

    private async Task RunPowerShellAsync(string script, string summary, CancellationToken ct)
    {
        _ = await RunPowerShellOutputAsync(script, summary, ct, true);
    }

    private async Task<string> RunPowerShellOutputAsync(string script, string summary, CancellationToken ct, bool logOutput)
    {
        _logger.Info(summary);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            }
        };
        process.Start();
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(ct);
            var errorTask = process.StandardError.ReadToEndAsync(ct);
            await Task.WhenAll(outputTask, errorTask);
            await process.WaitForExitAsync(ct);
            var output = outputTask.Result.Trim();
            var error = errorTask.Result.Trim();
            if (process.ExitCode != 0)
            {
                var detail = Summarize(error);
                _logger.Warn($"{summary} failed: exit={process.ExitCode} detail={detail}");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"PowerShell exit {process.ExitCode}" : error);
            }
            if (logOutput) _logger.Info($"{summary} result: exit={process.ExitCode} detail={Summarize(output)}");
            return output;
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

    private static string Summarize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "OK";
        var normalized = string.Join(" | ", text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return normalized.Length > 240 ? normalized[..240] + "..." : normalized;
    }

    private static List<RouteSnapshot> ParseRoutes(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        using var document = JsonDocument.Parse(output);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Deserialize<List<RouteSnapshot>>(document.RootElement.GetRawText(), options) ?? []
            : [JsonSerializer.Deserialize<RouteSnapshot>(document.RootElement.GetRawText(), options) ?? new RouteSnapshot()];
    }

    private sealed class RouteSnapshot
    {
        public string DestinationPrefix { get; set; } = "";
        public string NextHop { get; set; } = "";
        public int RouteMetric { get; set; }
        public int InterfaceIndex { get; set; }
        public string InterfaceAlias { get; set; } = "";
        public string PolicyStore { get; set; } = "";
        public string Protocol { get; set; } = "";
        public string InstanceId { get; set; } = "";
    }
}
