using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
[DoNotParallelize]
public sealed class NetworkAdapterMutationTests
{
    [TestMethod]
    public async Task LegacyWifiDhcpSettingIsIgnoredAndServiceStillRejectsWlanIpv4Writes()
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>(
            "{\"Language\":\"en-US\",\"AllowDhcpOnWifi\":true,\"AllowDhcpOnAdapterWithGateway\":true}",
            JsonStore.Options)!;
        Assert.AreEqual("en-US", legacy.Language);
        Assert.IsTrue(legacy.AllowDhcpOnAdapterWithGateway, "Other existing safety settings remain compatible.");

        var writes = 0;
        var service = Service((_, _, _, _) =>
        {
            writes++;
            return Task.FromResult("unexpected write");
        });
        var wifi = Adapter();
        wifi.IsWifi = true;

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.ApplyStaticIPv4Async(wifi, "192.0.2.10", "255.255.255.0", "", ""));

        Assert.AreEqual(0, writes, "The legacy true value is ignored; the network layer still blocks WLAN before PowerShell.");
    }

    [TestMethod]
    public async Task MacWriteThatMutatesThenThrowsIsRestoredAndVerified()
    {
        var original = "02-00-00-00-00-01";
        var requested = "02-00-00-00-00-02";
        var current = original;
        var macWrites = 0;
        var restoreRecordCleared = false;
        var service = Service((script, summary, _, _) =>
        {
            if (summary.Contains("change adapter MAC", StringComparison.Ordinal))
            {
                current = ReadRequestedMac(script);
                macWrites++;
                if (macWrites == 1) throw new IOException("Set-NetAdapter changed the MAC, then the response read failed.");
                return Task.FromResult("OK");
            }
            Assert.Fail($"Unexpected PowerShell action: {summary}");
            return Task.FromResult("");
        });

        await Assert.ThrowsExactlyAsync<IOException>(() => service.RunWithMacRecoveryAsync(
            Adapter(), original,
            token => service.ChangeMacAddressAsync(Adapter(), requested, allowAnyAdapter: true, token),
            (expected, _) =>
            {
                Assert.AreEqual(expected, NetworkAdapterService.NormalizeMacAddress(current));
                return Task.CompletedTask;
            },
            "Test MAC change",
            CancellationToken.None,
            onRestored: () => restoreRecordCleared = true));

        Assert.AreEqual(2, macWrites, "One failed post-write command must be followed by one compensating write.");
        Assert.AreEqual(NetworkAdapterService.NormalizeMacAddress(original), current);
        Assert.IsTrue(restoreRecordCleared, "A verified compensation may clear the temporary recovery record.");
    }

    [TestMethod]
    public async Task FailedMacCompensationInvokesPendingRecoveryRecordCallback()
    {
        var current = "02-00-00-00-00-01";
        var writes = 0;
        var recoveryRetained = false;
        var recoveryPath = Path.Combine(Path.GetTempPath(), "netboot-mac-recovery-" + Guid.NewGuid().ToString("N") + ".json");
        var backup = new AdapterMacBackup
        {
            AdapterId = Adapter().Id,
            InterfaceIndex = Adapter().InterfaceIndex,
            AdapterName = Adapter().Name,
            OriginalMacAddress = current,
            RestoreOnExit = true
        };
        var service = Service((script, summary, _, _) =>
        {
            if (!summary.Contains("change adapter MAC", StringComparison.Ordinal))
            {
                Assert.Fail($"Unexpected PowerShell action: {summary}");
                return Task.FromResult("");
            }
            writes++;
            if (writes == 1)
            {
                current = ReadRequestedMac(script);
                throw new IOException("Primary MAC command failed after the adapter changed.");
            }
            throw new IOException("Compensating MAC command failed.");
        });

        try
        {
            var error = await Assert.ThrowsExactlyAsync<AggregateException>(() => service.RunWithMacRecoveryAsync(
                Adapter(), "02-00-00-00-00-01",
                token => service.ChangeMacAddressAsync(Adapter(), "02-00-00-00-00-02", allowAnyAdapter: true, token),
                (_, _) => Task.CompletedTask,
                "Test MAC change",
                CancellationToken.None,
                onRecoveryRequired: _ =>
                {
                    backup.RestoreOnExit = true;
                    JsonStore.Save(recoveryPath, new List<AdapterMacBackup> { backup });
                    recoveryRetained = true;
                }));

            Assert.AreEqual(2, error.InnerExceptions.Count);
            Assert.IsTrue(recoveryRetained, "Unverified compensation must retain the durable recovery record.");
            Assert.AreEqual("02-00-00-00-00-02", current, "The fake adapter remains changed so the recovery case is not mistaken for success.");
            var savedBackups = JsonStore.Load<List<AdapterMacBackup>>(recoveryPath);
            Assert.AreEqual(DataLoadStatus.Loaded, savedBackups.Status);
            Assert.AreEqual(NetworkAdapterService.NormalizeMacAddress("02-00-00-00-00-01"), savedBackups.Value![0].OriginalMacAddress);
            Assert.IsTrue(savedBackups.Value[0].RestoreOnExit);
        }
        finally
        {
            if (File.Exists(recoveryPath)) File.Delete(recoveryPath);
            if (File.Exists(recoveryPath + ".bak")) File.Delete(recoveryPath + ".bak");
        }
    }

    [TestMethod]
    public async Task MacCancellationAfterWriteUsesIndependentCompensationToken()
    {
        var original = "02-00-00-00-00-01";
        var current = original;
        using var operation = new CancellationTokenSource();
        var writes = 0;
        var restored = false;
        var service = Service((script, summary, token, _) =>
        {
            if (!summary.Contains("change adapter MAC", StringComparison.Ordinal))
            {
                Assert.Fail($"Unexpected PowerShell action: {summary}");
                return Task.FromResult("");
            }
            current = ReadRequestedMac(script);
            writes++;
            if (writes == 1)
            {
                operation.Cancel();
                throw new OperationCanceledException(operation.Token);
            }
            Assert.IsFalse(token.IsCancellationRequested, "Compensation must not reuse the canceled operation token.");
            return Task.FromResult("OK");
        });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.RunWithMacRecoveryAsync(
            Adapter(), original,
            token => service.ChangeMacAddressAsync(Adapter(), "02-00-00-00-00-02", allowAnyAdapter: true, token),
            (expected, _) =>
            {
                Assert.AreEqual(expected, NetworkAdapterService.NormalizeMacAddress(current));
                return Task.CompletedTask;
            },
            "Test MAC change",
            operation.Token,
            onRestored: () => restored = true));

        Assert.AreEqual(2, writes);
        Assert.AreEqual(NetworkAdapterService.NormalizeMacAddress(original), current);
        Assert.IsTrue(restored);
    }

    [TestMethod]
    public async Task RestoringStaticEmptyConfigurationDoesNotEnableDhcp()
    {
        string? script = null;
        var service = Service((value, _, _, _) => { script = value; return Task.FromResult("OK"); });
        var snapshot = Snapshot(dhcp: false, dnsMode: AdapterDnsMode.Automatic, enabled: true);
        snapshot.AutomaticMetric = false;
        snapshot.InterfaceMetric = 37;

        await service.RestoreIPv4ConfigAsync(Adapter(), snapshot);

        StringAssert.Contains(script!, "-Dhcp Disabled -ErrorAction Stop");
        Assert.IsFalse(script!.Contains("-Dhcp Enabled", StringComparison.Ordinal));
        Assert.IsFalse(script.Contains("New-NetIPAddress", StringComparison.Ordinal));
        StringAssert.Contains(script, "-AutomaticMetric Disabled -InterfaceMetric 37 -ErrorAction Stop");
        StringAssert.Contains(script, "-ResetServerAddresses -ErrorAction Stop");
    }

    [TestMethod]
    public async Task AdapterIpv4SnapshotAndRestoreFilterMissingDefaultRoutesWithoutExactCimQueries()
    {
        var scripts = new List<(string Summary, string Script)>();
        var baseline = new AdapterIpv4Snapshot
        {
            DhcpEnabled = false,
            DnsMode = AdapterDnsMode.Static,
            Dns = ["192.0.2.53"],
            AdapterEnabled = true,
            AutomaticMetric = false,
            InterfaceMetric = 9000,
            Addresses = [new AdapterIpv4AddressSnapshot { IpAddress = "192.0.2.1", PrefixLength = 24 }]
        };
        var service = Service((script, summary, _, _) =>
        {
            scripts.Add((summary, script));
            return Task.FromResult(summary.Contains("capture target adapter IPv4", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(baseline, JsonStore.Options)
                : "OK");
        });

        await service.CaptureIPv4ConfigAsync(Adapter());
        await service.ApplyStaticIPv4Async(Adapter(), "192.0.2.10", "255.255.255.0", "", "");
        await service.RestoreIPv4ConfigAsync(Adapter(), baseline);
        await service.RestoreIPv4ConfigAsync(Adapter(), new AdapterIpv4Snapshot
        {
            DhcpEnabled = true,
            DnsMode = AdapterDnsMode.Automatic,
            AdapterEnabled = true,
            AutomaticMetric = true
        });

        Assert.AreEqual(4, scripts.Count);
        StringAssert.Contains(scripts[0].Script, "$adapterGuid = [guid]$netAdapter.InterfaceGuid");
        foreach (var (_, script) in scripts)
        {
            StringAssert.Contains(script, "Get-NetRoute -AddressFamily IPv4 -ErrorAction Stop");
            StringAssert.Contains(script, "[int]$_.InterfaceIndex -eq $idx");
            Assert.IsFalse(script.Contains("-DestinationPrefix '0.0.0.0/0' -ErrorAction Stop", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task RestoreUsesSavedAutomaticOrStaticDnsMode()
    {
        var scripts = new List<string>();
        var service = Service((script, _, _, _) => { scripts.Add(script); return Task.FromResult("OK"); });
        await service.RestoreIPv4ConfigAsync(Adapter(), Snapshot(dhcp: false, dnsMode: AdapterDnsMode.Automatic, enabled: true));
        var manual = Snapshot(dhcp: false, dnsMode: AdapterDnsMode.Static, enabled: true);
        manual.Dns = ["192.0.2.53", "192.0.2.54"];
        await service.RestoreIPv4ConfigAsync(Adapter(), manual);

        StringAssert.Contains(scripts[0], "-ResetServerAddresses -ErrorAction Stop");
        StringAssert.Contains(scripts[1], "-ServerAddresses @('192.0.2.53','192.0.2.54') -ErrorAction Stop");
        Assert.IsFalse(scripts[1].Contains("-ResetServerAddresses", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task DnsClientCommandsUseSupportedParametersForApplyAndEveryRestoreMode()
    {
        var scripts = new List<string>();
        var service = Service((script, _, _, _) => { scripts.Add(script); return Task.FromResult("OK"); });
        await service.ApplyStaticIPv4Async(Adapter(), "192.0.2.10", "255.255.255.0", "", "");
        await service.RestoreDhcpAsync(Adapter());
        await service.RestoreIPv4ConfigAsync(Adapter(), Snapshot(dhcp: false, dnsMode: AdapterDnsMode.Automatic, enabled: true));
        await service.RestoreIPv4ConfigAsync(Adapter(), Snapshot(dhcp: true, dnsMode: AdapterDnsMode.Automatic, enabled: true));
        var manual = Snapshot(dhcp: false, dnsMode: AdapterDnsMode.Static, enabled: true);
        manual.Dns = ["192.0.2.53"];
        await service.RestoreIPv4ConfigAsync(Adapter(), manual);

        var dnsCommands = scripts.SelectMany(script => Regex.Split(script, @"\r?\n"))
            .Where(line => line.TrimStart().StartsWith("Set-DnsClientServerAddress", StringComparison.Ordinal))
            .ToArray();
        Assert.AreEqual(5, dnsCommands.Length);
        foreach (var command in dnsCommands)
            Assert.IsFalse(command.Contains("-AddressFamily", StringComparison.Ordinal), command);
        Assert.IsTrue(dnsCommands.Take(4).All(command => command.Contains("-ResetServerAddresses", StringComparison.Ordinal)));
        StringAssert.Contains(dnsCommands[4], "-ServerAddresses @('192.0.2.53')");
    }

    [TestMethod]
    public async Task UnknownLegacyDnsModeBlocksRestoreBeforeAnyWrite()
    {
        var calls = 0;
        var service = Service((_, _, _, _) => { calls++; return Task.FromResult("OK"); });

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            service.RestoreIPv4ConfigAsync(Adapter(), Snapshot(dhcp: false, dnsMode: AdapterDnsMode.Unknown, enabled: true)));

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task RestoreScriptRetainsMultipleAddressesRoutesAndSkipAsSource()
    {
        string? script = null;
        var service = Service((value, _, _, _) => { script = value; return Task.FromResult("OK"); });
        var snapshot = Snapshot(dhcp: false, dnsMode: AdapterDnsMode.Automatic, enabled: false);
        snapshot.Addresses =
        [
            new AdapterIpv4AddressSnapshot { IpAddress = "192.0.2.10", PrefixLength = 24 },
            new AdapterIpv4AddressSnapshot { IpAddress = "192.0.2.11", PrefixLength = 24, SkipAsSource = true }
        ];
        snapshot.Routes =
        [
            new AdapterRouteSnapshot { NextHop = "192.0.2.1", RouteMetric = 7, PolicyStore = "ActiveStore", Protocol = "NetMgmt" },
            new AdapterRouteSnapshot { NextHop = "192.0.2.2", RouteMetric = 19, PolicyStore = "PersistentStore", Protocol = "NetMgmt" }
        ];

        await service.RestoreIPv4ConfigAsync(Adapter(), snapshot);

        StringAssert.Contains(script!, "-IPAddress '192.0.2.10' -PrefixLength 24 -SkipAsSource $false");
        StringAssert.Contains(script!, "-IPAddress '192.0.2.11' -PrefixLength 24 -SkipAsSource $true");
        StringAssert.Contains(script!, "-RouteMetric 7 -PolicyStore 'ActiveStore'");
        StringAssert.Contains(script!, "-RouteMetric 19 -PolicyStore 'PersistentStore'");
        StringAssert.Contains(script!, "Disable-NetAdapter -InputObject $currentAdapter -Confirm:$false -ErrorAction Stop");
        StringAssert.Contains(script!, "$currentAdapter.AdminStatus -ne 'Down'");
        StringAssert.Contains(script!, "$restoredAdapter.AdminStatus -ne 'Down'");
        StringAssert.Contains(script!, "InterfaceGuid -ne $expectedGuid");
    }

    [TestMethod]
    public async Task RestartFailureAfterDisableRestoresOriginalEnabledState()
    {
        var enabled = true;
        var failAfterDisableOnce = true;
        var operations = new List<string>();
        var service = Service((script, summary, _, _) =>
        {
            operations.Add(summary);
            if (summary.Contains("read adapter administrative status", StringComparison.Ordinal))
                return Task.FromResult(enabled ? "Up" : "Disabled");
            if (summary.Contains("administrative state disabled", StringComparison.Ordinal))
            {
                enabled = false;
                if (failAfterDisableOnce)
                {
                    failAfterDisableOnce = false;
                    throw new InvalidOperationException("injected failure after disable");
                }
            }
            else if (summary.Contains("administrative state enabled", StringComparison.Ordinal)) enabled = true;
            return Task.FromResult(enabled ? "Up" : "Disabled");
        });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            service.RestartAdapterAsync(Adapter(), allowAnyAdapter: true));

        Assert.IsTrue(enabled, "A failure after Disable must compensate to the prior enabled state.");
        CollectionAssert.AreEqual(
            new[] { "PowerShell read adapter administrative status idx=17", "PowerShell set adapter administrative state disabled idx=17", "PowerShell set adapter administrative state enabled idx=17", "PowerShell read adapter administrative status idx=17" },
            operations);
    }

    [TestMethod]
    public async Task CanceledRestartRestoresOriginallyDisabledState()
    {
        var enabled = false;
        var canceled = false;
        using var operation = new CancellationTokenSource();
        var service = Service((_, summary, _, _) =>
        {
            if (summary.Contains("read adapter administrative status", StringComparison.Ordinal))
                return Task.FromResult(enabled ? "Up" : "Disabled");
            if (summary.Contains("administrative state enabled", StringComparison.Ordinal)) enabled = true;
            else if (summary.Contains("administrative state disabled", StringComparison.Ordinal))
            {
                enabled = false;
                if (!canceled)
                {
                    canceled = true;
                    operation.Cancel();
                }
            }
            return Task.FromResult(enabled ? "Up" : "Disabled");
        }, (delay, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            service.RestartAdapterAsync(Adapter(), allowAnyAdapter: true, operation.Token, expectedEnabledBefore: false));

        Assert.IsFalse(enabled, "Cancellation between disable and enable must not change an originally disabled adapter to enabled.");
    }

    [TestMethod]
    public async Task SuccessfulRestartLeavesOriginallyDisabledAdapterEnabled()
    {
        var enabled = false;
        var scripts = new List<string>();
        var service = Service((script, summary, _, _) =>
        {
            scripts.Add(script);
            if (summary.Contains("read adapter administrative status", StringComparison.Ordinal))
                return Task.FromResult(enabled ? "Up" : "Disabled");
            if (summary.Contains("administrative state enabled", StringComparison.Ordinal)) enabled = true;
            else if (summary.Contains("administrative state disabled", StringComparison.Ordinal)) enabled = false;
            return Task.FromResult(enabled ? "Up" : "Disabled");
        }, (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });

        var result = await service.RestartAdapterAsync(Adapter(), allowAnyAdapter: true, expectedEnabledBefore: false);

        Assert.IsTrue(result.WasDisabledBefore);
        Assert.AreEqual("Up", result.FinalStatus);
        Assert.IsTrue(enabled, "A successful restart intentionally finishes enabled.");
        StringAssert.Contains(scripts[0], "[string]$netAdapter.AdminStatus");
        var disableScript = scripts.Single(x => x.Contains("Disable-NetAdapter", StringComparison.Ordinal));
        var enableScript = scripts.First(x => x.Contains("Enable-NetAdapter", StringComparison.Ordinal));
        StringAssert.Contains(disableScript, "Disable-NetAdapter -InputObject $netAdapter -Confirm:$false -ErrorAction Stop");
        StringAssert.Contains(disableScript, "$finalAdapter.AdminStatus -ne 'Down'");
        StringAssert.Contains(enableScript, "Enable-NetAdapter -InputObject $netAdapter -Confirm:$false -ErrorAction Stop");
        StringAssert.Contains(enableScript, "$finalAdapter.AdminStatus -ne 'Up'");
    }

    [TestMethod]
    public void SnapshotComparerIncludesDnsSourceAdminStateAndAddressFlags()
    {
        var baseline = Snapshot(dhcp: false, dnsMode: AdapterDnsMode.Static, enabled: true);
        baseline.Dns = ["192.0.2.53"];
        baseline.Addresses = [new AdapterIpv4AddressSnapshot { IpAddress = "192.0.2.10", PrefixLength = 24 }];
        var actual = Snapshot(dhcp: false, dnsMode: AdapterDnsMode.Static, enabled: true);
        actual.Dns = ["192.0.2.53"];
        actual.Addresses = [new AdapterIpv4AddressSnapshot { IpAddress = "192.0.2.10", PrefixLength = 24 }];

        Assert.IsTrue(AdapterIpv4SnapshotComparer.Equivalent(baseline, actual));
        actual.DnsMode = AdapterDnsMode.Automatic;
        Assert.IsFalse(AdapterIpv4SnapshotComparer.Equivalent(baseline, actual));
        actual.DnsMode = AdapterDnsMode.Static;
        actual.AdapterEnabled = false;
        Assert.IsFalse(AdapterIpv4SnapshotComparer.Equivalent(baseline, actual));
        actual.AdapterEnabled = true;
        actual.Addresses[0].SkipAsSource = true;
        Assert.IsFalse(AdapterIpv4SnapshotComparer.Equivalent(baseline, actual));
    }

    [TestMethod]
    public async Task PartialStaticIpv4MutationAtEachWriteBoundaryIsRestoredAndVerified()
    {
        string? applyScript = null;
        var original = new AdapterIpv4Snapshot
        {
            DhcpEnabled = true,
            DnsMode = AdapterDnsMode.Static,
            Dns = ["192.0.2.53"],
            AdapterEnabled = true,
            AutomaticMetric = true,
            InterfaceMetric = 25,
            Addresses = [new AdapterIpv4AddressSnapshot { IpAddress = "192.0.2.10", PrefixLength = 24 }],
            Routes = [new AdapterRouteSnapshot { NextHop = "192.0.2.1", RouteMetric = 7, PolicyStore = "ActiveStore", Protocol = "NetMgmt" }]
        };
        var boundaries = new[] { "disable DHCP", "remove addresses", "remove default routes", "create address", "set metric", "set DNS" };

        for (var failureStage = 1; failureStage <= boundaries.Length; failureStage++)
        {
            var stage = failureStage;
            var state = Clone(original);
            var restored = false;
            var service = Service((script, summary, _, _) =>
            {
                if (summary.Contains("apply target adapter static IPv4", StringComparison.Ordinal))
                {
                    applyScript = script;
                    if (stage >= 1) state.DhcpEnabled = false;
                    if (stage >= 2) state.Addresses.Clear();
                    if (stage >= 3) state.Routes.Clear();
                    if (stage >= 4) state.Addresses.Add(new AdapterIpv4AddressSnapshot { IpAddress = "192.0.2.99", PrefixLength = 24 });
                    if (stage >= 5) { state.AutomaticMetric = false; state.InterfaceMetric = NetworkAdapterService.DhcpHostAdapterMetric; }
                    if (stage >= 6) { state.DnsMode = AdapterDnsMode.Automatic; state.Dns.Clear(); }
                    throw new IOException($"injected failure after {boundaries[stage - 1]}");
                }
                if (summary.Contains("restore target adapter original", StringComparison.Ordinal))
                {
                    state = Clone(original);
                    return Task.FromResult("OK");
                }
                if (summary.Contains("capture target adapter IPv4", StringComparison.Ordinal))
                    return Task.FromResult(JsonSerializer.Serialize(state, JsonStore.Options));
                Assert.Fail($"Unexpected PowerShell action: {summary}");
                return Task.FromResult("");
            });

            await Assert.ThrowsExactlyAsync<IOException>(() => service.RunWithIPv4RecoveryAsync(
                Adapter(), original,
                token => service.ApplyStaticIPv4Async(Adapter(), "192.0.2.99", "255.255.255.0", "", "", token),
                "Test adapter operation",
                CancellationToken.None,
                onRestored: () => restored = true));

            Assert.IsTrue(restored, $"stage {failureStage} should verify compensation");
            Assert.IsTrue(AdapterIpv4SnapshotComparer.Equivalent(original, state), $"stage {failureStage} did not restore the saved snapshot");
            StringAssert.Contains(applyScript!, "-Dhcp Disabled -ErrorAction Stop");
            StringAssert.Contains(applyScript!, "Remove-NetIPAddress -Confirm:$false -ErrorAction Stop");
            StringAssert.Contains(applyScript!, "Remove-NetRoute -Confirm:$false -ErrorAction Stop");
            StringAssert.Contains(applyScript!, "New-NetIPAddress -InterfaceIndex $idx");
            StringAssert.Contains(applyScript!, "-AutomaticMetric Disabled -InterfaceMetric 9000 -ErrorAction Stop");
            StringAssert.Contains(applyScript!, "-ResetServerAddresses -ErrorAction Stop");
        }
    }

    [TestMethod]
    public async Task FailedCompensationIsReportedAndDoesNotClaimRestored()
    {
        var original = Snapshot(dhcp: true, dnsMode: AdapterDnsMode.Automatic, enabled: true);
        var compensationAttempted = false;
        var service = Service((_, summary, _, _) =>
        {
            if (summary.Contains("apply target adapter static IPv4", StringComparison.Ordinal)) throw new IOException("primary failure");
            if (summary.Contains("restore target adapter original", StringComparison.Ordinal))
            {
                compensationAttempted = true;
                throw new IOException("compensation failure");
            }
            return Task.FromResult("");
        });
        var restored = false;

        var error = await Assert.ThrowsExactlyAsync<AggregateException>(() => service.RunWithIPv4RecoveryAsync(
            Adapter(), original,
            token => service.ApplyStaticIPv4Async(Adapter(), "192.0.2.99", "255.255.255.0", "", "", token),
            "Test adapter operation",
            CancellationToken.None,
            onRestored: () => restored = true));

        Assert.IsTrue(compensationAttempted);
        Assert.IsFalse(restored);
        Assert.AreEqual(2, error.InnerExceptions.Count);
    }

    [TestMethod]
    public async Task CancellationUsesIndependentTokenToRestoreOriginalSnapshot()
    {
        var original = Snapshot(dhcp: true, dnsMode: AdapterDnsMode.Automatic, enabled: true);
        var state = Clone(original);
        using var operation = new CancellationTokenSource();
        var service = Service((_, summary, _, _) =>
        {
            if (summary.Contains("apply target adapter static IPv4", StringComparison.Ordinal))
            {
                state.DhcpEnabled = false;
                operation.Cancel();
                throw new OperationCanceledException(operation.Token);
            }
            if (summary.Contains("restore target adapter original", StringComparison.Ordinal))
            {
                state = Clone(original);
                return Task.FromResult("OK");
            }
            if (summary.Contains("capture target adapter IPv4", StringComparison.Ordinal))
                return Task.FromResult(JsonSerializer.Serialize(state, JsonStore.Options));
            Assert.Fail($"Unexpected PowerShell action: {summary}");
            return Task.FromResult("");
        });
        var restored = false;

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => service.RunWithIPv4RecoveryAsync(
            Adapter(), original,
            token => service.ApplyStaticIPv4Async(Adapter(), "192.0.2.99", "255.255.255.0", "", "", token),
            "Test adapter operation",
            operation.Token,
            onRestored: () => restored = true));

        Assert.IsTrue(restored);
        Assert.IsTrue(AdapterIpv4SnapshotComparer.Equivalent(original, state));
    }

    [TestMethod]
    public async Task DhcpFirewallRulesAreScopedToTheEscapedSelectedInterfaceAlias()
    {
        var scripts = new List<string>();
        var service = Service((value, _, _, _) =>
        {
            scripts.Add(value);
            var name = System.Text.RegularExpressions.Regex.Match(value, @"\$name='(?<name>[^']+)'").Groups["name"].Value;
            return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new { Name = name, InstanceId = $"instance-{scripts.Count}" }));
        });

        var lease = await service.EnsureDhcpFirewallRulesAsync("Lab ' Ethernet");

        Assert.AreEqual(2, scripts.Count);
        Assert.AreEqual(2, lease.Rules.Count);
        Assert.IsTrue(lease.Rules.All(x => x.OwnershipVerified));
        Assert.IsTrue(scripts.All(x => x.Contains("$interface='Lab '' Ethernet'", StringComparison.Ordinal)));
        Assert.IsTrue(scripts.All(x => x.Contains("InterfaceAlias=$interface", StringComparison.Ordinal)));
        Assert.IsTrue(scripts.All(x => !x.Contains("Get-NetFirewallRule -DisplayName", StringComparison.Ordinal)));
        Assert.AreEqual(2, lease.Rules.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static AdapterIpv4Snapshot Clone(AdapterIpv4Snapshot snapshot) =>
        JsonSerializer.Deserialize<AdapterIpv4Snapshot>(JsonSerializer.Serialize(snapshot, JsonStore.Options), JsonStore.Options)!;

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static AdapterIpv4Snapshot Snapshot(bool dhcp, AdapterDnsMode dnsMode, bool? enabled) => new()
    {
        DhcpEnabled = dhcp,
        DnsMode = dnsMode,
        AdapterEnabled = enabled,
        AutomaticMetric = true,
        InterfaceMetric = 10
    };

    private static NetworkAdapterInfo Adapter() => new()
    {
        Id = "11111111-2222-3333-4444-555555555555",
        InterfaceIndex = "17",
        Name = "Ethernet",
        Description = "Test Ethernet",
        Status = "Up"
    };

    private static string ReadRequestedMac(string script) =>
        Regex.Match(script, @"\$mac='(?<mac>[^']+)'", RegexOptions.CultureInvariant).Groups["mac"].Value;

    private static NetworkAdapterService Service(
        PowerShellScriptExecutor executor,
        Func<TimeSpan, CancellationToken, Task>? delay = null) =>
        new(new NullLogger(), executor, delay);

    private sealed class NullLogger : ILogger
    {
        public event Action<string>? LineWritten { add { } remove { } }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
