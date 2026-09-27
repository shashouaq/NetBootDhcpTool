namespace NetBootDhcpTool.Core;

public sealed record UpdateControllerState
{
    public long Revision { get; init; }
    public bool CheckStarted { get; init; }
    public bool IsChecking { get; init; }
    public bool CheckCanceled { get; init; }
    public UpdateCheckResult? CheckResult { get; init; }
    public IReadOnlyList<UpdateSourceSpeed> SourceSpeeds { get; init; } = [];
    public bool DownloadInProgress { get; init; }
    public bool DownloadCancellationRequested { get; init; }
    public bool DownloadCanceled { get; init; }
    public UpdateDownloadProgress? DownloadProgress { get; init; }
    public bool LowSpeedWarningRaised { get; init; }
    public bool PackageVerificationInProgress { get; init; }
    public UpdateDownloadResult? DownloadedResult { get; init; }
    public string? DownloadError { get; init; }
}

/// <summary>Owns update check/download execution, cancellation, task tracking and service disposal.</summary>
public sealed class UpdateController : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly VersionUpdateService _service;
    private readonly UpdatePackageApplier _packageApplier;
    private readonly Version _currentVersion;
    private readonly UpdateInstallContext? _installContext;
    private readonly TaskCompletionSource _disposeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _checkCancellation;
    private CancellationTokenSource? _downloadCancellation;
    private Task<UpdateCheckResult?>? _checkTask;
    private Task<UpdateDownloadResult>? _downloadTask;
    private UpdateControllerState _state = new();
    private bool _disposed;
    private bool _disposeStarted;

    public UpdateController(VersionUpdateService service, Version currentVersion, UpdateInstallContext? installContext = null)
        : this(service, currentVersion, installContext, new UpdatePackageApplier())
    {
    }

    internal UpdateController(VersionUpdateService service, Version currentVersion, UpdateInstallContext? installContext, UpdatePackageApplier packageApplier)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _currentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        _installContext = installContext;
        _packageApplier = packageApplier ?? throw new ArgumentNullException(nameof(packageApplier));
    }

    public UpdateInstallContext? InstallContext => _installContext;

    public event Action<UpdateControllerState>? StateChanged;

    public UpdateControllerState State { get { lock (_sync) return _state; } }

    public Task<UpdateCheckResult?> CheckAsync()
    {
        TaskCompletionSource<UpdateCheckResult?> completion;
        CancellationTokenSource cancellation;
        UpdateControllerState published;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_checkTask != null) return _checkTask;
            completion = new TaskCompletionSource<UpdateCheckResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellation = _checkCancellation = new CancellationTokenSource();
            _checkTask = completion.Task;
            published = Update(_state with
            {
                CheckStarted = true,
                IsChecking = true,
                CheckCanceled = false,
                CheckResult = null,
                SourceSpeeds = [],
                DownloadError = null
            });
        }
        Publish(published);
        _ = RunCheckAsync(cancellation, completion);
        return completion.Task;
    }

    public Task<UpdateDownloadResult> DownloadAsync(UpdateCheckResult result, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        TaskCompletionSource<UpdateDownloadResult> completion;
        CancellationTokenSource cancellation;
        CancellationTokenSource? oldCancellation = null;
        UpdateControllerState published;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_downloadTask is { IsCompleted: false })
                throw new InvalidOperationException("An update download is already in progress.");
            oldCancellation = _downloadCancellation;
            completion = new TaskCompletionSource<UpdateDownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellation = _downloadCancellation = new CancellationTokenSource();
            _downloadTask = completion.Task;
            published = Update(_state with
            {
                DownloadInProgress = true,
                DownloadCancellationRequested = false,
                DownloadCanceled = false,
                DownloadProgress = null,
                PackageVerificationInProgress = false,
                LowSpeedWarningRaised = false,
                DownloadedResult = null,
                DownloadError = null
            });
        }
        oldCancellation?.Dispose();
        Publish(published);
        _ = RunDownloadAsync(result, destinationPath, cancellation, completion);
        return completion.Task;
    }

    public void CancelDownload()
    {
        CancellationTokenSource? cancellation;
        UpdateControllerState? published = null;
        lock (_sync)
        {
            if (_disposed || !_state.DownloadInProgress || _state.DownloadCancellationRequested) return;
            cancellation = _downloadCancellation;
            if (cancellation == null) return;
            published = Update(_state with { DownloadCancellationRequested = true });
        }
        Publish(published);
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public ValueTask DisposeAsync()
    {
        CancellationTokenSource? checkCancellation = null;
        CancellationTokenSource? downloadCancellation = null;
        Task<UpdateCheckResult?>? checkTask = null;
        Task<UpdateDownloadResult>? downloadTask = null;
        lock (_sync)
        {
            if (_disposeStarted) return new ValueTask(_disposeCompletion.Task);
            _disposeStarted = true;
            _disposed = true;
            checkCancellation = _checkCancellation;
            downloadCancellation = _downloadCancellation;
            checkTask = _checkTask;
            downloadTask = _downloadTask;
        }
        if (checkCancellation != null) TryCancel(checkCancellation);
        if (downloadCancellation != null) TryCancel(downloadCancellation);
        _ = DisposeResourcesAsync(checkCancellation, downloadCancellation, checkTask, downloadTask);
        return new ValueTask(_disposeCompletion.Task);
    }

    private async Task RunCheckAsync(CancellationTokenSource cancellation, TaskCompletionSource<UpdateCheckResult?> completion)
    {
        try
        {
            var progress = new CallbackProgress<UpdateSourceSpeed>(source => Mutate(state => state with
            {
                SourceSpeeds = state.SourceSpeeds
                    .Where(existing => !existing.Url.Equals(source.Url, StringComparison.OrdinalIgnoreCase))
                    .Append(source)
                    .ToArray()
            }));
            var result = await _service.CheckAsync(_currentVersion, cancellation.Token, progress).ConfigureAwait(false);
            if (result is { Succeeded: true, IsNewVersion: true })
            {
                result = CopyWithSelectedPackage(result, UpdatePackageSelector.Select(result, _installContext));
            }
            Mutate(state => state with { IsChecking = false, CheckResult = result, SourceSpeeds = result.DownloadSpeeds });
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Mutate(state => state with { IsChecking = false, CheckCanceled = true });
            completion.TrySetCanceled(cancellation.Token);
        }
        catch (Exception ex)
        {
            Mutate(state => state with
            {
                IsChecking = false,
                CheckResult = new UpdateCheckResult { CurrentVersion = _currentVersion, Succeeded = false, Error = ex.Message }
            });
            completion.TrySetException(ex);
        }
    }

    private async Task RunDownloadAsync(
        UpdateCheckResult result,
        string destinationPath,
        CancellationTokenSource cancellation,
        TaskCompletionSource<UpdateDownloadResult> completion)
    {
        try
        {
            var progress = new CallbackProgress<UpdateDownloadProgress>(item => Mutate(state => state with
            {
                DownloadProgress = item,
                LowSpeedWarningRaised = state.LowSpeedWarningRaised || item.LowSpeedDuration >= TimeSpan.FromSeconds(10)
            }));
            var downloaded = result.SelectedPackage is { } package
                ? await _service.DownloadPackageAsync(result, package, destinationPath, progress, cancellation.Token).ConfigureAwait(false)
                : await _service.DownloadAsync(result, destinationPath, progress, cancellation.Token).ConfigureAwait(false);
            if (downloaded.ReadyToInstall)
            {
                if (_installContext is null || result.LatestVersion is null)
                    throw new InvalidDataException("The signed update package cannot be applied without a verified installation baseline.");
                Mutate(state => state with { PackageVerificationInProgress = true, DownloadProgress = null });
                try
                {
                    await VerifyPackageForInstallAsync(_packageApplier, downloaded, _installContext, result.LatestVersion, cancellation.Token).ConfigureAwait(false);
                }
                catch (UpdateBaseInventoryMismatchException) when (downloaded.Package!.Kind.Equals("Ota", StringComparison.OrdinalIgnoreCase))
                {
                    var fullPackage = result.Packages.FirstOrDefault(item => item.Kind.Equals("Full", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("The installed files do not match the OTA base, and no Full package is available.");
                    var fullDestination = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!, fullPackage.FileName);
                    Mutate(state => state with { PackageVerificationInProgress = false, DownloadProgress = null });
                    downloaded = await _service.DownloadPackageAsync(result, fullPackage, fullDestination, progress, cancellation.Token).ConfigureAwait(false);
                    Mutate(state => state with { PackageVerificationInProgress = true, DownloadProgress = null });
                    await VerifyPackageForInstallAsync(_packageApplier, downloaded, _installContext, result.LatestVersion, cancellation.Token).ConfigureAwait(false);
                    downloaded = new UpdateDownloadResult
                    {
                        FilePath = downloaded.FilePath,
                        Sha256 = downloaded.Sha256,
                        DownloadUrl = downloaded.DownloadUrl,
                        ReadyToInstall = downloaded.ReadyToInstall,
                        Package = downloaded.Package,
                        FellBackToFullPackage = true,
                        SignedManifestJson = downloaded.SignedManifestJson,
                        ManifestSignature = downloaded.ManifestSignature
                    };
                }
            }
            Mutate(state => state with { DownloadInProgress = false, PackageVerificationInProgress = false, DownloadedResult = downloaded, DownloadError = null });
            completion.TrySetResult(downloaded);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Mutate(state => state with { DownloadInProgress = false, DownloadCancellationRequested = false, DownloadCanceled = true, PackageVerificationInProgress = false });
            completion.TrySetCanceled(cancellation.Token);
        }
        catch (Exception ex)
        {
            Mutate(state => state with { DownloadInProgress = false, PackageVerificationInProgress = false, DownloadError = ex.Message });
            completion.TrySetException(ex);
        }
    }

    private static async Task VerifyPackageForInstallAsync(
        UpdatePackageApplier applier,
        UpdateDownloadResult downloaded,
        UpdateInstallContext install,
        Version targetVersion,
        CancellationToken cancellationToken)
    {
        var preflight = new UpdateApplyRequest(
            install.RootDirectory,
            downloaded.FilePath,
            downloaded.Sha256,
            install.Manifest.Version,
            targetVersion.ToString(3),
            downloaded.Package!.Kind,
            install.ManifestSha256,
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(downloaded.SignedManifestJson)),
            downloaded.ManifestSignature,
            Guid.NewGuid().ToString("N"));
        await applier.ApplyAsync(preflight, cancellationToken, validateOnly: true).ConfigureAwait(false);
    }

    private void Mutate(Func<UpdateControllerState, UpdateControllerState> change)
    {
        UpdateControllerState? published;
        lock (_sync)
        {
            if (_disposed) return;
            published = Update(change(_state));
        }
        Publish(published);
    }

    private static UpdateCheckResult CopyWithSelectedPackage(UpdateCheckResult source, UpdatePackageMetadata? selectedPackage) => new()
    {
        CurrentVersion = source.CurrentVersion,
        LatestVersion = source.LatestVersion,
        Succeeded = source.Succeeded,
        IsNewVersion = source.IsNewVersion,
        DownloadUrl = source.DownloadUrl,
        DownloadUrls = source.DownloadUrls,
        ReleasePageUrl = source.ReleasePageUrl,
        ArchiveName = source.ArchiveName,
        ArchiveSha256 = source.ArchiveSha256,
        DownloadSpeeds = source.DownloadSpeeds,
        ReleaseNotes = source.ReleaseNotes,
        Changes = source.Changes,
        SignatureVerified = source.SignatureVerified,
        SignedManifestJson = source.SignedManifestJson,
        ManifestSignature = source.ManifestSignature,
        Packages = source.Packages,
        SelectedPackage = selectedPackage,
        Error = source.Error
    };

    private UpdateControllerState Update(UpdateControllerState state)
    {
        _state = state with { Revision = _state.Revision + 1 };
        return _state;
    }

    private void Publish(UpdateControllerState? state)
    {
        if (state == null) return;
        var handlers = StateChanged;
        if (handlers == null) return;
        foreach (Action<UpdateControllerState> handler in handlers.GetInvocationList())
        {
            try { handler(state); }
            catch { /* UI observers must not break update execution. */ }
        }
    }

    private async Task DisposeResourcesAsync(
        CancellationTokenSource? checkCancellation,
        CancellationTokenSource? downloadCancellation,
        Task<UpdateCheckResult?>? checkTask,
        Task<UpdateDownloadResult>? downloadTask)
    {
        try
        {
            if (checkTask != null) await ObserveAsync(checkTask).ConfigureAwait(false);
            if (downloadTask != null) await ObserveAsync(downloadTask).ConfigureAwait(false);
            _service.Dispose();
            checkCancellation?.Dispose();
            downloadCancellation?.Dispose();
            _disposeCompletion.TrySetResult();
        }
        catch (Exception ex)
        {
            _disposeCompletion.TrySetException(ex);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch { /* The initiating caller receives the operation error; disposal only observes it. */ }
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
