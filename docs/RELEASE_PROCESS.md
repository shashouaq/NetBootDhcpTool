# Release Process

This project uses a single repeatable release path for local packaging, GitHub publication, and Gitee distribution.

For day-to-day maintenance, required change-log practice, GitHub synchronization, and upgrade work, start with `docs\MAINTENANCE_GUIDE.md`.

## Version Source

- The application version is defined in `src/NetBootDhcpTool.App/NetBootDhcpTool.App.csproj`.
- `build/publish.ps1` reads that version and must not use a separate hard-coded version.
- Release tags must use `v<version>`, for example `v1.0.12`.
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

The publish script tests the `.7z` archive and writes package SHA-256 sidecars plus `latest.json`. The first build without a prior managed install manifest is Full-only. Before a formally authorized publication, verify that each package matches its sidecar and manifest; the release workflow also verifies the final signed dual-source manifest and public bytes:

```powershell
$version = "<version>"
$buildDirectory = "<new local-build output directory>"
$archive = Join-Path $buildDirectory "NetBootDhcpTool-v$version.7z"
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$sidecarHash = (Get-Content -Raw "$archive.sha256").Trim().Split(' ')[0].ToLowerInvariant()
$manifest = Get-Content -Raw (Join-Path $buildDirectory 'latest.json') | ConvertFrom-Json
if ($sidecarHash -ne $hash -or $manifest.version -ne $version -or $manifest.archiveSha256 -ne $hash) {
    throw "Local release assets are inconsistent for v$version"
}
```

## Formal Release Workflow

Local source changes are committed, pushed, and published only within the user's explicit release authorization. Keep the versioned feature change-log entry and bilingual release notes aligned; the package publisher takes user-facing release text from the current-version section of docs/RELEASE_NOTES.md, then falls back to the feature change log if that section is missing.

GitHub-hosted Windows runners are the only required Actions infrastructure for formal publication. Push the versioned commit to main, wait for Windows CI for that exact commit, then push its matching stable v<version> tag. CI covers restore, maintenance checks, Release build, unit tests, console smoke, non-admin UI smoke when available, release-pipeline tests and Gitee mirror idempotency tests.

The formal entry point is .github/workflows/formal-release.yml on main. Its verify-ci job confirms the tag resolves to a commit on main and that the exact tagged commit has a successful Windows CI run. The build-once job checks out that immutable tag, builds and tests one signed release bundle, computes package SHA-256 values, and uploads one artifact for 90 days. The publish-github job consumes that artifact, creates or reuses the GitHub Release, downloads and verifies every asset, then promotes that release to stable. Only this workflow receives the protected NETBOOT_UPDATE_SIGNING_PRIVATE_KEY secret; it does not receive GITEE_TOKEN. No local Windows Actions Runner is required.

GitHub formal-release status is independent from Gitee mirror status. A successful GitHub publication remains successful if Gitee is unavailable. The separate .github/workflows/gitee-mirror.yml workflow starts after Formal Release succeeds or can be started manually for a tag. It runs scripts/Publish-GiteeMirror.ps1 on a GitHub-hosted Windows runner and reads GITEE_TOKEN only there.

The mirror script fetches the public GitHub Release through the ordinary GitHub REST and release-download endpoints. It checks the signed manifest and each package locally before changing Gitee. Before creating the Gitee Release, it synchronizes the exact formal tag and advances Gitee main only when its current tip is an ancestor of that release commit; a diverged branch fails without force-pushing. It reads the Gitee tag back and confirms it resolves to the same commit. The script creates a Gitee Release only when absent, reuses an existing Release, and skips an attachment only after downloading it and matching its expected size and SHA-256. A missing attachment is uploaded once; duplicate names or a different existing SHA-256 hard-fail without deleting or overwriting anything. latest.json and its signature are published after package assets. It then downloads the public Gitee assets and verifies their hashes and manifest before recording GITEE_MIRROR SUCCESS. Rerunning the same tag resumes missing attachments and does not rebuild or duplicate assets.

The client uses the signed GitHub manifest as the canonical formal-release identity when available. A source participates in speed probes and downloads only if its own valid manifest matches the current version, archive filename and SHA-256; signed Full/OTA package metadata must also match. An outdated or inconsistent Gitee mirror is excluded until synchronization completes. A valid Gitee-only client configuration remains supported.

For an incomplete GitHub formal-release job, rerun the job against its retained build artifact where possible; do not rebuild a different package under the same immutable tag. For a failed Gitee mirror, rerun the independent Gitee Mirror workflow with the same tag. It compares actual remote bytes and uploads only missing files. Never delete a Release, overwrite an attachment, move a tag, or force-push to recover a mirror.

The former self-hosted formal-release workflow is retained byte-for-byte at docs/archive/formal-release-self-hosted-2026-09-28.yml. The old build/publish-release.ps1 pipeline, its modules and the installed Runner configuration/cache are intentionally preserved until the first new-architecture formal release and Gitee readback complete. After that verification, review whether the old Runner process and flow can be retired; do not make the former Runner a prerequisite for new releases.

## Gitee Distribution

The public Gitee repository is https://gitee.com/joel20230302/NetBootDhcpTool. Keep it public so clients can read release metadata and assets without authentication. Gitee synchronization is performed by the independent Gitee Mirror workflow or scripts/Publish-GiteeMirror.ps1 with a tag input. Its credential is limited to that workflow and is never stored in the application package. The workflow checks out full Git history so the script can prove fast-forward ancestry and mirror the exact release tag. A mirror failure is tracked as a separate workflow result and can be resumed later.

## Upgrade Detection

The application first requests the latest stable release from Gitee's public API, reads its `latest.json` attachment, and falls back to this GitHub URL if that request or manifest validation fails:

```text
https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json
```

The manifest contains:

- `version`: latest available version.
- `archiveName`: release archive file name.
- `archiveSha256`: SHA256 checksum for download verification.
- `downloadUrl`: direct URL for the primary host's release asset.
- `downloadMirrors`: approved alternate release asset URLs from the other host.
- `releasePageUrl`: user-facing release page on the host serving the manifest.
- `minimumSupportedVersion`: oldest version allowed to use this update path.
- `releaseNotes`: Markdown section from the current-version bilingual release notes, falling back to the feature change log when needed. The client selects zh-CN or en-US using the saved Language setting.
- `changes`: concise change items extracted from the same change-log section for the in-app update dialog.

For user-facing release descriptions, include `### zh-CN / 简体中文` and `### en-US / English` sections. The current client selects a section using the saved `Language` in `%LOCALAPPDATA%\NetBootDhcpTool\config\appsettings.json`; `auto` follows the Windows UI language. Keep both translations in the manifest's legacy `changes` array too, so older clients still receive readable release details.

When `version` is newer than the running version, the app requests at most 64 KB from each valid download URL in parallel. Successful rates determine download order; a failed or timed-out probe is shown as unavailable and remains a later fallback. The toolbar shows rates and the selected host, with per-source details in its tooltip and confirmation dialog. After confirmation, a background download to the user's Downloads folder refreshes transfer speed and host in the status bar and reports a fallback switch. The archive is written to a temporary `.download` file and SHA-256 verified before it is moved into place; it is never installed automatically. Below 3 KB/s for 10 continuous seconds only produces an email contact hint and no automatic email.
