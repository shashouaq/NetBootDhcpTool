using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class StaticRouteOwnershipTests
{
    [TestMethod]
    public async Task RemoveScriptUsesPolicyStoreAndDeletesOnlyVerifiedInstance()
    {
        var routes = new[]
        {
            new MockRoute("owned-instance", "192.0.2.0/24", "0.0.0.0", 11, "ActiveStore"),
            new MockRoute("other-instance", "192.0.2.0/24", "0.0.0.0", 11, "ActiveStore")
        };
        string? script = null;
        var service = Service(async (value, _, token, _) =>
        {
            script = value;
            var execution = await ExecuteMockedPowerShellAsync(value, routes, token);
            Assert.AreEqual(0, execution.ExitCode, execution.Error);
            return execution.Output;
        });

        await service.RemoveAsync(Applied("owned-instance"), Adapter());

        StringAssert.Contains(script!, "$policyStore='ActiveStore'");
        var state = await ExecuteMockedPowerShellAsync(script!, routes, CancellationToken.None);
        Assert.AreEqual(0, state.ExitCode, state.Error);
        var remaining = JsonSerializer.Deserialize<List<MockRoute>>(state.StateJson!)!;
        CollectionAssert.AreEqual(new[] { "other-instance" }, remaining.Select(x => x.InstanceId).ToArray());
    }

    [TestMethod]
    public async Task RepeatedRemovalOfMissingOwnedRouteIsIdempotent()
    {
        var service = Service(async (script, _, token, _) =>
        {
            var result = await ExecuteMockedPowerShellAsync(script, [], token);
            Assert.AreEqual(0, result.ExitCode, result.Error);
            StringAssert.Contains(result.Output, "OK");
            return result.Output;
        });

        await service.RemoveAsync(Applied("already-removed"), Adapter());
    }

    [TestMethod]
    public async Task MissingRouteCanBeReadWithoutDestinationFilteredCimFailure()
    {
        string? observedScript = null;
        var service = Service(async (script, _, token, _) =>
        {
            observedScript = script;
            var result = await ExecuteMockedPowerShellAsync(script, [], token);
            Assert.AreEqual(0, result.ExitCode, result.Error);
            var stateMarker = result.Output.LastIndexOf("__MOCK_STATE__", StringComparison.Ordinal);
            return stateMarker < 0 ? result.Output : result.Output[..stateMarker].Trim();
        });

        Assert.IsFalse(await service.ExistsAsync(Applied("already-removed"), Adapter()));
        StringAssert.Contains(observedScript!, "Get-NetRoute -AddressFamily $family");
        Assert.IsFalse(observedScript!.Contains("-DestinationPrefix", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task PlanningSnapshotRunsTheGeneratedReadOnlyScriptOnceAndPreservesIpv4Ipv6Projection()
    {
        var root = Path.Combine(Path.GetTempPath(), "netboot-route-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var tracePath = Path.Combine(root, "cmdlets.txt");
        var interfaces = JsonSerializer.Serialize(new[]
        {
            new { AddressFamily = "IPv4", InterfaceIndex = 17, InterfaceMetric = 10, ConnectionState = "Connected" },
            new { AddressFamily = "IPv6", InterfaceIndex = 17, InterfaceMetric = 5, ConnectionState = "Connected" }
        });
        var routes = JsonSerializer.Serialize(new[]
        {
            new { AddressFamily = "IPv4", InterfaceIndex = 17, DestinationPrefix = "192.0.2.0/24", NextHop = "0.0.0.0", RouteMetric = 11, Protocol = 3, PolicyStore = "ActiveStore", Store = "ActiveStore", InstanceId = "existing-v4" }
        });
        var service = Service(async (script, _, token, _) =>
        {
            var wrapped = WrapRoutePlanningScript(script, tracePath, interfaces, routes, "[]");
            var execution = await RunPowerShellAsync(wrapped, token);
            Assert.AreEqual(0, execution.ExitCode, execution.Error);
            return execution.Output;
        });

        try
        {
            var plan = await service.PreviewAsync([Target(), Target("ipv6", "2001:db8:abcd::/64")]);

            Assert.AreEqual(2, plan.Count);
            Assert.IsFalse(plan[0].ShouldCreate, "The existing IPv4 route on the same path remains an already-present route.");
            Assert.AreEqual(11, plan[0].Target.Route.RouteMetric);
            Assert.AreEqual(10, plan[0].InterfaceMetric);
            Assert.IsTrue(plan[1].ShouldCreate);
            Assert.AreEqual(5, plan[1].InterfaceMetric);
            Assert.AreEqual(1, plan[1].Target.Route.RouteMetric);

            var generated = StaticRouteService.BuildRoutePlanningSnapshotScript();
            Assert.AreEqual(1, Regex.Matches(generated, @"\bGet-NetIPInterface\b").Count);
            Assert.AreEqual(1, Regex.Matches(generated, @"\bGet-NetRoute\b").Count);
            Assert.AreEqual(1, Regex.Matches(generated, @"\bGet-NetIPAddress\b").Count);
            var calls = File.ReadAllLines(tracePath);
            CollectionAssert.AreEqual(new[] { "Get-NetIPInterface", "Get-NetRoute", "Get-NetIPAddress" }, calls);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task ApplyReplansFromFreshSnapshotAndRejectsAConflictAddedAfterPreview()
    {
        var reads = 0;
        var createCalls = 0;
        var service = Service((_, summary, _, _) =>
        {
            if (!summary.Contains("read route planning snapshot", StringComparison.Ordinal))
            {
                if (summary.Contains("create IPv4 static route", StringComparison.Ordinal)) createCalls++;
                Assert.Fail($"Unexpected PowerShell call: {summary}");
            }

            reads++;
            IReadOnlyList<CurrentStaticRoute> routes = reads == 1
                ? []
                : [new CurrentStaticRoute
                {
                    AddressFamily = "IPv4", InterfaceIndex = "18", DestinationPrefix = "192.0.2.128/25",
                    NextHop = "0.0.0.0", RouteMetric = 1, InterfaceMetric = 10, Protocol = "3",
                    PolicyStore = "ActiveStore", InstanceId = "new-conflict"
                }];
            return Task.FromResult(RoutePlanningSnapshotJson(routes));
        });

        var preview = await service.PreviewAsync([Target()]);
        Assert.IsTrue(preview[0].ShouldCreate);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ApplyAsync([Target()]));

        Assert.AreEqual(2, reads, "Preview and Apply must each read a fresh planning snapshot.");
        Assert.AreEqual(0, createCalls, "Apply must reject the newly observed conflict before any write.");
    }

    [TestMethod]
    public async Task IncompletePlanningSnapshotFailsClosedInsteadOfMeaningNoRoutes()
    {
        var service = Service((_, _, _, _) => Task.FromResult("{\"Routes\":[]}"));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.PreviewAsync([Target()]));
    }

    [TestMethod]
    public async Task RemovalLeavesSamePrefixRouteOnAnotherAdapterUntouched()
    {
        var routes = new[]
        {
            new MockRoute("owned-instance", "192.0.2.0/24", "0.0.0.0", 11, "ActiveStore") { InterfaceIndex = 17 },
            new MockRoute("other-adapter-instance", "192.0.2.0/24", "0.0.0.0", 11, "ActiveStore") { InterfaceIndex = 18 }
        };
        List<MockRoute>? remaining = null;
        var service = Service(async (script, _, token, _) =>
        {
            var result = await ExecuteMockedPowerShellAsync(script, routes, token);
            Assert.AreEqual(0, result.ExitCode, result.Error);
            remaining = JsonSerializer.Deserialize<List<MockRoute>>(result.StateJson!)!;
            return result.Output;
        });

        await service.RemoveAsync(Applied("owned-instance"), Adapter());

        Assert.IsNotNull(remaining);
        CollectionAssert.AreEqual(new[] { "other-adapter-instance" }, remaining.Select(x => x.InstanceId).ToArray());
        Assert.AreEqual(18, remaining[0].InterfaceIndex);
    }

    [TestMethod]
    public async Task ChangedMetricOrInstanceDoesNotDeleteReplacementRoute()
    {
        var replacement = new[] { new MockRoute("owned-instance", "192.0.2.0/24", "0.0.0.0", 99, "ActiveStore") };
        var service = Service(async (script, _, token, _) =>
        {
            var result = await ExecuteMockedPowerShellAsync(script, replacement, token);
            Assert.AreNotEqual(0, result.ExitCode);
            Assert.IsTrue(result.Error.Contains("Owned route parameters changed", StringComparison.Ordinal));
            return "";
        });

        await service.RemoveAsync(Applied("owned-instance"), Adapter());
        Assert.AreEqual(99, replacement[0].RouteMetric);

        var newInstance = new[] { new MockRoute("replacement-instance", "192.0.2.0/24", "0.0.0.0", 11, "ActiveStore") };
        var replacementService = Service(async (script, _, token, _) =>
        {
            var result = await ExecuteMockedPowerShellAsync(script, newInstance, token);
            Assert.AreNotEqual(0, result.ExitCode);
            Assert.IsTrue(result.Error.Contains("Owned route identity not found", StringComparison.Ordinal));
            return "";
        });
        await replacementService.RemoveAsync(Applied("owned-instance"), Adapter());
        Assert.AreEqual("replacement-instance", newInstance[0].InstanceId);
    }

    [TestMethod]
    public async Task CreateFailureAfterIntentLeavesNonDeletableRecoveryRecord()
    {
        var journal = new List<AppliedStaticRoute>();
        var calls = new List<string>();
        var service = Service((script, summary, _, _) =>
        {
            calls.Add(summary);
            var planOutput = PlanReadOutput(summary);
            if (planOutput != null) return Task.FromResult(planOutput);
            if (summary.Contains("create IPv4 static route", StringComparison.Ordinal)) throw new IOException("injected failure after New-NetRoute may have changed system state");
            Assert.Fail($"Unexpected PowerShell call: {summary}\n{script}");
            return Task.FromResult("");
        });
        var target = Target();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ApplyAsync(
            [target],
            onCreating: intent => journal.Add(intent)));

        Assert.AreEqual(1, journal.Count);
        Assert.IsFalse(journal[0].OwnershipVerified);
        Assert.AreEqual("", journal[0].InstanceId);
        Assert.IsTrue(calls.Any(x => x.Contains("create IPv4 static route", StringComparison.Ordinal)));
        Assert.IsFalse(calls.Any(x => x.Contains("remove IPv4 static route", StringComparison.Ordinal)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.RemoveAsync(journal[0], Adapter()));
    }

    [TestMethod]
    public async Task ApplyPersistsIntentBeforeCreateAndVerifiedOwnershipAfterReadback()
    {
        var calls = new List<string>();
        var service = Service((script, summary, _, _) =>
        {
            calls.Add(summary);
            var planOutput = PlanReadOutput(summary);
            if (planOutput != null) return Task.FromResult(planOutput);
            if (summary.Contains("create IPv4 static route", StringComparison.Ordinal))
                return Task.FromResult("{\"AddressFamily\":\"IPv4\",\"DestinationPrefix\":\"192.0.2.0/24\",\"NextHop\":\"0.0.0.0\",\"RouteMetric\":1,\"InterfaceIndex\":17,\"InterfaceAlias\":\"Ethernet\",\"PolicyStore\":\"ActiveStore\",\"InstanceId\":\"created-instance\"}");
            Assert.Fail($"Unexpected PowerShell call: {summary}\n{script}");
            return Task.FromResult("");
        });
        AppliedStaticRoute? intent = null;
        AppliedStaticRoute? created = null;
        var creationCountAtIntent = -1;

        var results = await service.ApplyAsync(
            [Target()],
            onCreated: route => created = route,
            onCreating: route => { intent = route; creationCountAtIntent = calls.Count(x => x.Contains("create IPv4 static route", StringComparison.Ordinal)); });

        Assert.IsNotNull(intent);
        Assert.IsFalse(intent.OwnershipVerified);
        Assert.AreEqual("", intent.InstanceId);
        Assert.IsNotNull(created);
        Assert.IsTrue(created.OwnershipVerified);
        Assert.AreEqual("created-instance", created.InstanceId);
        Assert.AreEqual(0, creationCountAtIntent);
        Assert.AreEqual(1, results.Count);
        Assert.IsTrue(results[0].Created);
    }

    [TestMethod]
    public void PendingIntentSurvivesJsonAndLegacyRecordsKeepVerifiedDefault()
    {
        var legacy = JsonSerializer.Deserialize<AppliedStaticRoute>("{\"InstanceId\":\"legacy-id\"}", JsonStore.Options)!;
        Assert.IsTrue(legacy.OwnershipVerified);

        var pending = Applied("");
        pending.OwnershipVerified = false;
        var restored = JsonSerializer.Deserialize<AppliedStaticRoute>(JsonSerializer.Serialize(pending, JsonStore.Options), JsonStore.Options)!;
        Assert.IsFalse(restored.OwnershipVerified);
        Assert.AreEqual("", restored.InstanceId);
    }

    [TestMethod]
    public async Task JournalFailureBeforeCreateStopsBeforeMutation()
    {
        var calls = new List<string>();
        var service = Service((script, summary, _, _) =>
        {
            calls.Add(summary);
            var planOutput = PlanReadOutput(summary);
            if (planOutput != null) return Task.FromResult(planOutput);
            if (summary.Contains("create IPv4 static route", StringComparison.Ordinal)) Assert.Fail("Route creation must not start when its intent cannot be persisted.");
            return Task.FromResult("");
        });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ApplyAsync(
            [Target()],
            onCreating: _ => throw new IOException("recovery journal write failed")));

        Assert.IsFalse(calls.Any(x => x.Contains("create IPv4 static route", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ReusedInterfaceIndexWithMatchingMacCannotCreateRoute()
    {
        var attemptedCreate = false;
        var service = Service(async (script, summary, token, _) =>
        {
            var planOutput = PlanReadOutput(summary);
            if (planOutput != null) return planOutput;
            if (!summary.Contains("create IPv4 static route", StringComparison.Ordinal))
                Assert.Fail($"Unexpected PowerShell call: {summary}");

            var wrapped = $$"""
function Get-NetAdapter {
  param([int]$InterfaceIndex)
  [pscustomobject]@{ Status = 'Up'; InterfaceGuid = [guid]'e52a0bc0-32b7-423f-aa42-dd0013b0b6bd'; MacAddress = '00-11-22-33-44-55' }
}
function New-NetRoute { $global:routeCreated = $true }
try {
{{script}}
} catch {
  Write-Output ('__ERROR__' + $_.Exception.Message)
}
Write-Output ('__CREATED__' + [bool]$global:routeCreated)
""";
            var execution = await RunPowerShellAsync(wrapped, token);
            Assert.AreEqual(0, execution.ExitCode, execution.Error);
            StringAssert.Contains(execution.Output, "__ERROR__Selected adapter identity changed");
            StringAssert.Contains(execution.Output, "__CREATED__False");
            attemptedCreate = true;
            throw new IOException("The adapter identity check rejected a reused interface index.");
        });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ApplyAsync([Target()], onCreating: _ => { }));
        Assert.IsTrue(attemptedCreate);
    }

    [TestMethod]
    public async Task JournalFailureAfterCreateRunsExactOwnershipRollback()
    {
        var calls = new List<string>();
        string? rollbackScript = null;
        var service = Service((script, summary, _, _) =>
        {
            calls.Add(summary);
            var planOutput = PlanReadOutput(summary);
            if (planOutput != null) return Task.FromResult(planOutput);
            if (summary.Contains("create IPv4 static route", StringComparison.Ordinal))
                return Task.FromResult("{\"AddressFamily\":\"IPv4\",\"DestinationPrefix\":\"192.0.2.0/24\",\"NextHop\":\"0.0.0.0\",\"RouteMetric\":1,\"InterfaceIndex\":17,\"InterfaceAlias\":\"Ethernet\",\"PolicyStore\":\"ActiveStore\",\"InstanceId\":\"created-instance\"}");
            if (summary.Contains("remove IPv4 static route", StringComparison.Ordinal))
            {
                rollbackScript = script;
                return Task.FromResult("OK");
            }
            Assert.Fail($"Unexpected PowerShell call: {summary}");
            return Task.FromResult("");
        });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ApplyAsync(
            [Target()],
            onCreating: _ => { },
            onCreated: _ => throw new IOException("verified ownership journal write failed")));

        Assert.IsNotNull(rollbackScript);
        StringAssert.Contains(rollbackScript!, "$instanceId='created-instance'");
        StringAssert.Contains(rollbackScript!, "$policyStore='ActiveStore'");
        Assert.IsTrue(calls.FindIndex(x => x.Contains("create IPv4 static route", StringComparison.Ordinal))
            < calls.FindIndex(x => x.Contains("remove IPv4 static route", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task BatchFailureRollsBackInReverseOrderAndClearsOnlyVerifiedJournalRows()
    {
        var routeState = new List<MockRoute>();
        var journal = new List<AppliedStaticRoute>();
        var createCount = 0;
        var rollbackOrder = new List<string>();
        var service = Service(async (script, summary, token, _) =>
        {
            var planOutput = PlanReadOutput(summary);
            if (planOutput != null) return planOutput;
            if (summary.Contains("create IPv4 static route", StringComparison.Ordinal))
            {
                createCount++;
                if (createCount == 3)
                {
                    throw new IOException("The third route creation failed before any system mutation.");
                }

                var id = createCount == 1 ? "created-first" : "created-second";
                var json = CreateRouteJson(script, id);
                var route = JsonSerializer.Deserialize<RouteSnapshotForTest>(json)!;
                routeState.Add(new MockRoute(route.InstanceId, route.DestinationPrefix, route.NextHop, route.RouteMetric, route.PolicyStore));
                return json;
            }
            if (summary.Contains("remove IPv4 static route", StringComparison.Ordinal))
            {
                rollbackOrder.Add(Regex.Match(script, @"\$instanceId='([^']+)'").Groups[1].Value);
                var result = await ExecuteMockedPowerShellAsync(script, routeState, token);
                Assert.AreEqual(0, result.ExitCode, result.Error);
                var updated = JsonSerializer.Deserialize<List<MockRoute>>(result.StateJson!)!;
                routeState.Clear();
                routeState.AddRange(updated);
                return result.Output;
            }
            Assert.Fail($"Unexpected PowerShell call: {summary}\n{script}");
            return "";
        });

        void PersistIntent(AppliedStaticRoute route) => journal.Add(route);
        void PersistCreated(AppliedStaticRoute route)
        {
            journal.RemoveAll(x => !x.OwnershipVerified && x.RuleId == route.RuleId);
            journal.Add(route);
        }
        void PersistRemoved(AppliedStaticRoute route) => journal.RemoveAll(x => x.InstanceId == route.InstanceId);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ApplyAsync(
            [Target("first", "192.0.2.0/24"), Target("second", "198.51.100.0/24"), Target("third", "203.0.113.0/24")],
            onCreated: PersistCreated,
            onCreating: PersistIntent,
            onRemoved: PersistRemoved));

        Assert.AreEqual(3, createCount);
        CollectionAssert.AreEqual(new[] { "created-second", "created-first" }, rollbackOrder.ToArray());
        Assert.AreEqual(0, routeState.Count, "Both verified routes must be removed; the third write failed before mutation.");
        Assert.AreEqual(1, journal.Count);
        Assert.IsFalse(journal[0].OwnershipVerified, "The third route's intent remains because its ownership was never read back.");
        Assert.AreEqual("third", journal[0].RuleId);
        StringAssert.Contains(exception.Message, "Rollback verified for 2/2");
    }

    [TestMethod]
    public async Task CancellationAfterUnverifiedWriteRethrowsCancellationAndKeepsPendingIntent()
    {
        var routeState = new List<MockRoute>();
        var journal = new List<AppliedStaticRoute>();
        var createCount = 0;
        var rollbackCount = 0;
        using var cancellation = new CancellationTokenSource();
        var service = Service(async (script, summary, token, _) =>
        {
            var planOutput = PlanReadOutput(summary);
            if (planOutput != null) return planOutput;
            if (summary.Contains("create IPv4 static route", StringComparison.Ordinal))
            {
                createCount++;
                var id = createCount == 1 ? "created-first" : "uncertain-second";
                var json = CreateRouteJson(script, id);
                var route = JsonSerializer.Deserialize<RouteSnapshotForTest>(json)!;
                routeState.Add(new MockRoute(route.InstanceId, route.DestinationPrefix, route.NextHop, route.RouteMetric, route.PolicyStore));
                if (createCount == 2) throw new OperationCanceledException(cancellation.Token);
                return json;
            }
            if (summary.Contains("remove IPv4 static route", StringComparison.Ordinal))
            {
                rollbackCount++;
                var result = await ExecuteMockedPowerShellAsync(script, routeState, token);
                Assert.AreEqual(0, result.ExitCode, result.Error);
                var updated = JsonSerializer.Deserialize<List<MockRoute>>(result.StateJson!)!;
                routeState.Clear();
                routeState.AddRange(updated);
                return result.Output;
            }
            Assert.Fail($"Unexpected PowerShell call: {summary}\n{script}");
            return "";
        });

        void PersistIntent(AppliedStaticRoute route) => journal.Add(route);
        void PersistCreated(AppliedStaticRoute route)
        {
            journal.RemoveAll(x => !x.OwnershipVerified && x.RuleId == route.RuleId);
            journal.Add(route);
        }
        void PersistRemoved(AppliedStaticRoute route) => journal.RemoveAll(x => x.InstanceId == route.InstanceId);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.ApplyAsync(
            [Target("first", "192.0.2.0/24"), Target("second", "198.51.100.0/24")],
            onCreated: PersistCreated,
            ct: cancellation.Token,
            onCreating: PersistIntent,
            onRemoved: PersistRemoved));

        Assert.AreEqual(2, createCount);
        Assert.AreEqual(1, rollbackCount, "The verified first route must be rolled back before cancellation is returned.");
        Assert.AreEqual(1, routeState.Count);
        Assert.AreEqual("uncertain-second", routeState[0].InstanceId, "A potentially-created route without verified ownership must not be guessed at or deleted.");
        Assert.AreEqual(1, journal.Count);
        Assert.IsFalse(journal[0].OwnershipVerified);
        Assert.AreEqual("second", journal[0].RuleId);
    }

    [TestMethod]
    public async Task MalformedCreateReadbackPreservesIntentAndNeverAttemptsDeletion()
    {
        var calls = new List<string>();
        var journal = new List<AppliedStaticRoute>();
        var service = Service((script, summary, _, _) =>
        {
            calls.Add(summary);
            var planOutput = PlanReadOutput(summary);
            if (planOutput != null) return Task.FromResult(planOutput);
            if (summary.Contains("create IPv4 static route", StringComparison.Ordinal)) return Task.FromResult("{broken JSON");
            if (summary.Contains("remove IPv4 static route", StringComparison.Ordinal)) Assert.Fail("An unverified creation intent must never authorize deletion.");
            return Task.FromResult("");
        });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ApplyAsync(
            [Target("malformed", "192.0.2.0/24")],
            onCreating: journal.Add));

        Assert.AreEqual(1, journal.Count);
        Assert.IsFalse(journal[0].OwnershipVerified);
        Assert.IsFalse(calls.Any(x => x.Contains("remove IPv4 static route", StringComparison.Ordinal)));
    }

    private static string? PlanReadOutput(string summary)
    {
        if (summary.Contains("read route planning snapshot", StringComparison.Ordinal))
            return """{"Routes":[],"InterfaceMetrics":[{"AddressFamily":"IPv4","InterfaceIndex":"17","InterfaceMetric":10},{"AddressFamily":"IPv6","InterfaceIndex":"17","InterfaceMetric":10}],"InterfaceStates":[{"AddressFamily":"IPv4","InterfaceIndex":"17","ConnectionState":"Connected"},{"AddressFamily":"IPv6","InterfaceIndex":"17","ConnectionState":"Connected"}],"InterfaceAddresses":[]}""";
        return null;
    }

    private static string RoutePlanningSnapshotJson(IReadOnlyList<CurrentStaticRoute> routes) => JsonSerializer.Serialize(new
    {
        Routes = routes,
        InterfaceMetrics = new[]
        {
            new InterfaceMetricInfo { AddressFamily = "IPv4", InterfaceIndex = "17", InterfaceMetric = 10 },
            new InterfaceMetricInfo { AddressFamily = "IPv6", InterfaceIndex = "17", InterfaceMetric = 10 }
        },
        InterfaceStates = new[]
        {
            new InterfaceStateInfo { AddressFamily = "IPv4", InterfaceIndex = "17", ConnectionState = "Connected" },
            new InterfaceStateInfo { AddressFamily = "IPv6", InterfaceIndex = "17", ConnectionState = "Connected" }
        },
        InterfaceAddresses = Array.Empty<InterfaceAddressInfo>()
    });

    private static string WrapRoutePlanningScript(string script, string tracePath, string interfacesJson, string routesJson, string addressesJson)
    {
        var escapedTracePath = tracePath.Replace("'", "''", StringComparison.Ordinal);
        return $$"""
$global:mockTracePath = '{{escapedTracePath}}'
$global:mockInterfaces = @'
{{interfacesJson}}
'@ | ConvertFrom-Json
$global:mockRoutes = @'
{{routesJson}}
'@ | ConvertFrom-Json
$global:mockAddresses = @'
{{addressesJson}}
'@ | ConvertFrom-Json
function Add-MockTrace([string]$name) { [IO.File]::AppendAllText($global:mockTracePath, $name + [Environment]::NewLine) }
function Get-NetIPInterface {
  param([string[]]$AddressFamily)
  if (($AddressFamily -join ',') -ne 'IPv4,IPv6') { throw 'Unexpected address-family query.' }
  Add-MockTrace 'Get-NetIPInterface'
  $global:mockInterfaces
}
function Get-NetRoute {
  param([string[]]$AddressFamily)
  if (($AddressFamily -join ',') -ne 'IPv4,IPv6') { throw 'Unexpected address-family query.' }
  Add-MockTrace 'Get-NetRoute'
  $global:mockRoutes
}
function Get-NetIPAddress {
  param([string[]]$AddressFamily)
  if (($AddressFamily -join ',') -ne 'IPv4,IPv6') { throw 'Unexpected address-family query.' }
  Add-MockTrace 'Get-NetIPAddress'
  $global:mockAddresses
}
try {
{{script}}
} catch {
  [Console]::Error.WriteLine($_.Exception.ToString())
  exit 23
}
""";
    }

    private static StaticRouteService Service(PowerShellScriptExecutor executor) => new(NullLogger.Instance, executor, () => true);

    private static StaticRouteTarget Target() => Target("route-rule", "192.0.2.0/24");

    private static StaticRouteTarget Target(string ruleId, string destinationPrefix)
    {
        var adapter = Adapter();
        var rule = new StaticRouteRule { Id = ruleId, DestinationPrefix = destinationPrefix, AdapterId = adapter.Id };
        return new StaticRouteTarget(rule, adapter, StaticRouteValidator.Normalize(rule));
    }

    private static string CreateRouteJson(string script, string instanceId)
    {
        var destination = Regex.Match(script, @"\$destination='([^']+)'").Groups[1].Value;
        var nextHop = Regex.Match(script, @"\$nextHop='([^']+)'").Groups[1].Value;
        var family = Regex.Match(script, @"\$family='([^']+)'").Groups[1].Value;
        var metric = int.Parse(Regex.Match(script, @"-RouteMetric (\d+)").Groups[1].Value);
        return JsonSerializer.Serialize(new RouteSnapshotForTest(family, destination, nextHop, metric, 17, "Ethernet", "ActiveStore", instanceId));
    }

    private static NetworkAdapterInfo Adapter() => new()
    {
        Id = "d7866951-1034-46a9-a94b-627c6137c835",
        InterfaceIndex = "17",
        Name = "Ethernet",
        MacAddress = "00-11-22-33-44-55",
        Status = "Up"
    };

    private static AppliedStaticRoute Applied(string id) => new()
    {
        RuleId = "route-rule",
        DestinationPrefix = "192.0.2.0/24",
        AddressFamily = "IPv4",
        AdapterId = Adapter().Id,
        AdapterName = Adapter().Name,
        AdapterMac = Adapter().MacAddress,
        InterfaceIndex = 17,
        NextHop = "0.0.0.0",
        RouteMetric = 11,
        PolicyStore = "ActiveStore",
        InstanceId = id
    };

    private static async Task<MockPowerShellResult> ExecuteMockedPowerShellAsync(string script, IReadOnlyList<MockRoute> routes, CancellationToken cancellationToken)
    {
        var routeJson = JsonSerializer.Serialize(routes);
        var wrapped = $$"""
$global:mockRoutes = ConvertFrom-Json @'
{{routeJson}}
'@
function Get-NetRoute {
  [CmdletBinding()]
  param([int]$InterfaceIndex, [string]$AddressFamily, [string]$DestinationPrefix)
  $matches = @($global:mockRoutes | Where-Object {
    (!$PSBoundParameters.ContainsKey('InterfaceIndex') -or [int]$_.InterfaceIndex -eq $InterfaceIndex) -and
    (!$PSBoundParameters.ContainsKey('AddressFamily') -or [string]$_.AddressFamily -eq $AddressFamily) -and
    (!$PSBoundParameters.ContainsKey('DestinationPrefix') -or [string]$_.DestinationPrefix -eq $DestinationPrefix)
  })
  if ($PSBoundParameters.ContainsKey('DestinationPrefix') -and $matches.Count -eq 0) { throw 'CIM query returned no matching MSFT_NetRoute instance' }
  $matches
}
function Remove-NetRoute {
  [CmdletBinding()]
  param([Parameter(ValueFromPipeline=$true)]$InputObject, [switch]$Confirm)
  process { $removedInstanceId = [string]$InputObject.InstanceId; $global:mockRoutes = @($global:mockRoutes | Where-Object { [string]$_.InstanceId -ne $removedInstanceId }) }
}
try {
{{script}}
} catch {
  [Console]::Error.WriteLine($_.Exception.Message)
  exit 23
}
Write-Output ('__MOCK_STATE__' + (ConvertTo-Json -InputObject @($global:mockRoutes) -Compress -Depth 4))
""";
        var execution = await RunPowerShellAsync(wrapped, cancellationToken);
        var marker = execution.Output.LastIndexOf("__MOCK_STATE__", StringComparison.Ordinal);
        return new MockPowerShellResult(execution.ExitCode, execution.Output, execution.Error,
            marker >= 0 ? execution.Output[(marker + "__MOCK_STATE__".Length)..] : null);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunPowerShellAsync(string script, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
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
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(outputTask, errorTask);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, outputTask.Result.Trim(), errorTask.Result.Trim());
    }

    private sealed record MockRoute(string InstanceId, string DestinationPrefix, string NextHop, int RouteMetric, string Store)
    {
        public string AddressFamily { get; init; } = "IPv4";
        public int InterfaceIndex { get; init; } = 17;
    }

    private sealed record RouteSnapshotForTest(string AddressFamily, string DestinationPrefix, string NextHop, int RouteMetric, int InterfaceIndex, string InterfaceAlias, string PolicyStore, string InstanceId);

    private sealed record MockPowerShellResult(int ExitCode, string Output, string Error, string? StateJson);

    private sealed class NullLogger : ILogger
    {
        public static NullLogger Instance { get; } = new();
        public event Action<string>? LineWritten { add { } remove { } }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
