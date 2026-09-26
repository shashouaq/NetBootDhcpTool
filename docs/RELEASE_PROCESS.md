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

The publish script invokes `stop-test-processes.ps1`. That helper resolves actual executable paths and quoted `dotnet` entry points, stops only test runners proven to be under this repository's test-project output trees, and rechecks PID identity before stopping. It refuses to package while any NetBootDhcpTool GUI is running or its executable path is unreadable; no GUI is force-terminated. Close the app normally and retry. It leaves machine-wide PktMon captures and filters untouched.

When `-GitHubRepository` is supplied, `release\latest.json` includes GitHub download URLs for the versioned archive. If the repository is not known yet, omit the parameter and rerun the publish command before uploading a GitHub release.

Expected outputs:

- `release\NetBootDhcpTool`
- `release\NetBootDhcpTool-tools`
- `release\NetBootDhcpTool-v<version>`
- `release\NetBootDhcpTool-v<version>.7z`
- `release\NetBootDhcpTool-v<version>.7z.sha256`
- `release\latest.json`

The publish script tests the `.7z` archive and writes its SHA-256 sidecar and `latest.json` manifest. Before uploading, verify that the local manifest and checksum sidecar both match the archive:

```powershell
$version = "<version>"
$archive = ".\release\NetBootDhcpTool-v$version.7z"
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$sidecarHash = (Get-Content -Raw "$archive.sha256").Trim().Split(' ')[0].ToLowerInvariant()
$manifest = Get-Content -Raw .\release\latest.json | ConvertFrom-Json
if ($sidecarHash -ne $hash -or $manifest.version -ne $version -or $manifest.archiveSha256 -ne $hash) {
    throw "Local release assets are inconsistent for v$version"
}
```

## GitHub Release Standard

Local source changes are not committed/pushed or published unless the user explicitly says `发布` or `release`. Normal feature-change validation and source-preview rules are owned by the [maintenance guide](MAINTENANCE_GUIDE.md#standard-change-workflow). When publication is explicitly authorized, publish the source commit and matching `v<version>` tag, create/update the GitHub Release from that tag, then verify the remote commit, tag, release, and assets before claiming GitHub publication. Keep the release change-log entry and notes aligned; `publish.ps1` prefers the current version section and falls back to `Unreleased` when that version section does not exist.

Before creating the GitHub Release, confirm the Windows CI workflow succeeded for the exact commit and verify that the pushed tag resolves to that commit. The workflow is defined in `.github/workflows/windows-ci.yml`; it runs restore, maintenance checks, Release build, unit tests, console smoke, and non-admin UI smoke when the runner is not elevated.

Compare the local release commit with the remote branch and tag (for annotated tags, compare the peeled `^{}` tag ref):

```powershell
git rev-parse HEAD
git ls-remote origin main "refs/tags/v<version>" "refs/tags/v<version>^{}"
```

Create a GitHub Release with:

- Tag: `v<version>`
- Title: `NetBoot DHCP Tool v<version>`
- Assets:
  - `NetBootDhcpTool-v<version>.7z`
  - `NetBootDhcpTool-v<version>.7z.sha256`
  - `latest.json`

Release notes must be extracted and polished from `docs/FEATURE_CHANGELOG.md`. Do not write release notes without a matching change log entry.

After creating or updating a release, verify the GitHub Release assets and the latest manifest:

```powershell
& "C:\Program Files\GitHub CLI\gh.exe" release view v<version> --repo shashouaq/NetBootDhcpTool
$manifest = Invoke-RestMethod -Uri "https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json"
if ($manifest.version -ne "<version>") { throw "Latest manifest version mismatch: $($manifest.version)" }
```

## Gitee Distribution

The public Gitee repository is `https://gitee.com/joel20230302/NetBootDhcpTool`. The app checks its latest stable Gitee Release first and falls back to the GitHub manifest if Gitee metadata cannot be read. Both hosts publish a `latest.json` containing Gitee and GitHub archive URLs. When an update is available, the app requests at most 64 KB from each mirror, orders the sources by measured speed, shows the chosen source, and tries the next approved mirror if transfer or SHA-256 validation fails. Keep the Gitee repository public so unauthenticated clients can read release metadata and assets.

The GitHub Actions workflow `.github/workflows/gitee-release-sync.yml` runs after a stable GitHub Release is published; it also accepts a manually supplied existing stable tag for retrying a failed sync. It downloads that release's archive, checksum, and manifest; verifies their local hashes; then fast-forward pushes GitHub `main` and the exact release tag to Gitee without force. It creates or updates the matching Gitee Release and manages three assets (`.7z`, `.7z.sha256`, and `latest.json`). An existing archive with the expected size is reused and fully downloaded for SHA-256 verification; a missing or differently sized archive is replaced. The checksum and manifest are replaced, and the dual-source manifest is published only after the archive and checksum pass remote readback. Attachment uploads use `curl.exe` with HTTP/1.1 and Gitee API v5 multipart form fields `access_token` and `file`; curl generates the multipart boundary. The access token is passed through a short-lived curl config file that the workflow removes after each upload. Each upload has a 20-second connection timeout, a 30-minute transfer timeout, and five retries with a five-second delay. The workflow logs each attachment upload and archive readback duration, and uploads the small checksum before the archive so a failed large upload is distinguishable from a general attachment API failure. If the upload response is interrupted, the script checks the Release attachment list before reporting failure. It then replaces the GitHub `latest.json` with the verified dual-source manifest and reads it back. The workflow requires `contents: write` for that one release-asset update. A non-fast-forward Git push fails closed and requires resolving the Gitee branch divergence before retrying.

The workflow reads the repository Actions secret named `GITEE_TOKEN`. Configure it in the GitHub repository under **Settings → Secrets and variables → Actions → New repository secret**. The value must be a Gitee token that can push to this repository and create/update Releases and upload/delete Release attachments. The token is used only by the workflow; the desktop app does not need it. Never print or put the token in a command-line URL. The Gitee API operations follow the [official Gitee API v5 specification](https://gitee.com/api/v5/swagger_doc.json).

The temporary credential check created, read, and deleted a disposable Gitee Git branch. For the v1.0.14 publication, the checksum and full archive have been uploaded through the attachment API; the archive was downloaded from Gitee on the local network, its byte count matched, and its SHA-256 matched the local archive. The final dual-source manifest and exact-commit workflow readback must also succeed before calling T18 complete. If Gitee returns `null` with HTTP 200 for an absent tag release, treat it as not yet created; do not PATCH a missing release ID. Do not publish a test tag or release just to exercise this path.

Download the remote archive and compare its hash with the local archive and published sidecar before closing the release:

```powershell
$version = "<version>"
$archiveName = "NetBootDhcpTool-v$version.7z"
$localArchive = Join-Path (Get-Location) "release\$archiveName"
$localSidecar = "$localArchive.sha256"
$verifyDir = Join-Path $env:TEMP ("netboot-release-verify-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $verifyDir | Out-Null
& "C:\Program Files\GitHub CLI\gh.exe" release download "v$version" --repo shashouaq/NetBootDhcpTool --dir $verifyDir
if ($LASTEXITCODE -ne 0) { throw "Could not download release assets for v$version" }
$expectedHash = (Get-Content -Raw $localSidecar).Trim().Split(' ')[0].ToLowerInvariant()
$localHash = (Get-FileHash -LiteralPath $localArchive -Algorithm SHA256).Hash.ToLowerInvariant()
$remoteArchive = Join-Path $verifyDir $archiveName
$remoteSidecar = "$remoteArchive.sha256"
$remoteExpectedHash = (Get-Content -Raw $remoteSidecar).Trim().Split(' ')[0].ToLowerInvariant()
$remoteHash = (Get-FileHash -LiteralPath $remoteArchive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($localHash -ne $expectedHash -or $remoteHash -ne $expectedHash -or $remoteHash -ne $remoteExpectedHash) { throw "Release archive SHA-256 mismatch" }
$manifest = Invoke-RestMethod -Uri "https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json"
if ($manifest.version -ne $version -or $manifest.archiveName -ne $archiveName -or $manifest.archiveSha256 -ne $expectedHash) { throw "Published manifest does not match v$version archive" }
& "C:\Program Files\7-Zip\7z.exe" t $remoteArchive
if ($LASTEXITCODE -ne 0) { throw "Downloaded release archive failed 7-Zip testing" }
```

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
- `releaseNotes`: Markdown release-note section extracted from the unreleased/current-version change-log section.
- `changes`: concise change items extracted from the same change-log section for the in-app update dialog.

When `version` is newer than the running version, the app requests at most 64 KB from each valid download URL in parallel. Successful rates determine download order; a failed or timed-out probe is shown as unavailable and remains a later fallback. The toolbar shows rates and the selected host, with per-source details in its tooltip and confirmation dialog. After confirmation, a background download to the user's Downloads folder refreshes transfer speed and host in the status bar and reports a fallback switch. The archive is written to a temporary `.download` file and SHA-256 verified before it is moved into place; it is never installed automatically. Below 3 KB/s for 10 continuous seconds only produces an email contact hint and no automatic email.
