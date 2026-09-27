using System.Diagnostics;
using System.Collections;
using System.Net;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NetBootDhcpTool.Core;
using NetBootDhcpTool.Network;

namespace NetBootDhcpTool.UnitTests;

[TestClass]
public sealed class PerformanceBaselineTests
{
    private const int SamplesPerFixture = 5;
    private const int LogLinesPerSample = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [TestMethod]
    [TestCategory("Performance")]
    public async Task MeasuresControlledScanAndSessionLogFixtures()
    {
        var path = Environment.GetEnvironmentVariable("NETBOOT_PERFORMANCE_REPORT_PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            Assert.Inconclusive("Run the Performance category through build/measure-performance.ps1.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var scan254 = await MeasureScanAsync(254);
        var scan4096 = await MeasureScanAsync(4096);
        var httpProbe64 = await MeasureHttpProbeAsync();
        var sessionLog = MeasureSessionLogWrites();
        var scanHistory = await MeasureScanHistoryPersistenceAsync();
        var leaseProbe = await MeasureLeaseProbeSchedulingAsync();
        var adapterRefresh = await MeasureAdapterRefreshCoalescingAsync();
        var adapterRefreshNoChange = MeasureAdapterRefreshNoChangeComparison();
        var adapterReadComparison = MeasureAdapterReadComparison();
        var routePlanningComparison = await MeasureRoutePlanningReadComparisonAsync();
        var powerShellRunner = await MeasurePowerShellProcessRunnerOutcomesAsync();
        var process = Process.GetCurrentProcess();
        var report = new PerformanceReport
        {
            SchemaVersion = 1,
            StartedAtUtc = startedAt,
            Commit = ReadGitCommit(),
            WorkingTreeDirty = ReadGitStatus().Length > 0,
            ApplicationVersion = ReadAppSetting("Version"),
            DotnetRuntime = RuntimeInformation.FrameworkDescription,
            DotnetSdk = ReadSdkVersion(),
            OperatingSystem = Environment.OSVersion.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            Machine = Environment.MachineName,
            Scan254 = scan254,
            Scan4096 = scan4096,
            HttpProbe64 = httpProbe64,
            SessionLog10K = sessionLog,
            ScanHistory4096 = scanHistory,
            LeaseProbe128 = leaseProbe,
            AdapterRefreshBurst100 = adapterRefresh,
            AdapterRefreshNoChange = adapterRefreshNoChange,
            AdapterReadFullListReference = adapterReadComparison.FullList,
            AdapterReadSelectedIdentity = adapterReadComparison.SelectedIdentity,
            RoutePlanningFourReadBaseline = routePlanningComparison.FourReadBaseline,
            RoutePlanningSingleSnapshot = routePlanningComparison.SingleSnapshot,
            PowerShellRunnerSuccess = powerShellRunner.Success,
            PowerShellRunnerFailure = powerShellRunner.Failure,
            PowerShellRunnerCancellation = powerShellRunner.Cancellation,
            PowerShellRunnerTimeout = powerShellRunner.Timeout,
            PendingFixtures = []
        };

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path!))!);
        await File.WriteAllTextAsync(path!, JsonSerializer.Serialize(report, JsonOptions));
        Console.WriteLine($"PERFORMANCE_FIXTURE_OK commit={report.Commit} runtime={report.DotnetRuntime} scan254MedianMs={scan254.MedianElapsedMs:0.0} scan4096MedianMs={scan4096.MedianElapsedMs:0.0} log10KMedianMs={sessionLog.MedianElapsedMs:0.0} adapterNoChangeAllocatedBytesPer100K={adapterRefreshNoChange.Samples[0].AllocatedBytes} routePlanMs={routePlanningComparison.FourReadBaseline.MedianElapsedMs:0.0}->{routePlanningComparison.SingleSnapshot.MedianElapsedMs:0.0} routeCalls=4->1 powershellRunnerMs=success:{powerShellRunner.Success.MedianElapsedMs:0.0},failure:{powerShellRunner.Failure.MedianElapsedMs:0.0},cancel:{powerShellRunner.Cancellation.MedianElapsedMs:0.0},timeout:{powerShellRunner.Timeout.MedianElapsedMs:0.0} report={path}");
    }

    private static async Task<(Measurement Success, Measurement Failure, Measurement Cancellation, Measurement Timeout)>
        MeasurePowerShellProcessRunnerOutcomesAsync()
    {
        return (
            await MeasurePowerShellRunnerOutcomeAsync("success", "'performance-runner-success'"),
            await MeasurePowerShellRunnerOutcomeAsync("failure", "throw 'intentional performance runner failure'"),
            await MeasurePowerShellRunnerOutcomeAsync("cancellation", "Start-Sleep -Seconds 30"),
            await MeasurePowerShellRunnerOutcomeAsync("timeout", "Start-Sleep -Seconds 30"));
    }

    private static async Task<Measurement> MeasurePowerShellRunnerOutcomeAsync(string outcome, string script)
    {
        var samples = new List<MeasurementSample>(SamplesPerFixture);
        var summary = $"performance runner {outcome}";
        var timeout = outcome == "timeout" ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(5);
        var expectedLogText = outcome switch
        {
            "success" => "completed:",
            "failure" => "failed:",
            "cancellation" => "canceled:",
            "timeout" => "timed out:",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };

        for (var sample = 0; sample < SamplesPerFixture; sample++)
        {
            var logger = new OutcomePerformanceLogger();
            var processStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new PowerShellProcessRunner(logger, timeout: timeout,
                processStarted: processId => processStarted.TrySetResult(processId));
            using var cancellation = new CancellationTokenSource();
            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            var operation = runner.RunAsync(script, summary, cancellation.Token, logOutput: false);
            var processId = await processStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            long outputBytes = 0;

            switch (outcome)
            {
                case "success":
                    var output = await operation;
                    Assert.AreEqual("performance-runner-success", output);
                    outputBytes = Encoding.UTF8.GetByteCount(output);
                    break;
                case "failure":
                    var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => operation);
                    StringAssert.Contains(failure.Message, "intentional performance runner failure");
                    outputBytes = Encoding.UTF8.GetByteCount(failure.Message);
                    break;
                case "cancellation":
                    cancellation.Cancel();
                    await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => operation);
                    break;
                case "timeout":
                    await Assert.ThrowsExactlyAsync<TimeoutException>(() => operation);
                    break;
            }

            timer.Stop();
            var processExited = HasExited(processId);
            Assert.IsTrue(processExited, $"The PowerShell child for the {outcome} measurement remained alive.");
            Assert.IsTrue(logger.Messages.Any(message => message.Contains(expectedLogText, StringComparison.Ordinal)
                && message.Contains("elapsedMs=", StringComparison.Ordinal)),
                $"The {outcome} outcome did not record its elapsed duration.");

            samples.Add(new MeasurementSample(timer.Elapsed.TotalMilliseconds,
                (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(false) - allocatedBefore, GC.GetTotalMemory(false), outputBytes, 1, 1,
                Counters: new Dictionary<string, long>
                {
                    ["PowerShellProcessesStarted"] = 1,
                    ["ExpectedOutcomeObserved"] = 1,
                    ["ElapsedOutcomeLogObserved"] = 1,
                    ["ProcessExited"] = 1
                }));
        }

        return Measurement.Create($"powershell-runner-{outcome}", SamplesPerFixture, samples);
    }

    private static bool HasExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static async Task<Measurement> MeasureScanAsync(int targetCount)
    {
        var targets = CreateTargets(targetCount);
        var samples = new List<MeasurementSample>(SamplesPerFixture);
        for (var sample = 0; sample < SamplesPerFixture; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var probe = new ControlledScanProbe();
            var timer = Stopwatch.StartNew();
            var scanner = new PingScanner(new PerformanceLogger(), probe);
            var progress = new Progress<(int done, int total, ScanResult? result)>(_ => { });
            var results = await scanner.ScanTargetsAsync(targets, PingScanner.MaxConcurrency, 1, 1, progress, CancellationToken.None);
            timer.Stop();
            Assert.AreEqual(targetCount, probe.CompletedCount);
            Assert.AreEqual((targetCount + 7) / 8, results.Count);
            samples.Add(new MeasurementSample(timer.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(false) - allocatedBefore, GC.GetTotalMemory(false), 0, probe.MaximumConcurrency, results.Count));
        }

        return Measurement.Create($"scan-targets-{targetCount}", targetCount, samples);
    }

    private static Measurement MeasureSessionLogWrites()
    {
        var samples = new List<MeasurementSample>(SamplesPerFixture);
        var root = Path.Combine(Path.GetTempPath(), "netboot-performance-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            for (var sample = 0; sample < SamplesPerFixture; sample++)
            {
                var path = Path.Combine(root, $"sample-{sample}.log");
                var appPaths = new AppPaths(root);
                var logger = new FileLogger(appPaths);
                var process = Process.GetCurrentProcess();
                var cpuBefore = process.TotalProcessorTime;
                var allocatedBefore = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                for (var line = 0; line < LogLinesPerSample; line++)
                    logger.Info($"Performance fixture sample={sample} line={line}");
                var measuredPath = logger.SessionLogPath;
                logger.Dispose(); // Flush the asynchronous writer before validating persisted output.
                timer.Stop();
                var lineCount = File.ReadLines(measuredPath).Count();
                Assert.AreEqual(LogLinesPerSample, lineCount);
                samples.Add(new MeasurementSample(timer.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                    GC.GetTotalAllocatedBytes(false) - allocatedBefore, GC.GetTotalMemory(false), new FileInfo(measuredPath).Length, 1, lineCount,
                    Counters: new Dictionary<string, long>
                    {
                        ["SessionFileOpens"] = 1,
                        ["RowsWritten"] = LogLinesPerSample
                    }));
                File.Move(measuredPath, path);
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        return Measurement.Create("session-log-10000-lines", LogLinesPerSample, samples);
    }

    private static async Task<Measurement> MeasureScanHistoryPersistenceAsync()
    {
        const int hitCount = 4096;
        const int batchSize = 128;
        const int historyCapacity = 500;
        var samples = new List<MeasurementSample>(SamplesPerFixture);
        var root = Path.Combine(Path.GetTempPath(), "netboot-performance-history-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            for (var sample = 0; sample < SamplesPerFixture; sample++)
            {
                var path = Path.Combine(root, $"sample-{sample}.json");
                var saveCount = 0;
                var recent = new List<OperationHistoryItem>(historyCapacity);
                await using var writer = new CoalescingSnapshotWriter<OperationHistoryItem>(snapshot =>
                {
                    JsonStore.Save(path, snapshot.ToList());
                    Interlocked.Increment(ref saveCount);
                    return Task.CompletedTask;
                });

                var process = Process.GetCurrentProcess();
                var cpuBefore = process.TotalProcessorTime;
                var allocatedBefore = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                for (var first = 1; first <= hitCount; first += batchSize)
                {
                    var end = Math.Min(first + batchSize - 1, hitCount);
                    for (var hit = first; hit <= end; hit++)
                    {
                        recent.Add(new OperationHistoryItem
                        {
                            Id = $"sample-{sample}-hit-{hit}",
                            Type = "Scan",
                            Status = "Online",
                            IpAddress = $"192.0.2.{((hit - 1) % 254) + 1}"
                        });
                        if (recent.Count > historyCapacity) recent.RemoveAt(0);
                    }
                    await writer.SaveAsync(recent);
                }
                timer.Stop();
                var final = JsonStore.Load<List<OperationHistoryItem>>(path);
                Assert.IsTrue(final.HasData);
                Assert.AreEqual(historyCapacity, final.Value!.Count);
                Assert.AreEqual($"sample-{sample}-hit-{hitCount}", final.Value[^1].Id);
                Assert.AreEqual((hitCount + batchSize - 1) / batchSize, saveCount);
                samples.Add(new MeasurementSample(timer.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                    GC.GetTotalAllocatedBytes(false) - allocatedBefore, GC.GetTotalMemory(false), new FileInfo(path).Length,
                    1, final.Value.Count, Counters: new Dictionary<string, long>
                    {
                        ["PersistedSnapshots"] = saveCount,
                        ["RetainedRows"] = final.Value.Count,
                        ["BatchSize"] = batchSize
                    }));
            }
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        return Measurement.Create("scan-history-4096-hits-batch-128-json-store", hitCount, samples);
    }

    private static async Task<Measurement> MeasureLeaseProbeSchedulingAsync()
    {
        const int bindingCount = 128;
        var samples = new List<MeasurementSample>(SamplesPerFixture);
        for (var sample = 0; sample < SamplesPerFixture; sample++)
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            var applied = 0;
            var active = 0;
            var maximumActive = 0;
            await using var coordinator = new LeaseProbeCoordinator(async (request, token) =>
            {
                Interlocked.Increment(ref started);
                var nowActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, nowActive);
                try
                {
                    await release.Task.WaitAsync(token);
                    return new LeaseProbeResult(request.Identity, 1, true, true);
                }
                finally { Interlocked.Decrement(ref active); }
            }, (_, _) =>
            {
                Interlocked.Increment(ref applied);
                return Task.CompletedTask;
            });

            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            var initial = Enumerable.Range(1, bindingCount).Select(index =>
            {
                var identity = new LeaseProbeIdentity($"perf-{sample}", $"client-{index}", $"192.0.2.{index}", 1);
                return coordinator.Schedule(new LeaseProbeRequest(identity, IncludeWeb: true));
            }).ToArray();
            Assert.AreEqual(72, initial.Count(result => result == LeaseProbeScheduleResult.StartedOrQueued));
            Assert.AreEqual(56, initial.Count(result => result == LeaseProbeScheduleResult.QueueFull));

            release.TrySetResult();
            await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var deferred = Enumerable.Range(73, 56).Select(index =>
            {
                var identity = new LeaseProbeIdentity($"perf-{sample}", $"client-{index}", $"192.0.2.{index}", 1);
                return coordinator.Schedule(new LeaseProbeRequest(identity, IncludeWeb: true));
            }).ToArray();
            await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
            timer.Stop();
            Assert.AreEqual(56, deferred.Count(result => result == LeaseProbeScheduleResult.StartedOrQueued));
            Assert.AreEqual(bindingCount, applied);
            Assert.AreEqual(bindingCount, started);
            Assert.IsTrue(maximumActive <= LeaseProbeCoordinator.DefaultMaximumConcurrency);
            samples.Add(new MeasurementSample(timer.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(false) - allocatedBefore, GC.GetTotalMemory(false), 0, maximumActive, applied,
                Counters: new Dictionary<string, long>
                {
                    ["ScheduledRequests"] = bindingCount + 56,
                    ["ProbeExecutions"] = started,
                    ["QueueFullRequestsDeferred"] = 56,
                    ["MaximumQueued"] = LeaseProbeCoordinator.DefaultMaximumQueued
                }));
        }

        return Measurement.Create("lease-probe-128-bindings-concurrency-8-queue-64", bindingCount, samples);
    }

    private static async Task<Measurement> MeasureAdapterRefreshCoalescingAsync()
    {
        const int eventCount = 100;
        var samples = new List<MeasurementSample>(SamplesPerFixture);
        for (var sample = 0; sample < SamplesPerFixture; sample++)
        {
            var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var reads = 0;
            var applied = 0;
            var maximumActive = 0;
            var active = 0;
            await using var coordinator = new CoalescedRefreshCoordinator<int, RefreshValue>(async (request, _) =>
            {
                var nowActive = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, nowActive);
                Interlocked.Increment(ref reads);
                try
                {
                    if (request == -1)
                    {
                        firstStarted.TrySetResult();
                        await releaseFirst.Task;
                    }
                    return new RefreshValue(request);
                }
                finally { Interlocked.Decrement(ref active); }
            }, (_, _) =>
            {
                Interlocked.Increment(ref applied);
                return Task.CompletedTask;
            });

            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            coordinator.Request(-1);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            for (var request = 0; request < eventCount; request++) coordinator.Request(request);
            releaseFirst.TrySetResult();
            await coordinator.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(3));
            timer.Stop();
            Assert.AreEqual(2, reads, "A burst during a read must produce just one latest follow-up read.");
            Assert.AreEqual(2, applied);
            Assert.AreEqual(1, maximumActive);
            samples.Add(new MeasurementSample(timer.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(false) - allocatedBefore, GC.GetTotalMemory(false), 0, maximumActive, applied,
                Counters: new Dictionary<string, long>
                {
                    ["RefreshEvents"] = eventCount + 1,
                    ["ActualReads"] = reads,
                    ["CoalescedEvents"] = eventCount - 1
                }));
        }

        return Measurement.Create("adapter-refresh-burst-100-one-follow-up", eventCount, samples);
    }

    private static Measurement MeasureAdapterRefreshNoChangeComparison()
    {
        const int noChangeSnapshots = 100_000;
        var current = new NetworkAdapterInfo
        {
            Id = "performance-adapter", Name = "Performance adapter", InterfaceIndex = "17",
            Status = "Up", IPv4Address = "192.0.2.17", SubnetMask = "255.255.255.0",
            Gateway = "192.0.2.1", Dns = "192.0.2.53", MacAddress = "02-00-00-00-00-17", LinkSpeedMbps = 1000
        };
        var sameSnapshot = new NetworkAdapterInfo
        {
            Id = current.Id, Name = current.Name, InterfaceIndex = current.InterfaceIndex,
            Status = current.Status, IPv4Address = current.IPv4Address, SubnetMask = current.SubnetMask,
            Gateway = current.Gateway, Dns = current.Dns, MacAddress = current.MacAddress, LinkSpeedMbps = current.LinkSpeedMbps
        };
        var samples = new List<MeasurementSample>(SamplesPerFixture);
        for (var sample = 0; sample < SamplesPerFixture; sample++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            for (var comparison = 0; comparison < noChangeSnapshots; comparison++)
            {
                if (AdapterStatusComparer.HasChanges(current, sameSnapshot))
                    throw new InvalidOperationException("The idle adapter snapshot unexpectedly reported a displayed-field change.");
            }
            timer.Stop();
            samples.Add(new MeasurementSample(timer.Elapsed.TotalMilliseconds,
                (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(false) - allocatedBefore, GC.GetTotalMemory(false), 0, 1, noChangeSnapshots,
                Counters: new Dictionary<string, long>
                {
                    ["NoChangeSnapshots"] = noChangeSnapshots,
                    ["UiListRefreshes"] = 0,
                    ["IpHistoryWrites"] = 0
                }));
        }

        return Measurement.Create("adapter-refresh-idle-no-change-comparison-100k", noChangeSnapshots, samples);
    }

    private static (Measurement FullList, Measurement SelectedIdentity) MeasureAdapterReadComparison()
    {
        var service = new NetworkAdapterService(new PerformanceLogger());
        var initialAdapters = service.GetAdapters(logAdapters: false);
        var selected = initialAdapters.FirstOrDefault(adapter => int.TryParse(adapter.InterfaceIndex, out var index) && index > 0);
        Assert.IsNotNull(selected, "The read-only adapter comparison requires one enumerable IPv4 adapter.");
        var fullListSamples = new List<MeasurementSample>(SamplesPerFixture);
        var selectedSamples = new List<MeasurementSample>(SamplesPerFixture);

        for (var sample = 0; sample < SamplesPerFixture; sample++)
        {
            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            var fromFullList = service.GetAdapters(logAdapters: false).FirstOrDefault(adapter =>
                adapter.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase)
                && adapter.InterfaceIndex.Equals(selected.InterfaceIndex, StringComparison.Ordinal));
            timer.Stop();
            Assert.IsNotNull(fromFullList, "The selected adapter must remain enumerable in the full-list reference read.");
            Assert.AreEqual(selected.Id, fromFullList.Id, ignoreCase: true);
            Assert.AreEqual(selected.InterfaceIndex, fromFullList.InterfaceIndex);
            fullListSamples.Add(new MeasurementSample(timer.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(false) - allocatedBefore, GC.GetTotalMemory(false), 0, 1, initialAdapters.Count,
                Counters: new Dictionary<string, long> { ["EnumeratedAdapters"] = initialAdapters.Count, ["MatchedSelection"] = 1 }));

            cpuBefore = process.TotalProcessorTime;
            allocatedBefore = GC.GetTotalAllocatedBytes(true);
            timer.Restart();
            var byIdentity = service.GetAdapterByIdentity(selected.Id, selected.InterfaceIndex);
            timer.Stop();
            Assert.IsNotNull(byIdentity, "The identity-targeted refresh must find the exact selected adapter.");
            Assert.AreEqual(selected.Id, byIdentity.Id, ignoreCase: true);
            Assert.AreEqual(selected.InterfaceIndex, byIdentity.InterfaceIndex);
            selectedSamples.Add(new MeasurementSample(timer.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(false) - allocatedBefore, GC.GetTotalMemory(false), 0, 1, 1,
                Counters: new Dictionary<string, long> { ["ReturnedAdapters"] = 1, ["MatchedSelection"] = 1 }));
        }

        return (
            Measurement.Create("adapter-read-full-list-selected-row-reference", initialAdapters.Count, fullListSamples),
            Measurement.Create("adapter-read-by-stable-id-and-index", 1, selectedSamples));
    }

    private static async Task<(Measurement FourReadBaseline, Measurement SingleSnapshot)> MeasureRoutePlanningReadComparisonAsync()
    {
        var fourReadSamples = new List<MeasurementSample>(SamplesPerFixture);
        var singleSnapshotSamples = new List<MeasurementSample>(SamplesPerFixture);
        var snapshotScript = StaticRouteService.BuildRoutePlanningSnapshotScript();
        Assert.AreEqual(1, Regex.Matches(snapshotScript, @"\bGet-NetIPInterface\b").Count);
        Assert.AreEqual(1, Regex.Matches(snapshotScript, @"\bGet-NetRoute\b").Count);
        Assert.AreEqual(1, Regex.Matches(snapshotScript, @"\bGet-NetIPAddress\b").Count);
        var snapshotRead = typeof(StaticRouteService).GetMethod("GetRoutePlanningSnapshotAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing production route-planning snapshot read.");

        for (var sample = 0; sample < SamplesPerFixture; sample++)
        {
            var service = new StaticRouteService(new PerformanceLogger());
            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            // Recreate the pre-T22 PreparePlanAsync read path using the same production queries.
            var routes = await service.GetCurrentStaticRoutesAsync();
            var metrics = await service.GetInterfaceMetricsAsync();
            var states = await service.GetInterfaceStatesAsync();
            var addresses = await service.GetInterfaceAddressesAsync();
            timer.Stop();
            fourReadSamples.Add(new MeasurementSample(timer.Elapsed.TotalMilliseconds,
                (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(false) - allocatedBefore, GC.GetTotalMemory(false), 0, 1,
                routes.Count + metrics.Count + states.Count + addresses.Count,
                Counters: new Dictionary<string, long>
                {
                    ["PowerShellProcesses"] = 4,
                    ["GetNetIPInterface"] = 3,
                    ["GetNetRoute"] = 1,
                    ["GetNetIPAddress"] = 1,
                    ["RouteRows"] = routes.Count,
                    ["MetricRows"] = metrics.Count,
                    ["StateRows"] = states.Count,
                    ["AddressRows"] = addresses.Count
                }));

            process = Process.GetCurrentProcess();
            cpuBefore = process.TotalProcessorTime;
            allocatedBefore = GC.GetTotalAllocatedBytes(true);
            timer.Restart();
            var snapshotTask = snapshotRead.Invoke(service, [CancellationToken.None]) as Task
                ?? throw new InvalidOperationException("The route-planning snapshot did not return a task.");
            await snapshotTask;
            timer.Stop();
            var snapshot = snapshotTask.GetType().GetProperty("Result")?.GetValue(snapshotTask)
                ?? throw new InvalidOperationException("The route-planning snapshot task returned no result.");
            var snapshotRoutes = CountSnapshotItems(snapshot, "Routes");
            var snapshotMetrics = CountSnapshotItems(snapshot, "InterfaceMetrics");
            var snapshotStates = CountSnapshotItems(snapshot, "InterfaceStates");
            var snapshotAddresses = CountSnapshotItems(snapshot, "InterfaceAddresses");
            singleSnapshotSamples.Add(new MeasurementSample(timer.Elapsed.TotalMilliseconds,
                (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(false) - allocatedBefore, GC.GetTotalMemory(false), 0, 1,
                snapshotRoutes + snapshotMetrics + snapshotStates + snapshotAddresses,
                Counters: new Dictionary<string, long>
                {
                    ["PowerShellProcesses"] = 1,
                    ["GetNetIPInterface"] = 1,
                    ["GetNetRoute"] = 1,
                    ["GetNetIPAddress"] = 1,
                    ["RouteRows"] = snapshotRoutes,
                    ["MetricRows"] = snapshotMetrics,
                    ["StateRows"] = snapshotStates,
                    ["AddressRows"] = snapshotAddresses
                }));
        }

        return (
            Measurement.Create("route-planning-four-production-reads", 4, fourReadSamples),
            Measurement.Create("route-planning-single-production-snapshot", 1, singleSnapshotSamples));
    }

    private static int CountSnapshotItems(object snapshot, string propertyName)
    {
        if (snapshot.GetType().GetProperty(propertyName)?.GetValue(snapshot) is not IEnumerable items)
            throw new InvalidOperationException($"Route-planning snapshot field {propertyName} was not an array.");
        var count = 0;
        foreach (var _ in items) count++;
        return count;
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref maximum)) && Interlocked.CompareExchange(ref maximum, value, current) != current) { }
    }

    private static async Task<Measurement> MeasureHttpProbeAsync()
    {
        const int targetCount = 64;
        var samples = new List<MeasurementSample>(SamplesPerFixture);
        for (var sample = 0; sample < SamplesPerFixture; sample++)
        {
            var createdTransports = 0;
            var metrics = new MeasuringHttpMetrics();
            var service = new HttpProbeService(() =>
            {
                Interlocked.Increment(ref createdTransports);
                return new MeasuringHttpHandler(metrics);
            });
            var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var timer = Stopwatch.StartNew();
            for (var target = 0; target < targetCount; target++)
            {
                var result = await service.ProbeAsync($"192.0.2.{target + 1}", 1000);
                Assert.IsTrue(result.http && result.https);
            }
            timer.Stop();
            await service.DisposeAsync();
            Assert.AreEqual(targetCount * 2, metrics.RequestCount);
            samples.Add(new MeasurementSample(timer.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(false) - allocatedBefore, GC.GetTotalMemory(false), metrics.RequestCount,
                metrics.MaximumConcurrency, metrics.RequestCount, createdTransports));
        }

        return Measurement.Create("http-probe-64-targets", targetCount, samples);
    }

    private static IReadOnlyList<IPAddress> CreateTargets(int count)
    {
        var result = new IPAddress[count];
        var first = BitConverter.ToUInt32(IPAddress.Parse("10.0.0.1").GetAddressBytes().Reverse().ToArray());
        for (var i = 0; i < count; i++)
        {
            var value = BitConverter.GetBytes(first + (uint)i).Reverse().ToArray();
            result[i] = new IPAddress(value);
        }
        return result;
    }

    private static string ReadGitCommit() => ReadGit("rev-parse", "HEAD");
    private static string ReadGitStatus() => ReadGit("status", "--porcelain");

    private static string ReadAppSetting(string settingName)
    {
        var root = FindRepositoryRoot();
        var projectPath = Path.Combine(root, "src", "NetBootDhcpTool.App", "NetBootDhcpTool.App.csproj");
        return XDocument.Load(projectPath).Descendants(settingName).Single().Value;
    }

    private static string ReadSdkVersion()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRepositoryRoot(), "global.json")));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString() ?? "unknown";
    }

    private static string ReadGit(params string[] arguments)
    {
        var root = FindRepositoryRoot();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) throw new InvalidOperationException("Could not start Git for performance metadata.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(5000);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Could not read performance metadata from Git: {error}");
        return output.Trim();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the project Git checkout.");
    }

    private sealed class ControlledScanProbe : IScanTargetProbe
    {
        private int _active;
        private int _maximumConcurrency;
        private int _completed;
        public int CompletedCount => Volatile.Read(ref _completed);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public async Task<ScanResult?> ProbeAsync(IPAddress ip, int pingTimeoutMs, int httpTimeoutMs, CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _maximumConcurrency, active);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
                var index = Interlocked.Increment(ref _completed) - 1;
                return index % 8 == 0
                    ? new ScanResult { IpAddress = ip.ToString(), PingOk = true, LatencyMs = 1 }
                    : null;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static void UpdateMaximum(ref int maximum, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref maximum)) && Interlocked.CompareExchange(ref maximum, value, current) != current) { }
        }
    }

    private sealed class PerformanceLogger : ILogger
    {
        public event Action<string>? LineWritten { add { } remove { } }
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private sealed class OutcomePerformanceLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public event Action<string>? LineWritten;
        public void Info(string message) { Messages.Add(message); LineWritten?.Invoke(message); }
        public void Warn(string message) { Messages.Add(message); LineWritten?.Invoke(message); }
        public void Error(string message, Exception? exception = null) { Messages.Add(message); LineWritten?.Invoke(message); }
    }

    private sealed class MeasuringHttpHandler : HttpMessageHandler
    {
        private readonly MeasuringHttpMetrics _metrics;

        public MeasuringHttpHandler(MeasuringHttpMetrics metrics) => _metrics = metrics;

        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _metrics.RequestStarted();
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK);
            }
            finally
            {
                _metrics.RequestCompleted();
            }
        }
    }

    private sealed class MeasuringHttpMetrics
    {
        private int _active;
        private int _maximumConcurrency;
        private int _requestCount;
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public int RequestCount => Volatile.Read(ref _requestCount);

        public void RequestStarted()
        {
            var active = Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _requestCount);
            int current;
            while (active > (current = Volatile.Read(ref _maximumConcurrency)) && Interlocked.CompareExchange(ref _maximumConcurrency, active, current) != current) { }
        }

        public void RequestCompleted() => Interlocked.Decrement(ref _active);
    }

    private sealed record RefreshValue(int Request);

    private sealed record MeasurementSample(double ElapsedMs, double CpuMs, long AllocatedBytes, long ManagedHeapBytes,
        long OutputBytes, int MaximumConcurrency, int ResultCount, int CreatedTransports = 0,
        IReadOnlyDictionary<string, long>? Counters = null);

    private sealed record Measurement(string Fixture, int TargetCount, IReadOnlyList<MeasurementSample> Samples,
        double MedianElapsedMs, double MaximumElapsedMs)
    {
        public static Measurement Create(string fixture, int targetCount, List<MeasurementSample> samples)
        {
            var ordered = samples.Select(x => x.ElapsedMs).Order().ToArray();
            return new Measurement(fixture, targetCount, samples, ordered[ordered.Length / 2], ordered[^1]);
        }
    }

    private sealed record PerformanceReport
    {
        public int SchemaVersion { get; init; }
        public DateTimeOffset StartedAtUtc { get; init; }
        public string Commit { get; init; } = "";
        public bool WorkingTreeDirty { get; init; }
        public string ApplicationVersion { get; init; } = "";
        public string DotnetRuntime { get; init; } = "";
        public string DotnetSdk { get; init; } = "";
        public string OperatingSystem { get; init; } = "";
        public int ProcessorCount { get; init; }
        public string Machine { get; init; } = "";
        public Measurement Scan254 { get; init; } = null!;
        public Measurement Scan4096 { get; init; } = null!;
        public Measurement HttpProbe64 { get; init; } = null!;
        public Measurement SessionLog10K { get; init; } = null!;
        public Measurement ScanHistory4096 { get; init; } = null!;
        public Measurement LeaseProbe128 { get; init; } = null!;
        public Measurement AdapterRefreshBurst100 { get; init; } = null!;
        public Measurement AdapterRefreshNoChange { get; init; } = null!;
        public Measurement AdapterReadFullListReference { get; init; } = null!;
        public Measurement AdapterReadSelectedIdentity { get; init; } = null!;
        public Measurement RoutePlanningFourReadBaseline { get; init; } = null!;
        public Measurement RoutePlanningSingleSnapshot { get; init; } = null!;
        public Measurement PowerShellRunnerSuccess { get; init; } = null!;
        public Measurement PowerShellRunnerFailure { get; init; } = null!;
        public Measurement PowerShellRunnerCancellation { get; init; } = null!;
        public Measurement PowerShellRunnerTimeout { get; init; } = null!;
        public IReadOnlyList<string> PendingFixtures { get; init; } = [];
    }
}
