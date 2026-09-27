using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
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

public sealed class CurrentStaticRoute
{
    public string AddressFamily { get; set; } = "IPv4";
    public string InterfaceIndex { get; set; } = "";
    public string DestinationPrefix { get; set; } = "";
    public string NextHop { get; set; } = "0.0.0.0";
    public int RouteMetric { get; set; }
    public int InterfaceMetric { get; set; }
    public string Protocol { get; set; } = "";
    public string PolicyStore { get; set; } = "";
    public string InstanceId { get; set; } = "";
}

public sealed class InterfaceMetricInfo
{
    public string AddressFamily { get; set; } = "IPv4";
    public string InterfaceIndex { get; set; } = "";
    public int InterfaceMetric { get; set; }
}

public sealed class InterfaceStateInfo
{
    public string AddressFamily { get; set; } = "IPv4";
    public string InterfaceIndex { get; set; } = "";
    public string ConnectionState { get; set; } = "";
}

public sealed class InterfaceAddressInfo
{
    public string AddressFamily { get; set; } = "IPv4";
    public string InterfaceIndex { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int PrefixLength { get; set; }
}

public sealed class StaticRoutePlanItem
{
    public StaticRoutePlanItem(StaticRouteTarget target, bool shouldCreate, string statusText, int interfaceMetric, string overlapWarning = "")
    {
        Target = target;
        ShouldCreate = shouldCreate;
        StatusText = statusText;
        InterfaceMetric = interfaceMetric;
        OverlapWarning = overlapWarning;
    }

    public StaticRouteTarget Target { get; }
    public bool ShouldCreate { get; }
    public string StatusText { get; }
    public int InterfaceMetric { get; }
    public int EffectiveMetric => Math.Min(int.MaxValue, Math.Max(0, InterfaceMetric) + Math.Max(0, Target.Route.RouteMetric));
    public string OverlapWarning { get; set; }
}

public sealed class StaticRouteService
{
    private readonly ILogger _logger;
    private readonly PowerShellProcessRunner _powerShellProcessRunner;
    private readonly Func<bool> _isAdministrator;

    public StaticRouteService(ILogger logger, PowerShellScriptExecutor? powerShellScriptExecutor = null, Func<bool>? isAdministrator = null)
    {
        _logger = logger;
        _powerShellProcessRunner = new PowerShellProcessRunner(logger, powerShellScriptExecutor);
        _isAdministrator = isAdministrator ?? IsAdministrator;
    }

    public async Task<IReadOnlyList<CurrentStaticRoute>> GetCurrentStaticRoutesAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        _logger.Info("Static route read started: all IPv4 and IPv6 interfaces");
        var script = """
$interfaces = @{}
Get-NetIPInterface -AddressFamily IPv4,IPv6 -ErrorAction Stop | ForEach-Object {
  $key = "{0}|{1}" -f ([string]$_.AddressFamily), ([int]$_.InterfaceIndex)
  $interfaces[$key] = [int]$_.InterfaceMetric
}
$routes = @(Get-NetRoute -AddressFamily IPv4,IPv6 -ErrorAction Stop |
  Sort-Object AddressFamily,InterfaceIndex,DestinationPrefix,RouteMetric,NextHop |
  ForEach-Object {
    $family = [string]$_.AddressFamily
    $index = [int]$_.InterfaceIndex
    $key = "{0}|{1}" -f $family,$index
    [pscustomobject]@{
      AddressFamily = $family
      InterfaceIndex = [string]$index
      DestinationPrefix = [string]$_.DestinationPrefix
      NextHop = [string]$_.NextHop
      RouteMetric = [int]$_.RouteMetric
      InterfaceMetric = if ($interfaces.ContainsKey($key)) { [int]$interfaces[$key] } else { 0 }
      Protocol = [string]$_.Protocol
      PolicyStore = if ($_.PolicyStore) { [string]$_.PolicyStore } elseif ($_.Store) { [string]$_.Store } else { '' }
      InstanceId = [string]$_.InstanceId
    }
  })
ConvertTo-Json -InputObject $routes -Compress -Depth 4
""";
        var output = await RunPowerShellOutputAsync(script, "PowerShell action read current IPv4/IPv6 routes", ct, false);
        var routes = ParseJsonList<CurrentStaticRoute>(output);
        _logger.Info($"Static route read completed: count={routes.Count} elapsedMs={stopwatch.ElapsedMilliseconds}");
        return routes;
    }

    public async Task<IReadOnlyList<InterfaceMetricInfo>> GetInterfaceMetricsAsync(CancellationToken ct = default)
    {
        var script = """
$metrics = @(Get-NetIPInterface -AddressFamily IPv4,IPv6 -ErrorAction Stop |
  ForEach-Object {
    [pscustomobject]@{
      AddressFamily = [string]$_.AddressFamily
      InterfaceIndex = [string]$_.InterfaceIndex
      InterfaceMetric = [int]$_.InterfaceMetric
    }
  })
ConvertTo-Json -InputObject $metrics -Compress -Depth 3
""";
        var output = await RunPowerShellOutputAsync(script, "PowerShell action read IPv4/IPv6 interface metrics", ct, false);
        return ParseJsonList<InterfaceMetricInfo>(output);
    }

    public async Task<IReadOnlyList<InterfaceStateInfo>> GetInterfaceStatesAsync(CancellationToken ct = default)
    {
        var script = """
$states = @(Get-NetIPInterface -AddressFamily IPv4,IPv6 -ErrorAction Stop |
  ForEach-Object {
    [pscustomobject]@{
      AddressFamily = [string]$_.AddressFamily
      InterfaceIndex = [string]$_.InterfaceIndex
      ConnectionState = [string]$_.ConnectionState
    }
  })
ConvertTo-Json -InputObject $states -Compress -Depth 3
""";
        var output = await RunPowerShellOutputAsync(script, "PowerShell action read IPv4/IPv6 interface states", ct, false);
        return ParseJsonList<InterfaceStateInfo>(output);
    }

    public async Task<IReadOnlyList<InterfaceAddressInfo>> GetInterfaceAddressesAsync(CancellationToken ct = default)
    {
        var script = """
$addresses = @(Get-NetIPAddress -AddressFamily IPv4,IPv6 -ErrorAction Stop |
  Where-Object { $_.IPAddress -notin @('0.0.0.0','::','::1','127.0.0.1') } |
  ForEach-Object {
    [pscustomobject]@{
      AddressFamily = [string]$_.AddressFamily
      InterfaceIndex = [string]$_.InterfaceIndex
      IpAddress = [string]$_.IPAddress
      PrefixLength = [int]$_.PrefixLength
    }
  })
ConvertTo-Json -InputObject $addresses -Compress -Depth 3
""";
        var output = await RunPowerShellOutputAsync(script, "PowerShell action read interface addresses", ct, false);
        return ParseJsonList<InterfaceAddressInfo>(output);
    }

    internal static string BuildRoutePlanningSnapshotScript() => """
$interfaces = @(Get-NetIPInterface -AddressFamily IPv4,IPv6 -ErrorAction Stop |
  ForEach-Object {
    [pscustomobject]@{
      AddressFamily = [string]$_.AddressFamily
      InterfaceIndex = [string]$_.InterfaceIndex
      InterfaceMetric = [int]$_.InterfaceMetric
      ConnectionState = [string]$_.ConnectionState
    }
  })
$metrics = @($interfaces | ForEach-Object {
  [pscustomobject]@{ AddressFamily = $_.AddressFamily; InterfaceIndex = $_.InterfaceIndex; InterfaceMetric = $_.InterfaceMetric }
})
$states = @($interfaces | ForEach-Object {
  [pscustomobject]@{ AddressFamily = $_.AddressFamily; InterfaceIndex = $_.InterfaceIndex; ConnectionState = $_.ConnectionState }
})
$metricByInterface = @{}
foreach ($item in $interfaces) {
  $metricByInterface["{0}|{1}" -f $item.AddressFamily,$item.InterfaceIndex] = [int]$item.InterfaceMetric
}
$routes = @(Get-NetRoute -AddressFamily IPv4,IPv6 -ErrorAction Stop |
  Sort-Object AddressFamily,InterfaceIndex,DestinationPrefix,RouteMetric,NextHop |
  ForEach-Object {
    $family = [string]$_.AddressFamily
    $index = [int]$_.InterfaceIndex
    $key = "{0}|{1}" -f $family,$index
    [pscustomobject]@{
      AddressFamily = $family
      InterfaceIndex = [string]$index
      DestinationPrefix = [string]$_.DestinationPrefix
      NextHop = [string]$_.NextHop
      RouteMetric = [int]$_.RouteMetric
      InterfaceMetric = if ($metricByInterface.ContainsKey($key)) { [int]$metricByInterface[$key] } else { 0 }
      Protocol = [string]$_.Protocol
      PolicyStore = if ($_.PolicyStore) { [string]$_.PolicyStore } elseif ($_.Store) { [string]$_.Store } else { '' }
      InstanceId = [string]$_.InstanceId
    }
  })
$addresses = @(Get-NetIPAddress -AddressFamily IPv4,IPv6 -ErrorAction Stop |
  Where-Object { $_.IPAddress -notin @('0.0.0.0','::','::1','127.0.0.1') } |
  ForEach-Object {
    [pscustomobject]@{
      AddressFamily = [string]$_.AddressFamily
      InterfaceIndex = [string]$_.InterfaceIndex
      IpAddress = [string]$_.IPAddress
      PrefixLength = [int]$_.PrefixLength
    }
  })
$snapshot = [pscustomobject]@{
  Routes = $routes
  InterfaceMetrics = $metrics
  InterfaceStates = $states
  InterfaceAddresses = $addresses
}
ConvertTo-Json -InputObject $snapshot -Compress -Depth 5
""";

    public async Task<IReadOnlyList<StaticRoutePlanItem>> PreviewAsync(
        IReadOnlyList<StaticRouteTarget> targets,
        IReadOnlyCollection<AppliedStaticRoute>? ignoredOwnedRoutes = null,
        CancellationToken ct = default)
    {
        if (targets.Count == 0) throw new InvalidOperationException("No static routes to preview / 没有可预览的静态路由");
        EnsureAdministratorForOperation();
        return await PreparePlanAsync(targets, ignoredOwnedRoutes, ct);
    }

    public async Task<IReadOnlyList<StaticRouteApplyResult>> ApplyAsync(
        IReadOnlyList<StaticRouteTarget> targets,
        Action<AppliedStaticRoute>? onCreated = null,
        IReadOnlyCollection<AppliedStaticRoute>? ignoredOwnedRoutes = null,
        CancellationToken ct = default,
        Action<AppliedStaticRoute>? onCreating = null,
        Action<AppliedStaticRoute>? onRemoved = null)
    {
        if (targets.Count == 0) throw new InvalidOperationException("No static routes to apply / 没有可应用的静态路由");
        EnsureAdministratorForOperation();

        var planned = await PreparePlanAsync(targets, ignoredOwnedRoutes, ct);
        var created = new List<(AppliedStaticRoute Route, NetworkAdapterInfo Adapter)>();
        var results = new List<StaticRouteApplyResult>();
        try
        {
            foreach (var item in planned)
            {
                ct.ThrowIfCancellationRequested();
                item.Target.Rule.RouteMetric = item.Target.Route.RouteMetric;
                if (!item.ShouldCreate)
                {
                    results.Add(new StaticRouteApplyResult(item.Target.Rule, false, item.StatusText, null));
                    continue;
                }

                var intent = CreateOwnershipIntent(item.Target);
                onCreating?.Invoke(intent);
                var applied = await CreateRouteAsync(item.Target, ct);
                created.Add((applied, item.Target.Adapter));
                onCreated?.Invoke(applied);
                results.Add(new StaticRouteApplyResult(item.Target.Rule, true, "Applied / 已应用", applied));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.Warn($"Static route batch failed; rolling back {created.Count} route(s) / 静态路由批处理失败，正在回滚 {created.Count} 条路由");
            var rollbackFailures = new List<string>();
            var removedCount = 0;
            foreach (var item in created.AsEnumerable().Reverse())
            {
                try
                {
                    await RemoveAsync(item.Route, item.Adapter, CancellationToken.None);
                    onRemoved?.Invoke(item.Route);
                    removedCount++;
                }
                catch (Exception rollbackEx)
                {
                    var failure = $"{item.Route.AddressFamily} {item.Route.DestinationPrefix} on {item.Adapter.Name}: {rollbackEx.Message}";
                    rollbackFailures.Add(failure);
                    _logger.Error($"Static route rollback failed: {failure}", rollbackEx);
                }
            }

            if (ex is OperationCanceledException && rollbackFailures.Count == 0) throw;

            var rollbackSummary = rollbackFailures.Count == 0
                ? $"Rollback verified for {removedCount}/{created.Count} route(s) / 已核实回滚 {removedCount}/{created.Count} 条路由"
                : $"Rollback verified for {removedCount}/{created.Count} route(s); failures: {string.Join("; ", rollbackFailures)} / 已核实回滚 {removedCount}/{created.Count} 条路由；失败项：{string.Join("; ", rollbackFailures)}";
            var failureCause = rollbackFailures.Count == 0
                ? ex
                : new AggregateException("Batch operation and rollback failures / 批量操作及回滚均失败", [ex, .. rollbackFailures.Select(x => new InvalidOperationException(x))]);
            throw new InvalidOperationException($"Static route batch failed: {ex.Message}. {rollbackSummary}", failureCause);
        }
    }

    private async Task<IReadOnlyList<StaticRoutePlanItem>> PreparePlanAsync(
        IReadOnlyList<StaticRouteTarget> targets,
        IReadOnlyCollection<AppliedStaticRoute>? ignoredOwnedRoutes,
        CancellationToken ct)
    {
        var snapshot = await GetRoutePlanningSnapshotAsync(ct);
        var currentRoutes = snapshot.Routes.Where(route =>
            ignoredOwnedRoutes == null
            || !ignoredOwnedRoutes.Any(owned =>
                !string.IsNullOrWhiteSpace(owned.InstanceId)
                && owned.InstanceId.Equals(route.InstanceId, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(owned.PolicyStore)
                    || owned.PolicyStore.Equals(route.PolicyStore, StringComparison.OrdinalIgnoreCase)))).ToList();
        var interfaceMetrics = snapshot.InterfaceMetrics;
        var interfaceStates = snapshot.InterfaceStates;
        var interfaceAddresses = snapshot.InterfaceAddresses;
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenAdapterPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<RouteCandidate>();

        for (var order = 0; order < targets.Count; order++)
        {
            var target = targets[order];
            EnsureApplyTarget(target.Adapter);
            EnsureAdapterReady(target, interfaceStates);
            EnsureGatewayOnLink(target, interfaceAddresses);

            var family = FamilyName(target.Route.AddressFamily);
            var destinationKey = $"{family}|{target.Route.DestinationPrefix}";
            var targetKey = $"{destinationKey}|{target.Adapter.InterfaceIndex}|{target.Route.NextHop}";
            var adapterPrefixKey = $"{destinationKey}|{target.Adapter.InterfaceIndex}";
            if (!seenTargets.Add(targetKey))
            {
                throw new InvalidOperationException($"Duplicate route target: {target.Route.DestinationPrefix} / 重复路由目标：{target.Route.DestinationPrefix}");
            }
            if (!seenAdapterPrefixes.Add(adapterPrefixKey))
            {
                throw new InvalidOperationException($"The same prefix cannot be added twice on one adapter; use one gateway / 同一网卡不能重复添加同一前缀，请只保留一条网关规则：{target.Route.DestinationPrefix}");
            }

            var samePrefix = currentRoutes.Where(x => SameDestination(x, family, target.Route.DestinationPrefix)).ToList();
            var samePath = samePrefix.FirstOrDefault(x =>
                x.InterfaceIndex.Equals(target.Adapter.InterfaceIndex, StringComparison.OrdinalIgnoreCase)
                && NormalizeNextHop(x.NextHop, target.Route.AddressFamily).Equals(target.Route.NextHop, StringComparison.OrdinalIgnoreCase));
            var interfaceMetric = FindInterfaceMetric(interfaceMetrics, family, target.Adapter.InterfaceIndex);
            if (samePath != null)
            {
                candidates.Add(new RouteCandidate(target, order, false, samePath.RouteMetric, interfaceMetric, samePath));
                continue;
            }

            var existingConflict = currentRoutes.FirstOrDefault(x =>
                x.AddressFamily.Equals(family, StringComparison.OrdinalIgnoreCase)
                && IsMoreSpecificConflict(target.Route, target.Adapter, x));
            if (existingConflict != null)
            {
                throw new InvalidOperationException(
                    $"A more specific existing route prevents this rule from covering the full range: {target.Route.DestinationPrefix} / 已有更具体的路由，无法覆盖完整范围：{target.Route.DestinationPrefix}");
            }

            var plannedConflict = targets.FirstOrDefault(x =>
                !ReferenceEquals(x, target)
                && x.Route.AddressFamily == target.Route.AddressFamily
                && IsMoreSpecificConflict(target.Route, target.Adapter, x.Route, x.Adapter));
            if (plannedConflict != null)
            {
                throw new InvalidOperationException(
                    $"A more specific route in this batch prevents this rule from covering the full range: {target.Route.DestinationPrefix} / 本次批量设置中已有更具体路由，无法覆盖完整范围：{target.Route.DestinationPrefix}");
            }

            candidates.Add(new RouteCandidate(target, order, true, 1, interfaceMetric, null));
        }

        foreach (var group in candidates.Where(x => x.ShouldCreate).GroupBy(x => $"{FamilyName(x.Target.Route.AddressFamily)}|{x.Target.Route.DestinationPrefix}", StringComparer.OrdinalIgnoreCase))
        {
            var first = group.First();
            var existing = currentRoutes.Where(x => SameDestination(x, FamilyName(first.Target.Route.AddressFamily), first.Target.Route.DestinationPrefix)).ToList();
            var existingBest = existing.Count == 0 ? long.MaxValue : existing.Min(x => (long)x.RouteMetric + x.InterfaceMetric);
            long previousEffective = 0;
            foreach (var candidate in group.OrderBy(x => x.InterfaceMetric).ThenBy(x => x.Order))
            {
                var desiredEffective = Math.Max((long)Math.Max(0, candidate.InterfaceMetric) + 1, previousEffective + 1);
                if (existing.Count > 0 && desiredEffective >= existingBest)
                {
                    throw new InvalidOperationException(
                        $"The selected adapters cannot all outrank the existing same-prefix route without changing interface metrics: {candidate.Target.Route.DestinationPrefix} / 现有同前缀路由优先级过高，若不修改网卡接口跃点，无法让新增路由全部优先：{candidate.Target.Route.DestinationPrefix}");
                }

                var routeMetric = desiredEffective - candidate.InterfaceMetric;
                if (routeMetric is < 1 or > 65535)
                {
                    throw new InvalidOperationException(
                        $"Automatic route metric is outside the Windows range: {candidate.Target.Route.DestinationPrefix} / 自动计算的路由跃点超出 Windows 允许范围：{candidate.Target.Route.DestinationPrefix}");
                }

                candidate.RouteMetric = (int)routeMetric;
                previousEffective = desiredEffective;
            }
        }

        var planItems = candidates.Select(candidate =>
        {
            var prepared = candidate.Target.Route with { RouteMetric = candidate.RouteMetric };
            var target = new StaticRouteTarget(candidate.Target.Rule, candidate.Target.Adapter, prepared);
            var status = candidate.ShouldCreate
                ? (candidate.ExistingRoute == null ? "Ready / 待应用" : "Ready with priority / 将以更高优先级应用")
                : "Already present / 已有相同路径";
            return new StaticRoutePlanItem(target, candidate.ShouldCreate, status, candidate.InterfaceMetric);
        }).ToList();

        AddOverlapWarnings(planItems);
        return planItems;
    }

    public async Task RemoveAsync(AppliedStaticRoute route, NetworkAdapterInfo adapter, CancellationToken ct = default)
    {
        EnsureAdministratorForOperation();
        EnsureCleanupTarget(route, adapter);
        if (!route.OwnershipVerified || string.IsNullOrWhiteSpace(route.InstanceId))
        {
            throw new InvalidOperationException("Route ownership identity is missing or unverified; refusing cleanup / 路由归属标识缺失或未验证，拒绝删除");
        }
        var family = FamilyName(ParseAddressFamily(route.AddressFamily));
        var instanceId = PsQuote(route.InstanceId ?? "");
        var policyStore = PsQuote(string.IsNullOrWhiteSpace(route.PolicyStore) ? "ActiveStore" : route.PolicyStore);
        var destination = PsQuote(route.DestinationPrefix);
        var nextHop = PsQuote(route.NextHop);
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{route.InterfaceIndex}}
$family='{{family}}'
$policyStore={{policyStore}}
$destination={{destination}}
$nextHop={{nextHop}}
$instanceId={{instanceId}}
$routes = @(Get-NetRoute -AddressFamily $family -ErrorAction Stop |
  Where-Object { [int]$_.InterfaceIndex -eq $idx -and [string]$_.DestinationPrefix -eq $destination })
$owned = @($routes | Where-Object { [string]$_.InstanceId -eq $instanceId })
$selected = @($owned | Where-Object {
  [string]$_.NextHop -eq $nextHop -and
  [int]$_.RouteMetric -eq {{route.RouteMetric}} -and
  (([string]$_.PolicyStore -eq $policyStore) -or ([string]$_.Store -eq $policyStore))
})
if ($owned.Count -eq 0) {
  $samePath = @($routes | Where-Object {
    [string]$_.NextHop -eq $nextHop -and
    (([string]$_.PolicyStore -eq $policyStore) -or ([string]$_.Store -eq $policyStore))
  })
  if ($samePath.Count -eq 0) { 'OK' ; exit }
  throw 'Owned route identity not found; refusing to remove another route'
}
if ($selected.Count -eq 0) { throw 'Owned route parameters changed; refusing to remove it' }
$selected | Remove-NetRoute -Confirm:$false -ErrorAction Stop
$remaining = @(Get-NetRoute -AddressFamily $family -ErrorAction Stop |
  Where-Object { [int]$_.InterfaceIndex -eq $idx -and [string]$_.DestinationPrefix -eq $destination -and [string]$_.InstanceId -eq $instanceId })
if ($remaining.Count -gt 0) { throw 'Owned route still exists after removal' }
'OK'
""";
        await RunPowerShellAsync(script, $"PowerShell action remove {family} static route {route.DestinationPrefix} on {adapter.Name}", ct);
    }

    public async Task<bool> ExistsAsync(AppliedStaticRoute route, NetworkAdapterInfo adapter, CancellationToken ct = default)
    {
        EnsureCleanupTarget(route, adapter);
        if (!route.OwnershipVerified || string.IsNullOrWhiteSpace(route.InstanceId)) return false;
        var addressFamily = ParseAddressFamily(route.AddressFamily);
        if (!IpNetwork.TryParseCidr(route.DestinationPrefix, out _, out var prefixLength, out _)) prefixLength = 0;
        var validated = new ValidatedStaticRoute(route.DestinationPrefix, route.NextHop, route.RouteMetric, addressFamily, prefixLength, prefixLength == 0);
        var matches = await GetExactRoutesAsync(adapter.InterfaceIndex, validated, ct);
        return matches.Any(x =>
            !string.IsNullOrWhiteSpace(route.InstanceId)
            && x.InstanceId.Equals(route.InstanceId, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(route.PolicyStore)
                || x.PolicyStore.Equals(route.PolicyStore, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<AppliedStaticRoute> CreateRouteAsync(StaticRouteTarget target, CancellationToken ct)
    {
        var family = FamilyName(target.Route.AddressFamily);
        var destination = PsQuote(target.Route.DestinationPrefix);
        var nextHop = PsQuote(target.Route.NextHop);
        var expectedGuid = Guid.Parse(target.Adapter.Id);
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{target.Adapter.InterfaceIndex}}
$family='{{family}}'
$destination={{destination}}
$nextHop={{nextHop}}
$expectedGuid=[guid]'{{expectedGuid:D}}'
$expectedMac={{PsQuote(target.Adapter.MacAddress ?? "")}}
$netAdapter = Get-NetAdapter -InterfaceIndex $idx -ErrorAction Stop
if ($netAdapter.Status -ne 'Up') { throw 'Selected adapter is not Up' }
if ([guid]$netAdapter.InterfaceGuid -ne $expectedGuid) { throw 'Selected adapter identity changed' }
if (-not [string]::IsNullOrWhiteSpace($expectedMac) -and [string]$netAdapter.MacAddress -ne $expectedMac) { throw 'Selected adapter identity changed' }
        New-NetRoute -DestinationPrefix $destination -InterfaceIndex $idx -AddressFamily $family -NextHop $nextHop -RouteMetric {{target.Route.RouteMetric}} -PolicyStore ActiveStore -ErrorAction Stop | Out-Null
$route = @(Get-NetRoute -InterfaceIndex $idx -AddressFamily $family -DestinationPrefix $destination -ErrorAction Stop |
  Where-Object {
    [string]$_.NextHop -eq $nextHop -and
    [int]$_.RouteMetric -eq {{target.Route.RouteMetric}} -and
    (([string]$_.PolicyStore -eq 'ActiveStore') -or ([string]$_.Store -eq 'ActiveStore'))
  } |
  ForEach-Object {
    [pscustomobject]@{
      AddressFamily = [string]$_.AddressFamily
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
        var output = await RunPowerShellOutputAsync(script, $"PowerShell action create {family} static route {target.Route.DestinationPrefix} on {target.Adapter.Name}", ct, true);
        var snapshot = ParseJsonList<RouteSnapshot>(output).FirstOrDefault() ?? throw new InvalidOperationException("Created route could not be parsed / 创建的路由无法解析");
        if (string.IsNullOrWhiteSpace(snapshot.InstanceId)) throw new InvalidOperationException("Created route has no ownership identity / 创建的路由没有归属实例标识");
        var applied = new AppliedStaticRoute
        {
            RuleId = target.Rule.Id,
            DestinationPrefix = target.Route.DestinationPrefix,
            AddressFamily = family,
            AdapterId = target.Adapter.Id,
            AdapterName = target.Adapter.Name,
            AdapterMac = target.Adapter.MacAddress ?? "",
            InterfaceIndex = target.Adapter.InterfaceIndex is { } index && int.TryParse(index, out var parsedIndex) ? parsedIndex : 0,
            NextHop = target.Route.NextHop,
            RouteMetric = target.Route.RouteMetric,
            PolicyStore = string.IsNullOrWhiteSpace(snapshot.PolicyStore) ? "ActiveStore" : snapshot.PolicyStore,
            InstanceId = snapshot.InstanceId
        };
        _logger.Info($"Static route created: family={family} destination={applied.DestinationPrefix} nextHop={applied.NextHop} adapter={applied.AdapterName} metric={applied.RouteMetric}");
        return applied;
    }

    private static AppliedStaticRoute CreateOwnershipIntent(StaticRouteTarget target) => new()
    {
        RuleId = target.Rule.Id,
        DestinationPrefix = target.Route.DestinationPrefix,
        AddressFamily = FamilyName(target.Route.AddressFamily),
        AdapterId = target.Adapter.Id,
        AdapterName = target.Adapter.Name,
        AdapterMac = target.Adapter.MacAddress ?? "",
        InterfaceIndex = int.TryParse(target.Adapter.InterfaceIndex, out var index) ? index : 0,
        NextHop = target.Route.NextHop,
        RouteMetric = target.Route.RouteMetric,
        PolicyStore = "ActiveStore",
        InstanceId = "",
        OwnershipVerified = false
    };

    private async Task<List<RouteSnapshot>> GetExactRoutesAsync(string interfaceIndex, ValidatedStaticRoute route, CancellationToken ct)
    {
        if (!int.TryParse(interfaceIndex, out var index) || index <= 0) throw new InvalidOperationException("Invalid adapter InterfaceIndex / 网卡 InterfaceIndex 无效");
        var family = FamilyName(route.AddressFamily);
        var destination = PsQuote(route.DestinationPrefix);
        var nextHop = PsQuote(route.NextHop);
        var script = $$"""
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [Console]::OutputEncoding
$PSDefaultParameterValues['Out-File:Encoding'] = 'utf8'
$idx={{index}}
$family='{{family}}'
$destination={{destination}}
$nextHop={{nextHop}}
$routes = @(Get-NetRoute -AddressFamily $family -ErrorAction Stop |
  Where-Object { [int]$_.InterfaceIndex -eq $idx -and [string]$_.DestinationPrefix -eq $destination -and [string]$_.NextHop -eq $nextHop } |
  ForEach-Object {
    [pscustomobject]@{
      AddressFamily = [string]$_.AddressFamily
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
        var output = await RunPowerShellOutputAsync(script, $"PowerShell action inspect {family} static route {route.DestinationPrefix}", ct, false);
        return ParseJsonList<RouteSnapshot>(output);
    }

    private static bool IsMoreSpecificConflict(ValidatedStaticRoute target, NetworkAdapterInfo targetAdapter, CurrentStaticRoute existing)
    {
        if (!IpNetwork.TryParseCidr(existing.DestinationPrefix, out var existingNetwork, out var existingPrefix, out _)) return false;
        if (existingPrefix <= target.PrefixLength) return false;
        if (!IpNetwork.TryParseCidr(target.DestinationPrefix, out var targetNetwork, out _, out _)) return false;
        if (!IpNetwork.Contains(targetNetwork, target.PrefixLength, existingNetwork)) return false;
        return !existing.InterfaceIndex.Equals(targetAdapter.InterfaceIndex, StringComparison.OrdinalIgnoreCase)
            || !NormalizeNextHop(existing.NextHop, target.AddressFamily).Equals(target.NextHop, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMoreSpecificConflict(ValidatedStaticRoute target, NetworkAdapterInfo targetAdapter, ValidatedStaticRoute existing, NetworkAdapterInfo existingAdapter)
    {
        if (existing.PrefixLength <= target.PrefixLength) return false;
        if (!IpNetwork.TryParseCidr(existing.DestinationPrefix, out var existingNetwork, out _, out _)) return false;
        if (!IpNetwork.TryParseCidr(target.DestinationPrefix, out var targetNetwork, out _, out _)) return false;
        if (!IpNetwork.Contains(targetNetwork, target.PrefixLength, existingNetwork)) return false;
        return !existingAdapter.InterfaceIndex.Equals(targetAdapter.InterfaceIndex, StringComparison.OrdinalIgnoreCase)
            || !existing.NextHop.Equals(target.NextHop, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SameDestination(CurrentStaticRoute route, string family, string destinationPrefix)
    {
        return route.AddressFamily.Equals(family, StringComparison.OrdinalIgnoreCase)
            && DestinationEquals(route.DestinationPrefix, destinationPrefix);
    }

    private static bool DestinationEquals(string left, string right)
    {
        return IpNetwork.TryParseCidr(left, out _, out _, out var leftCanonical)
            && IpNetwork.TryParseCidr(right, out _, out _, out var rightCanonical)
            && leftCanonical.Equals(rightCanonical, StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureAdapterReady(StaticRouteTarget target, IReadOnlyList<InterfaceStateInfo> states)
    {
        if (!target.Adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Selected adapter is not Up: {target.Adapter.Name} / 所选网卡未处于启用状态：{target.Adapter.Name}");
        }

        var family = FamilyName(target.Route.AddressFamily);
        var state = states.FirstOrDefault(x =>
            x.AddressFamily.Equals(family, StringComparison.OrdinalIgnoreCase)
            && x.InterfaceIndex.Equals(target.Adapter.InterfaceIndex, StringComparison.OrdinalIgnoreCase));
        if (state == null || !(state.ConnectionState.Equals("Connected", StringComparison.OrdinalIgnoreCase)
            || state.ConnectionState.Equals("Up", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Selected adapter does not have an active {family} interface: {target.Adapter.Name} / 所选网卡没有处于活动状态的 {family} 接口：{target.Adapter.Name}");
        }
    }

    private static void EnsureGatewayOnLink(StaticRouteTarget target, IReadOnlyList<InterfaceAddressInfo> addresses)
    {
        var addressFamily = target.Route.AddressFamily;
        var directHop = addressFamily == AddressFamily.InterNetwork ? "0.0.0.0" : "::";
        if (target.Route.NextHop.Equals(directHop, StringComparison.OrdinalIgnoreCase)) return;
        if (!IPAddress.TryParse(target.Route.NextHop, out var gateway) || gateway.AddressFamily != addressFamily)
        {
            throw new InvalidOperationException($"Gateway family is invalid: {target.Route.NextHop} / 网关地址族无效：{target.Route.NextHop}");
        }

        var onLink = addresses
            .Where(x => x.AddressFamily.Equals(FamilyName(addressFamily), StringComparison.OrdinalIgnoreCase)
                && x.InterfaceIndex.Equals(target.Adapter.InterfaceIndex, StringComparison.OrdinalIgnoreCase))
            .Any(x => IPAddress.TryParse(x.IpAddress, out var local)
                && local.AddressFamily == addressFamily
                && x.PrefixLength >= 0
                && x.PrefixLength <= (addressFamily == AddressFamily.InterNetwork ? 32 : 128)
                && IpNetwork.Contains(local, x.PrefixLength, gateway));
        if (!onLink)
        {
            throw new InvalidOperationException(
                $"Gateway is not on-link for the selected adapter: {target.Route.NextHop} / 网关不在所选网卡的直连网段内：{target.Route.NextHop}");
        }
    }

    private static void AddOverlapWarnings(IReadOnlyList<StaticRoutePlanItem> items)
    {
        var warnings = items.ToDictionary(x => x, _ => new List<string>());
        for (var i = 0; i < items.Count; i++)
        {
            for (var j = i + 1; j < items.Count; j++)
            {
                var left = items[i].Target.Route;
                var right = items[j].Target.Route;
                if (left.AddressFamily != right.AddressFamily || !AreOverlapping(left, right)) continue;

                if (DestinationEquals(left.DestinationPrefix, right.DestinationPrefix))
                {
                    var winner = items[i].EffectiveMetric <= items[j].EffectiveMetric ? items[i] : items[j];
                    var winnerText = $"{winner.Target.Adapter.Name} (effective metric {winner.EffectiveMetric})";
                    warnings[items[i]].Add($"Same prefix; winner preview: {winnerText} / 同前缀，预计优先网卡：{winner.Target.Adapter.Name}（综合跃点 {winner.EffectiveMetric}）");
                    warnings[items[j]].Add($"Same prefix; winner preview: {winnerText} / 同前缀，预计优先网卡：{winner.Target.Adapter.Name}（综合跃点 {winner.EffectiveMetric}）");
                }
                else
                {
                    warnings[items[i]].Add($"Overlaps {right.DestinationPrefix}; longest prefix wins / 与 {right.DestinationPrefix} 重叠，最长前缀优先");
                    warnings[items[j]].Add($"Overlaps {left.DestinationPrefix}; longest prefix wins / 与 {left.DestinationPrefix} 重叠，最长前缀优先");
                }
            }
        }

        foreach (var item in items)
        {
            item.OverlapWarning = string.Join(" | ", warnings[item].Distinct(StringComparer.Ordinal));
        }
    }

    private static bool AreOverlapping(ValidatedStaticRoute left, ValidatedStaticRoute right)
    {
        if (!IpNetwork.TryParseCidr(left.DestinationPrefix, out var leftNetwork, out var leftPrefix, out _)
            || !IpNetwork.TryParseCidr(right.DestinationPrefix, out var rightNetwork, out var rightPrefix, out _)
            || leftNetwork.AddressFamily != rightNetwork.AddressFamily)
        {
            return false;
        }

        return IpNetwork.Contains(leftNetwork, leftPrefix, rightNetwork)
            || IpNetwork.Contains(rightNetwork, rightPrefix, leftNetwork);
    }

    private static int FindInterfaceMetric(IReadOnlyList<InterfaceMetricInfo> metrics, string family, string interfaceIndex)
    {
        return metrics.FirstOrDefault(x =>
                x.AddressFamily.Equals(family, StringComparison.OrdinalIgnoreCase)
                && x.InterfaceIndex.Equals(interfaceIndex, StringComparison.OrdinalIgnoreCase))?.InterfaceMetric ?? 0;
    }

    private static string NormalizeNextHop(string? nextHop, AddressFamily addressFamily)
    {
        if (!string.IsNullOrWhiteSpace(nextHop)
            && IPAddress.TryParse(nextHop.Trim(), out var parsed)
            && parsed.AddressFamily == addressFamily)
        {
            return parsed.ToString();
        }
        if (!string.IsNullOrWhiteSpace(nextHop)) return nextHop.Trim();
        return addressFamily == AddressFamily.InterNetwork ? "0.0.0.0" : "::";
    }

    private static void EnsureApplyTarget(NetworkAdapterInfo adapter)
    {
        if (!int.TryParse(adapter.InterfaceIndex, out var index) || index <= 0) throw new InvalidOperationException("InterfaceIndex missing / 缺少网卡 InterfaceIndex");
        if (!Guid.TryParse(adapter.Id, out _)) throw new InvalidOperationException("Adapter GUID missing / 缺少网卡稳定 GUID");
    }

    private void EnsureAdministratorForOperation()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!_isAdministrator())
        {
            throw new InvalidOperationException("Administrator privileges are required to add or remove Windows routes. Restart the tool with Run as administrator. / 新增或删除 Windows 路由需要管理员权限，请使用‘以管理员身份运行’重新启动工具。");
        }
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return true;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void EnsureCleanupTarget(AppliedStaticRoute route, NetworkAdapterInfo adapter)
    {
        if (route.InterfaceIndex <= 0 || !int.TryParse(adapter.InterfaceIndex, out var index) || index != route.InterfaceIndex) throw new InvalidOperationException("Adapter identity changed; route cleanup skipped / 网卡身份已变化，跳过路由清理");
        if (!string.IsNullOrWhiteSpace(route.AdapterId) && !route.AdapterId.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Adapter identity changed; route cleanup skipped / 网卡身份已变化，跳过路由清理");
        if (!string.IsNullOrWhiteSpace(route.AdapterMac)
            && !string.IsNullOrWhiteSpace(adapter.MacAddress)
            && !route.AdapterMac.Equals(adapter.MacAddress, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Adapter MAC changed; route cleanup skipped / 网卡 MAC 已变化，跳过路由清理");
    }

    private static string FamilyName(AddressFamily family) => family == AddressFamily.InterNetwork ? "IPv4" : "IPv6";

    private static AddressFamily ParseAddressFamily(string? family) => string.Equals(family, "IPv6", StringComparison.OrdinalIgnoreCase)
        ? AddressFamily.InterNetworkV6
        : AddressFamily.InterNetwork;

    private static string PsQuote(string value) => "'" + (value ?? "").Replace("'", "''") + "'";

    private async Task RunPowerShellAsync(string script, string summary, CancellationToken ct)
    {
        _ = await RunPowerShellOutputAsync(script, summary, ct, true);
    }

    private Task<string> RunPowerShellOutputAsync(string script, string summary, CancellationToken ct, bool logOutput) =>
        _powerShellProcessRunner.RunAsync(script, summary, ct, logOutput);

    private async Task<RoutePlanningSnapshot> GetRoutePlanningSnapshotAsync(CancellationToken ct)
    {
        var output = await RunPowerShellOutputAsync(BuildRoutePlanningSnapshotScript(),
            "PowerShell action read route planning snapshot", ct, false);
        return ParseRoutePlanningSnapshot(output);
    }

    private static RoutePlanningSnapshot ParseRoutePlanningSnapshot(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) throw new InvalidDataException("Route planning snapshot was empty.");

        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Route planning snapshot must be a JSON object.");

        static JsonElement RequireArray(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"Route planning snapshot is missing array '{name}'.");
            return value;
        }

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var snapshot = new RoutePlanningSnapshot
        {
            Routes = JsonSerializer.Deserialize<List<CurrentStaticRoute>>(RequireArray(document.RootElement, "Routes").GetRawText(), options) ?? throw new InvalidDataException("Route array was null."),
            InterfaceMetrics = JsonSerializer.Deserialize<List<InterfaceMetricInfo>>(RequireArray(document.RootElement, "InterfaceMetrics").GetRawText(), options) ?? throw new InvalidDataException("Interface metric array was null."),
            InterfaceStates = JsonSerializer.Deserialize<List<InterfaceStateInfo>>(RequireArray(document.RootElement, "InterfaceStates").GetRawText(), options) ?? throw new InvalidDataException("Interface state array was null."),
            InterfaceAddresses = JsonSerializer.Deserialize<List<InterfaceAddressInfo>>(RequireArray(document.RootElement, "InterfaceAddresses").GetRawText(), options) ?? throw new InvalidDataException("Interface address array was null.")
        };

        if (snapshot.Routes.Any(route => string.IsNullOrWhiteSpace(route.AddressFamily)
                || string.IsNullOrWhiteSpace(route.InterfaceIndex)
                || string.IsNullOrWhiteSpace(route.DestinationPrefix)
                || string.IsNullOrWhiteSpace(route.NextHop)
                || route.RouteMetric < 0
                || route.InterfaceMetric < 0)
            || snapshot.InterfaceMetrics.Any(item => string.IsNullOrWhiteSpace(item.AddressFamily)
                || string.IsNullOrWhiteSpace(item.InterfaceIndex) || item.InterfaceMetric < 0)
            || snapshot.InterfaceStates.Any(item => string.IsNullOrWhiteSpace(item.AddressFamily)
                || string.IsNullOrWhiteSpace(item.InterfaceIndex) || string.IsNullOrWhiteSpace(item.ConnectionState))
            || snapshot.InterfaceAddresses.Any(item => string.IsNullOrWhiteSpace(item.AddressFamily)
                || string.IsNullOrWhiteSpace(item.InterfaceIndex) || string.IsNullOrWhiteSpace(item.IpAddress)
                || item.PrefixLength < 0 || item.PrefixLength > (item.AddressFamily.Equals("IPv6", StringComparison.OrdinalIgnoreCase) ? 128 : 32)))
        {
            throw new InvalidDataException("Route planning snapshot contains incomplete or invalid rows.");
        }

        return snapshot;
    }

    private static List<T> ParseJsonList<T>(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        using var document = JsonDocument.Parse(output);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? JsonSerializer.Deserialize<List<T>>(document.RootElement.GetRawText(), options) ?? []
            : [JsonSerializer.Deserialize<T>(document.RootElement.GetRawText(), options) ?? Activator.CreateInstance<T>()];
    }

    private sealed class RouteCandidate
    {
        public RouteCandidate(StaticRouteTarget target, int order, bool shouldCreate, int routeMetric, int interfaceMetric, CurrentStaticRoute? existingRoute)
        {
            Target = target;
            Order = order;
            ShouldCreate = shouldCreate;
            RouteMetric = routeMetric;
            InterfaceMetric = interfaceMetric;
            ExistingRoute = existingRoute;
        }

        public StaticRouteTarget Target { get; }
        public int Order { get; }
        public bool ShouldCreate { get; }
        public int RouteMetric { get; set; }
        public int InterfaceMetric { get; }
        public CurrentStaticRoute? ExistingRoute { get; }
    }

    private sealed class RoutePlanningSnapshot
    {
        public List<CurrentStaticRoute> Routes { get; init; } = [];
        public List<InterfaceMetricInfo> InterfaceMetrics { get; init; } = [];
        public List<InterfaceStateInfo> InterfaceStates { get; init; } = [];
        public List<InterfaceAddressInfo> InterfaceAddresses { get; init; } = [];
    }

    private sealed class RouteSnapshot
    {
        public string AddressFamily { get; set; } = "IPv4";
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
