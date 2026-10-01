# Release Process

This project uses a single repeatable release path for local packaging, GitHub publication, and Gitee distribution.

For day-to-day maintenance, required change-log practice, GitHub synchronization, and upgrade work, start with `docs\MAINTENANCE_GUIDE.md`.

## Version Source

- The application version is defined in `src/NetBootDhcpTool.App/NetBootDhcpTool.App.csproj`.
- `build/publish.ps1` reads that version and must not use a separate hard-coded version.
- Release tags must use `v<version>`, for example `v1.0.12`.
- RCs use explicit `v<version>-rc.<N>` tags and remain prereleases. Run `release-candidate.yml` from main only after that exact commit passed Windows CI and the local T19 process/lifecycle gates. The RC job repeats the official NuGet audit, uses the protected signing secret once, and reads back each asset. It never calls the stable promotion path; `-ReleaseCandidate` rejects stable tags and formal mode rejects RC tags.
- The repository targets .NET 10 and pins SDK `10.0.401` in `global.json`. Use `build/resolve-dotnet.ps1` so local builds honor that pin; it bootstraps the pinned SDK when needed.

## Required Checks

Run these checks before publishing:

```powershell
$dotnet = .\build\resolve-dotnet.ps1
& $dotnet restore .\NetBootDhcpTool.sln
& .\build\verify-maintenance.ps1
& $dotnet build .\NetBootDhcpTool.sln -c Release --no-restore
& $dotnet test .\src\NetBootDhcpTool.UnitTests\NetBootDhcpTool.UnitTests.csproj -c Release --no-build --minimum-expected-tests 1
& $dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release --no-build
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'UI smoke skipped: run from a standard-user Windows session.'
} else {
    & .\build\ui-automation-smoke.ps1
}
git diff --check
```

`NetBootDhcpTool.UnitTests` is the MSTest suite; `NetBootDhcpTool.Tests` is a console smoke and must be invoked with `dotnet run`. The UI smoke runs only from a standard-user Windows session; the command block records a skip when elevated, matching CI. Run each check separately and stop at the first failure.

For static-route-specific acceptance, follow the [maintenance guide](MAINTENANCE_GUIDE.md#standard-change-workflow); it defines the isolated Hyper-V route smoke and evidence required.

For documentation-only checks and change-log requirements, follow the [maintenance guide](MAINTENANCE_GUIDE.md#verification-evidence). A full build is not required unless the documentation changes packaging or release behavior.

## Installer result and isolation

Setup waits for the exact updater transaction and process exit, with a 15-minute upper bound. Exit `0` means HEALTHY, `21` means rollback completed, `22` means rollback failed, `23` means health verification failed and rollback completed, `24` means unexpected updater termination, and `25` means the wait timed out. Preflight/download/verification/space/access/cancellation are `10/11/12/13/14/16`. Values come from `InstallExitCode`, including NSIS cancellation. Inspect the matching `updates/health/<request-id>.status.json`; do not infer success from handoff or log text.

Fault tests must use a fresh, explicitly marked `NETBOOT_TEST_ROOT`, isolated TEMP/TMP and user data. Production paths, the registered installation and reparse paths are refused; test mode never writes production registration. Interrupted transactions recover through the durable journal, verify original backups before restoration, and retain diagnostic evidence if rollback cannot be guaranteed. See [T19](tasks/T19.md) for the process matrix, approved data-preservation fields, offline/online evidence boundaries and public RC gates.

## Packaging

Publish from the repository root after completing the required checks:

```powershell
.\build\publish.ps1 -GitHubRepository owner/repo
```

The publish script writes to a new `release\local-build-<timestamp>-<id>` folder by default (or to a caller-supplied new output directory) and refuses to overwrite existing output. It invokes `stop-test-processes.ps1`. That helper resolves actual executable paths and quoted `dotnet` entry points, stops only test runners proven to be under this repository's test-project output trees, and rechecks PID identity before stopping. It refuses to package while any NetBootDhcpTool GUI is running or its executable path is unreadable; no GUI is force-terminated. Close the app normally and retry. It leaves machine-wide PktMon captures and filters untouched.

`build/clean.ps1` retains the two newest `local-build-*` directories alongside the two newest versioned releases.

When `-GitHubRepository` is supplied, the local `latest.json` includes GitHub URLs for the versioned archive and Full/optional OTA packages. It is a build manifest, not a formally signed client manifest; the release publisher adds approved Gitee URLs and signs the final exact bytes. If the repository is not known yet, omit the parameter and rerun local packaging before any separately authorized release.

Expected outputs under the selected new build directory:

- `NetBootDhcpTool` (the portable product directory, without a version suffix)
- `NetBootDhcpTool-tools`
- `NetBootDhcpTool-v<version>` (versioned inventory copy for release recovery)
- `NetBootDhcpTool-v<version>.7z` and `.sha256` (archive root contains only `NetBootDhcpTool/`)
- `NetBootDhcpTool-full-v<version>.zip` and `.sha256`
- `NetBootDhcpTool-ota-v<base>-to-v<version>.zip` and `.sha256` only when a valid prior install manifest exists and the OTA is smaller than Full
- `latest.json`

The publish script tests the `.7z` archive and writes package SHA-256 sidecars plus the V2 manifest. The first build without a prior managed install manifest is Full-only. The legacy `latest.json` and signature remain frozen at v1.0.20. Before a formally authorized publication, verify that each package matches its sidecar and signed V2 manifest; the release workflow also verifies the final signed dual-source manifest and public bytes:

```powershell
$version = "<version>"
$buildDirectory = "<new local-build output directory>"
$archive = Join-Path $buildDirectory "NetBootDhcpTool-full-v$version.7z"
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$sidecarHash = (Get-Content -Raw "$archive.sha256").Trim().Split(' ')[0].ToLowerInvariant()
$manifest = Get-Content -Raw (Join-Path $buildDirectory 'latest-v2.json') | ConvertFrom-Json
$package = @($manifest.sevenZipPackages | Where-Object { $_.kind -eq 'Full' -and $_.fileName -ceq [IO.Path]::GetFileName($archive) })
if ($sidecarHash -ne $hash -or $manifest.version -ne $version -or $package.Count -ne 1 -or $package[0].sha256 -ne $hash -or $package[0].size -ne (Get-Item -LiteralPath $archive).Length) {
    throw "Local release assets are inconsistent for v$version"
}
```

### T19 migration and V2 update contract

The agreed migration boundary is one full installation from an older client, followed by automatic 7z updates. Do not attempt a v1.0.20 ZIP OTA Bridge. The v1.0.20 `latest.json` and signature remain frozen; V2 clients use `latest-v2.json` and its signature so ZIP-only clients cannot select a 7z payload. Older clients must download and run the paired Setup.exe and Full 7z once. Release notes must state that user settings and data are preserved and in-app updates resume after installation. No v1.0.20 asset, tag, or Gitee prerelease state may be changed during development.

The candidate Setup is a small NSIS bootstrapper around a self-contained SetupHelper and the same signed Full 7z used by the automatic updater. NSIS was selected for its compact script-based Windows installer and direct `makensis` build; its primary license permits commercial use and the LZMA module has an explicit linking exception. Inno Setup was not selected because its vendor asks commercial users to purchase a license, including for CI compiler use. WiX was not selected because a Windows Installer/MSI authoring chain adds more moving parts for this single-product migration; current WiX binary releases also require their Open Source Maintenance Fee EULA. The latest isolated lock-fix candidate measured Setup.exe at 33,381,527 bytes and Full 7z at 58,243,366 bytes; the 91,624,893-byte sum is only a first-migration bandwidth metric. An earlier 104,519,568-byte independent-asset sum was also allowed by preflight. Each Release asset is checked independently; no authoritative Gitee Release attachment cap is configured. These are local test-key assets, not publishable release artifacts.

Before installation, Setup verifies the V2 signature, Full 7z filename, size, SHA-256, archive manifest, and payload inventory. It migrates legacy settings/favorites/logs into `%LOCALAPPDATA%\NetBootDhcpTool` without overwriting existing user files; installation and update transactions must never delete or overwrite that data root. The Updater then performs one shared ZIP/7z transaction: locked package stream verification, canonical path validation, payload verification, staging, backup, replacement, startup health confirmation, and rollback. The Updater remains the final security authority. Full 7z is the required automatic-update payload; OTA 7z remains optional and must fall back to Full 7z where supported.

The NSIS wrapper waits for SetupHelper, which waits for the exact request's terminal typed status and the Updater process exit code. Setup.exe exit 0 means final `HEALTHY`; 21 means rollback completed, 22 means rollback failed, and 23 means health-check failure followed by rollback. Other shared protocol codes distinguish preflight, download, verification, disk capacity, permissions, cancellation, unexpected updater termination and timeout. The total wait is bounded at 15 minutes; it does not kill a transaction in the middle of replacement. The earlier handoff-only exit 0 behavior was replaced before RC acceptance. The current real locked-file test returns 21, and public RC installation tests return 0 only after HEALTHY.

Gitee's exact Release-attachment size cap is not confirmed. The public Gitee repository quota article describes repository files, not Release attachments. Setup, Full 7z, and every other Release asset are checked individually: the local mirror publisher warns above 95,000,000 bytes and has a centralized `GiteeReleaseAssetMaxBytes` hard limit that remains unset until an authoritative Release-specific limit is established. Its configured-limit test proves rejection before any Gitee request or upload POST. Independently downloadable asset sizes are never summed for this platform preflight; the first-migration sum is only recorded as a user download/bandwidth metric.

The product archive uses an explicit documentation allowlist in build/package-content.ps1. It keeps the README, release notes/changelog, one-page guide, and runtime troubleshooting document; it excludes internal task specs, TODOs, release workflows/tools, archived runner configuration, tests, PDBs, logs, caches, and temporary files. Windows CI tests the copied documentation inventory so internal files cannot silently re-enter the package.

## Formal Release Workflow

Local source changes are committed, pushed, and published only within the user's explicit release authorization. Keep the versioned feature change-log entry and bilingual release notes aligned; the package publisher takes user-facing release text from the current-version section of docs/RELEASE_NOTES.md, then falls back to the feature change log if that section is missing.

GitHub-hosted Windows runners are the only required Actions infrastructure for formal publication. Push the versioned commit to main, wait for Windows CI for that exact commit, then push its matching stable v<version> tag. CI covers restore, maintenance checks, Release build, unit tests, console smoke, non-admin UI smoke when available, release-pipeline tests and Gitee mirror idempotency tests.

The formal entry point is .github/workflows/formal-release.yml on main. Its verify-ci job confirms the tag resolves to a commit on main and that the exact tagged commit has a successful Windows CI run. The build-once job checks out that immutable tag, builds and tests one signed release bundle, computes package SHA-256 values, and uploads one artifact for 90 days. The publish-github job consumes that artifact, creates or reuses the GitHub Release, downloads and verifies every asset, then promotes that release to stable. Only this workflow receives the protected NETBOOT_UPDATE_SIGNING_PRIVATE_KEY secret; it does not receive GITEE_TOKEN. No local Windows Actions Runner is required.

GitHub formal-release status is independent from Gitee mirror status. A successful GitHub publication remains successful if Gitee is unavailable. Gitee publication is a local operation, independent of GitHub Actions and any GitHub Runner. Run `scripts/Invoke-GiteeMirrorSecure.ps1 -Tag v<version> -AssetDirectory <path>` in PowerShell 7. It uses the unified current-user DPAPI interface described below. Only first configuration prompts for an existing dedicated token; later PowerShell processes reuse the encrypted credential. The publisher receives a process-only `GITEE_TOKEN`, removed in `finally` on success or failure. Neither entry point accepts a plaintext token argument. The optional publisher `-AssetDirectory <path>` mode uses already-downloaded release files; it never builds or signs packages.

### Local Gitee credential management

The current Windows user's dedicated publishing token is encrypted by `ConvertFrom-SecureString` with Windows DPAPI and stored outside Git at `%LOCALAPPDATA%\NetBootDhcpTool\Secrets\gitee-token.dpapi`. The directory and file have a private current-user SID ACL. No symmetric key, plaintext `.env`, permanent User/Machine environment variable or credential in an application package is used. Another Windows account cannot normally decrypt this current-user DPAPI record.

```powershell
. .\scripts\Get-GiteeCredential.ps1 -LoadOnly
Set-GiteeCredential          # first hidden input; reuse an existing valid token
Test-GiteeCredential         # account, repository and Release read-only API checks
Remove-GiteeCredential       # deletes local ciphertext only; never revokes Gitee tokens
# Explicitly replace only an expired/revoked/rejected/compromised credential:
Set-GiteeCredential -Replace
```

The same commands are available through `Get-GiteeCredential.ps1 -Action Set|Test|Remove`. `Get-GiteeCredential` returns a SecureString, never plaintext. Existing unreadable ciphertext fails explicitly; it is not overwritten automatically. An unavailable API/feed retains the stored credential. Token write permissions are enforced by Gitee during publishing; read-only validation does not claim to prove all write permissions. Keep one valid project publishing token, and decide personally whether to revoke redundant account tokens after this reuse flow is verified. No script creates or revokes a PAT.

The mirror and optional source-preparation launcher use this same interface. `-SynchronizeSource` prepares the GitHub release's exact commit/tag on Gitee only when missing, checks fast-forward ancestry and never force-pushes. Git credentials are passed through scoped process configuration, not argv or a stored Git credential helper; traces are disabled and temporary configuration is cleared. The API-only asset publisher itself does not depend on Git synchronization. Setup and installed clients download public assets without a Gitee token.

`Protect-GiteeDiagnostic` redacts literal/URI-encoded tokens and Authorization/access_token values; diagnostic writers reject unredacted credentials. `Test-GiteeCredentialExposure` scans selected repository/log/status/telemetry files and readable process command lines without printing the credential. Windows CI runs a fake-credential regression with actual current-user DPAPI, private ACL, fresh-process reuse, failure cleanup, redaction, invalid replacement retention and local-only removal.

The mirror script uses the ordinary GitHub Release REST API and Release download endpoints. It validates every local/downloaded asset against the GitHub Release asset size and SHA-256, verifies the signed manifest and checksum sidecars, then queries Gitee. By default, verified GitHub assets are retained in `artifacts/gitee-mirror-assets/<tag>` so a later run can use `-AssetDirectory` to resume without downloading them again. Explicit local assets must match the requested tag, manifest version, filenames, GitHub REST digests, sidecars and trusted signature before any Gitee request or write.

The script creates a Gitee Release only when absent and otherwise reuses the existing Release. It preflights existing attachments through the single-attachment API and public download: matching assets are reused, missing assets are uploaded, and duplicate names or mismatched bytes hard-fail without deleting or overwriting anything. Every upload is immediately looked up by attachment ID and downloaded publicly for filename, size and SHA-256 verification before the next asset begins. The signature and `latest.json` are uploaded last. It then downloads every expected file from Gitee's public URLs again before changing the Release from prerelease to stable. Per-asset telemetry records start/end UTC, size, elapsed upload time, average speed, HTTP status, attachment ID and network error state; it contains no credentials. Rerunning the same tag resumes missing attachments without rebuilding, re-signing, deleting a Release or creating duplicate assets. The upload has a finite timeout and no automatic upload retry; an ambiguous response is resolved through Gitee readback. Gitee-generated `v<version>.zip` and `v<version>.tar.gz` source archives are ignored and are never formal client assets.

`.github/workflows/gitee-mirror.yml` is retained as a manual diagnostic entry that runs mocked recovery tests only. It does not receive `GITEE_TOKEN`, does not publish official assets, and is not a required completion path. No Gitee failure can change the result of GitHub Formal Release.

The client uses the signed GitHub manifest as the canonical formal-release identity when available. A source participates in speed probes and downloads only if its own valid manifest matches the current version, archive filename and SHA-256; signed Full/OTA package metadata must also match. An outdated or inconsistent Gitee mirror is excluded until synchronization completes. A valid Gitee-only client configuration remains supported.

For an incomplete GitHub formal-release job, rerun the job against its retained build artifact where possible; do not rebuild a different package under the same immutable tag. For a failed Gitee mirror, rerun the local PowerShell command with the same tag. If the GitHub download is unavailable, supply the retained official files with `-AssetDirectory`; the script still compares them to the GitHub Release API digests before upload. Never delete a Release, overwrite an attachment, move a tag, or force-push to recover a mirror.

The former self-hosted formal-release workflow is retained byte-for-byte at docs/archive/formal-release-self-hosted-2026-09-28.yml. The old build/publish-release.ps1 pipeline, its modules and the installed Runner configuration/cache are intentionally preserved until the first new-architecture formal release and Gitee readback complete. After that verification, review whether the old Runner process and flow can be retired; do not make the former Runner a prerequisite for new releases.

## Gitee Distribution

The public Gitee repository is https://gitee.com/joel20230302/NetBootDhcpTool. Keep it public so clients can read release metadata and assets without authentication. Gitee release assets are mirrored by `scripts/Publish-GiteeMirror.ps1` from an ordinary Windows PowerShell session using the user's local `GITEE_TOKEN`; that credential is never stored in the application package or GitHub Actions. The mirror copies only existing, hash-verified GitHub formal assets and does not synchronize Git refs. Workflow status is not involved in Gitee completion, so a mirror failure can be resumed independently.

## Upgrade Detection

Legacy ZIP-only clients use the Gitee latest Release API and its frozen `latest.json` attachment, then fall back to the legacy GitHub URL below. Do not add a 7z package to the legacy `packages` array: v1.0.20 could select a ZIP there and hand it to its legacy updater.

```text
https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json
```

After the one-time full install, V2 clients use the signed `latest-v2.json` manifest and its signature from GitHub/Gitee. Candidate Full 7z packages are listed in `sevenZipPackages`; the client compares the release version, package filename, size, and SHA-256 across sources before speed probing or download. The old manifest stays frozen and cannot advertise the V2 package to old clients. Source archives are never update candidates.

The legacy manifest contains:

- `version`: latest available version.
- `archiveName`: release archive file name.
- `archiveSha256`: SHA256 checksum for download verification.
- `downloadUrl`: direct URL for the primary host's release asset.
- `downloadMirrors`: approved alternate release asset URLs from the other host.
- `releasePageUrl`: user-facing release page on the host serving the manifest.
- `minimumSupportedVersion`: oldest version allowed to use this update path.
- `releaseNotes`: Markdown section from the current-version bilingual release notes, falling back to the feature change log when needed. The client selects zh-CN or en-US using the saved Language setting.
- `changes`: concise change items extracted from the same change-log section for the in-app update dialog.

For user-facing release descriptions, include `### zh-CN / 简体中文` and `### en-US / English` sections. V2 clients select a section using the saved `Language` in `%LOCALAPPDATA%\NetBootDhcpTool\config\appsettings.json`; `auto` follows the Windows UI language. Keep legacy `changes` readable for clients on the frozen channel.

The legacy client only recognizes the ZIP package fields and treats an unsupported package as a manual download. V2 probes only sources whose signed version, package filename, size, and SHA-256 match; Full 7z download is followed by Updater-side signature, locked-stream package, and payload verification before transactional application. The in-app flow shows **Restart to upgrade** after the verified download. A failed mirror may fall back to the other eligible source; a failed install restores the previous managed inventory.
