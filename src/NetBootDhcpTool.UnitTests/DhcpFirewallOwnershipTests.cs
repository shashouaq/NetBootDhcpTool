using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class DhcpFirewallOwnershipTests
{
    [TestMethod]
    public async Task IntentIsPersistedBeforeFirstWriteAndEachRuleIsReadBackBeforeNextWrite()
    {
        var persisted = new List<string>();
        var scripts = new List<string>();
        var service = Service((script, _, _, _) =>
        {
            Assert.IsTrue(persisted.Count > scripts.Count, "The intent/readback record must be committed before the next firewall mutation.");
            scripts.Add(script);
            var name = RuleName(script);
            return Task.FromResult(JsonSerializer.Serialize(new { Name = name, InstanceId = $"instance-{scripts.Count}" }));
        });

        var lease = await service.EnsureDhcpFirewallRulesAsync("Isolated Lab", value => persisted.Add(Snapshot(value)));

        Assert.AreEqual(2, scripts.Count);
        Assert.AreEqual(3, persisted.Count);
        var initial = JsonSerializer.Deserialize<DhcpFirewallRuleLease>(persisted[0], JsonStore.Options)!;
        Assert.AreEqual(2, initial.Rules.Count);
        Assert.IsTrue(initial.Rules.All(x => !x.OwnershipVerified));
        var afterFirst = JsonSerializer.Deserialize<DhcpFirewallRuleLease>(persisted[1], JsonStore.Options)!;
        Assert.AreEqual(1, afterFirst.Rules.Count(x => x.OwnershipVerified));
        Assert.AreEqual(1, afterFirst.Rules.Count(x => !x.OwnershipVerified));
        Assert.IsTrue(lease.Rules.All(x => x.OwnershipVerified && !string.IsNullOrWhiteSpace(x.InstanceId)));
        Assert.IsTrue(scripts[0].IndexOf("$name=", StringComparison.Ordinal) >= 0);
        Assert.IsTrue(scripts[0].Contains("New-NetFirewallRule @createArgs", StringComparison.Ordinal));
        Assert.IsTrue(scripts[0].Contains("Get-NetFirewallPortFilter", StringComparison.Ordinal));
        Assert.IsTrue(scripts[0].Contains("Get-NetFirewallInterfaceFilter", StringComparison.Ordinal));
        Assert.IsTrue(scripts[0].Contains("$actualInterfaces=@()", StringComparison.Ordinal));
        Assert.IsTrue(scripts[0].Contains("if ($iface.Count -eq 1) { $actualInterfaces=@($iface[0].InterfaceAlias) }", StringComparison.Ordinal));
        Assert.IsFalse(scripts[0].Contains("$actualInterfaces=if", StringComparison.Ordinal));
        Assert.IsFalse(scripts.Any(x => x.Contains("Get-NetFirewallRule -DisplayName", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task SecondCreateFailureLeavesDurableExactIntentForCompensation()
    {
        var saved = new List<string>();
        var createCount = 0;
        var removeScripts = new List<string>();
        DhcpFirewallRuleLease? lease = null;
        var service = Service((script, summary, _, _) =>
        {
            if (summary.Contains("create DHCP firewall rule", StringComparison.Ordinal))
            {
                createCount++;
                if (createCount == 2) throw new IOException("injected failure after the second create may have mutated system state");
                return Task.FromResult(JsonSerializer.Serialize(new { Name = RuleName(script), InstanceId = "created-first" }));
            }
            if (summary.Contains("remove owned DHCP firewall rule", StringComparison.Ordinal))
            {
                removeScripts.Add(script);
                return Task.FromResult("OK");
            }
            Assert.Fail($"Unexpected PowerShell action: {summary}");
            return Task.FromResult("");
        });

        await Assert.ThrowsExactlyAsync<IOException>(() => service.EnsureDhcpFirewallRulesAsync(
            "Isolated Lab",
            value => { lease = value; saved.Add(Snapshot(value)); }));

        Assert.IsNotNull(lease);
        Assert.AreEqual(2, saved.Count);
        Assert.AreEqual(1, lease!.Rules.Count(x => x.OwnershipVerified));
        Assert.AreEqual(1, lease.Rules.Count(x => !x.OwnershipVerified));
        var journaled = JsonSerializer.Deserialize<DhcpFirewallRuleLease>(saved[^1], JsonStore.Options)!;
        Assert.AreEqual(2, journaled.Rules.Count);
        Assert.AreEqual(1, journaled.Rules.Count(x => x.OwnershipVerified));

        await service.RemoveDhcpFirewallRulesAsync(lease, value => saved.Add(Snapshot(value)));

        Assert.AreEqual(2, removeScripts.Count);
        Assert.AreEqual(0, lease.Rules.Count);
        Assert.IsTrue(removeScripts.All(x => x.Contains("Get-NetFirewallRule -Name $name", StringComparison.Ordinal)));
        Assert.IsTrue(removeScripts.All(x => x.Contains("$rule | Remove-NetFirewallRule", StringComparison.Ordinal)));
        Assert.IsTrue(removeScripts.All(x => x.Contains("Firewall rule ownership or attributes changed", StringComparison.Ordinal)));
        Assert.IsTrue(removeScripts.All(x => x.Contains("Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue).Count -gt 0", StringComparison.Ordinal)));
        Assert.IsTrue(removeScripts.All(x => !x.Contains("Get-NetFirewallRule -DisplayName", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task FailedIntentPersistencePreventsAnyFirewallMutation()
    {
        var calls = 0;
        var service = Service((_, _, _, _) =>
        {
            calls++;
            return Task.FromResult("");
        });

        await Assert.ThrowsExactlyAsync<IOException>(() => service.EnsureDhcpFirewallRulesAsync(
            "Isolated Lab",
            _ => throw new IOException("injected recovery journal failure")));

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task CreateOrReadbackFailureRetainsIntentThatCanBeCleanedAfterRestart()
    {
        var journal = new List<string>();
        string? createScript = null;
        string? recoveryScript = null;
        DhcpFirewallRuleLease? lease = null;
        var service = Service((script, summary, _, _) =>
        {
            if (summary.Contains("create DHCP firewall rule", StringComparison.Ordinal))
            {
                createScript = script;
                throw new IOException("injected New-NetFirewallRule or readback failure");
            }
            recoveryScript = script;
            return Task.FromResult("OK");
        });

        await Assert.ThrowsExactlyAsync<IOException>(() => service.EnsureDhcpFirewallRulesAsync(
            "Isolated Lab",
            value => { lease = value; journal.Add(Snapshot(value)); }));

        Assert.IsNotNull(lease);
        Assert.AreEqual(1, journal.Count);
        Assert.IsTrue(lease!.Rules.All(x => !x.OwnershipVerified && string.IsNullOrEmpty(x.InstanceId)));
        var reopened = JsonSerializer.Deserialize<DhcpFirewallRuleLease>(journal[0], JsonStore.Options)!;
        await Service((script, _, _, _) =>
        {
            recoveryScript = script;
            return Task.FromResult("OK");
        }).RemoveDhcpFirewallRulesAsync(reopened);

        Assert.IsNotNull(createScript);
        Assert.IsNotNull(recoveryScript);
        Assert.IsTrue(recoveryScript!.Contains("$instanceId=''", StringComparison.Ordinal));
        Assert.IsTrue(recoveryScript.Contains("Owner=", StringComparison.Ordinal));
        Assert.AreEqual(0, reopened.Rules.Count);
    }

    [TestMethod]
    public async Task JournalFailureAfterCreateLeavesPrewriteIntentForNextRun()
    {
        var persisted = new List<string>();
        DhcpFirewallRuleLease? lease = null;
        var service = Service((script, _, _, _) => Task.FromResult(JsonSerializer.Serialize(new { Name = RuleName(script), InstanceId = "instance-after-create" })));

        await Assert.ThrowsExactlyAsync<IOException>(() => service.EnsureDhcpFirewallRulesAsync(
            "Isolated Lab",
            value =>
            {
                lease = value;
                if (persisted.Count == 0) persisted.Add(Snapshot(value));
                else throw new IOException("injected journal failure after firewall rule creation");
            }));

        Assert.IsNotNull(lease);
        Assert.IsTrue(lease!.Rules[0].OwnershipVerified);
        var durable = JsonSerializer.Deserialize<DhcpFirewallRuleLease>(persisted[0], JsonStore.Options)!;
        Assert.IsTrue(durable.Rules.All(x => !x.OwnershipVerified));
        StringAssert.Contains(durable.Rules[0].Description, $"Owner={durable.LeaseId}");
    }

    [TestMethod]
    public async Task CancellationAfterFirstRuleKeepsBothIntentsAndAllowsUncancelledCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var saved = new List<string>();
        var cleanupScripts = new List<string>();
        DhcpFirewallRuleLease? lease = null;
        var service = Service((script, summary, _, _) =>
        {
            if (summary.Contains("create DHCP firewall rule", StringComparison.Ordinal))
            {
                cancellation.Cancel();
                return Task.FromResult(JsonSerializer.Serialize(new { Name = RuleName(script), InstanceId = "first-instance" }));
            }
            cleanupScripts.Add(script);
            return Task.FromResult("OK");
        });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.EnsureDhcpFirewallRulesAsync(
            "Isolated Lab",
            value => { lease = value; saved.Add(Snapshot(value)); },
            cancellation.Token));

        Assert.IsNotNull(lease);
        Assert.AreEqual(1, lease!.Rules.Count(x => x.OwnershipVerified));
        Assert.AreEqual(1, lease.Rules.Count(x => !x.OwnershipVerified));
        await service.RemoveDhcpFirewallRulesAsync(lease, value => saved.Add(Snapshot(value)), CancellationToken.None);
        Assert.AreEqual(2, cleanupScripts.Count);
        Assert.AreEqual(0, lease.Rules.Count);
    }

    [TestMethod]
    public async Task ChangedRuleAttributesFailClosedAndLeaveJournalForRecovery()
    {
        var calls = 0;
        string? removal = null;
        var service = Service((script, summary, _, _) =>
        {
            if (summary.Contains("create DHCP firewall rule", StringComparison.Ordinal))
                return Task.FromResult(JsonSerializer.Serialize(new { Name = RuleName(script), InstanceId = $"instance-{++calls}" }));
            removal = script;
            throw new IOException("Firewall rule ownership or attributes changed; journal retained.");
        });
        var lease = await service.EnsureDhcpFirewallRulesAsync("Isolated Lab");
        var before = Snapshot(lease);

        await Assert.ThrowsExactlyAsync<IOException>(() => service.RemoveDhcpFirewallRulesAsync(lease));

        Assert.AreEqual(before, Snapshot(lease));
        StringAssert.Contains(removal!, "$rule.InstanceID -ieq $instanceId");
        StringAssert.Contains(removal!, "$actualProgram -ieq $program");
        StringAssert.Contains(removal!, "$actualInterfaces[0] -ieq $interface");
        StringAssert.Contains(removal!, "$actualInterfaces=@()");
        StringAssert.Contains(removal!, "if ($iface.Count -eq 1) { $actualInterfaces=@($iface[0].InterfaceAlias) }");
        StringAssert.Contains(removal!, "$actualLocal -ceq $localPort");
        StringAssert.Contains(removal!, "$actualRemote -ceq $remotePort");
        StringAssert.Contains(removal!, "$rule.Description -ceq $description");
        StringAssert.Contains(removal!, "Firewall rule ownership or attributes changed; journal retained.");
    }

    [TestMethod]
    public async Task EmptyAndAlreadyRemovedLeasesAreIdempotent()
    {
        var calls = 0;
        var service = Service((_, _, _, _) =>
        {
            calls++;
            return Task.FromResult("OK");
        });
        var empty = new DhcpFirewallRuleLease();

        await service.RemoveDhcpFirewallRulesAsync(empty);
        await service.RemoveDhcpFirewallRulesAsync(empty);

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public void FirewallLeaseJournalRoundTripsOwnershipAndPendingIntent()
    {
        var original = new DhcpFirewallRuleLease
        {
            LeaseId = "lease-id",
            InterfaceAlias = "Isolated Lab",
            ProgramPath = @"C:\Tools\NetBootDhcpTool.exe",
            CreatedAt = DateTimeOffset.UtcNow,
            Rules =
            [
                new DhcpFirewallRuleRecord
                {
                    Name = "NetBootDhcpTool-owned",
                    DisplayName = "NetBoot DHCP Tool Inbound",
                    Description = "NetBootDhcpTool Owner=lease-id Rule=owned",
                    Group = "NetBootDhcpTool",
                    Direction = "Inbound",
                    LocalPort = "67",
                    RemotePort = "Any",
                    InterfaceAlias = "Isolated Lab",
                    ProgramPath = @"C:\Tools\NetBootDhcpTool.exe",
                    LeaseId = "lease-id",
                    InstanceId = "instance-1",
                    OwnershipVerified = true
                },
                new DhcpFirewallRuleRecord { Name = "NetBootDhcpTool-pending", LeaseId = "lease-id" }
            ]
        };

        var restored = JsonSerializer.Deserialize<DhcpFirewallRuleLease>(Snapshot(original), JsonStore.Options)!;

        Assert.AreEqual(original.LeaseId, restored.LeaseId);
        Assert.AreEqual(2, restored.Rules.Count);
        Assert.IsTrue(restored.Rules[0].OwnershipVerified);
        Assert.AreEqual("instance-1", restored.Rules[0].InstanceId);
        Assert.IsFalse(restored.Rules[1].OwnershipVerified);
    }

    private static NetworkAdapterService Service(PowerShellScriptExecutor executor) => new(NullLogger.Instance, executor);

    private static string RuleName(string script)
    {
        var match = Regex.Match(script, @"\$name='(?<name>[^']+)'");
        Assert.IsTrue(match.Success, "Create script needs a unique firewall rule identity.");
        return match.Groups["name"].Value;
    }

    private static string Snapshot(DhcpFirewallRuleLease lease) => JsonSerializer.Serialize(lease, JsonStore.Options);

    private sealed class NullLogger : ILogger
    {
        public static NullLogger Instance { get; } = new();
        public event Action<string>? LineWritten { add { } remove { } }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
