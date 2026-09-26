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

## Formal Release Workflow

Local source changes are not committed, pushed, or published unless the user explicitly authorizes `发布` or `release`. Normal feature-change validation and source-preview rules are owned by the [maintenance guide](MAINTENANCE_GUIDE.md#standard-change-workflow). Keep the release change-log entry and notes aligned; `publish.ps1` prefers the current version section and falls back to `Unreleased` when that section does not exist.

GitHub-hosted Windows CI remains the gate for everyday branch and pull-request work. Before release, push the commit to `main`, wait for the exact commit's `windows-ci.yml` push run to succeed, and push its matching stable `v<version>` tag. The Windows CI run covers restore, maintenance checks, Release build, unit tests, console smoke, non-admin UI smoke when available, and the release-pipeline helper tests.

The only formal publication entry point is `.github/workflows/formal-release.yml` → **Run workflow** on `main`, with the stable tag as input. A GitHub-hosted validation job confirms the tag is in `main` and that Windows CI passed for both the exact application-tag commit and the exact workflow/publisher commit. The publish job then runs on `[self-hosted, windows, x64, netboot-release]`. It loads the release scripts from the CI-verified workflow commit but builds the application sources and reads the version from the requested immutable tag. It builds the existing `.7z` package once, stores the package and release checkpoint outside the checkout, and uploads those same bytes to GitHub and Gitee. The `.7z` format remains unchanged for current updater compatibility.

GitHub reported `main` as unprotected on 2026-09-26. The workflow checks CI evidence for normal dispatches, but a repository writer can still change that workflow and push directly. A branch ruleset requiring the Windows CI check is the remaining repository-level control if direct pushes should no longer bypass review; configure that separately because it changes the team's normal push flow.

Register one repository-level Windows x64 self-hosted runner before dispatching a release, following [GitHub's runner setup](https://docs.github.com/en/actions/how-tos/manage-runners/self-hosted-runners/add-runners). In addition to GitHub's default `self-hosted`, `windows`, and `x64` labels, assign it the custom `netboot-release` label. Keep that label on exactly one runner restricted to this repository; normal pull-request CI must continue to use GitHub-hosted runners. The runner must have Git, PowerShell 7, GitHub CLI, curl 7.76 or newer, and 7-Zip; the workflow installs .NET SDK 10.0.401 in the runner user's persistent `%LOCALAPPDATA%\NetBootDhcpTool\dotnet` directory. Keep its `_work\_release-state\NetBootDhcpTool` directory on persistent storage so an Actions retry can reuse the exact archive and saved Gitee attachment IDs. Do not clear this directory while a release is incomplete.

The current `NetBootRelease-DESQIAOWEI` runner is registered under `%LOCALAPPDATA%\NetBootDhcpTool\actions-runner` and runs in the logged-in user's session. Confirm its GitHub status is `online` before dispatch. If it is offline, start `run.cmd` from that directory and keep the session active through publication. It is not installed as a Windows service on this host.

The publisher creates each GitHub/Gitee Release only when absent. New Releases remain a GitHub draft and a Gitee prerelease until all assets and manifests have passed remote verification, so clients do not see an incomplete version as the latest stable release. It reuses a named attachment only after downloading and comparing its SHA-256; a same-name asset with different content fails closed and is never overwritten. Local filenames must exactly match remote asset names. After each successful Gitee upload, the attachment ID and returned URL are checkpointed locally. Recovery prioritizes the official single-attachment lookup and download endpoints by ID; when those endpoints are unavailable, it downloads from the URL returned for that saved ID. The attachment list endpoint is used only when an upload outcome is ambiguous or the checkpoint is missing. An ambiguous upload is never blindly repeated. Gitee release metadata is sent as JSON. GitHub and Gitee `latest.json` are published only after both archives and checksum sidecars pass complete remote readback; both manifests are then verified to contain identical dual-source content before the GitHub Release is published and the Gitee Release is promoted last. If the final Gitee promotion is interrupted, the verified GitHub Release remains available and a retry safely completes the Gitee promotion.

If a release job fails, use **Re-run failed jobs** on that workflow run or dispatch the same tag again from the same runner. The persistent cache prevents rebuilding the package; verified assets are skipped. A Gitee `main` branch that cannot fast-forward fails closed and requires resolving the source divergence before retrying. The workflow does not delete a Release or force-push a tag. The self-hosted publish job has a 30-minute upper bound so a blocked credential prompt or stalled platform operation cannot occupy the runner for hours; inspect the failed step before retrying.

The workflow reads the repository Actions secret `GITEE_TOKEN`, which must have permission to push to the Gitee repository and manage Releases and attachments. Never print it or put it in a command-line URL. CI exercises the Gitee Release JSON field contract for create, metadata update, and stable promotion; the publisher sends those fields as JSON. Uploads use one HTTP/1.1 request with a 10-minute transfer cap; there is no repeated 30-minute retry loop. If an upload response is ambiguous, recovery checks the saved attachment ID or performs one list lookup before deciding whether another upload is safe. Gitee API behavior follows the [official Gitee API v5 specification](https://gitee.com/api/v5/swagger_doc.json), including single-attachment metadata and download operations.

For every release, verify the exact source commit and tag, the green exact-commit CI run, the stable GitHub and Gitee Release pages, the archive and sidecar SHA-256 values on both hosts, and identical dual-source manifests. The publisher performs full archive readback from each host and checks the GitHub manifest readback; do not claim publication while that workflow is incomplete.

The v1.0.14 publication used the former GitHub-hosted sync path; its measured full downloads and variable 64 KiB client probes are historical evidence only. v1.0.16 passed the self-hosted publication workflow and an idempotent same-tag rerun; exact run IDs and hashes are recorded in [T19](tasks/T19.md). The app continues to probe the Gitee and GitHub sources independently and select by measured speed. It does not pin users to one host.

## Gitee Distribution

The public Gitee repository is `https://gitee.com/joel20230302/NetBootDhcpTool`. The app checks its latest stable Gitee Release first and falls back to the GitHub manifest if Gitee metadata cannot be read. Both hosts publish a `latest.json` containing Gitee and GitHub archive URLs. When an update is available, the app requests at most 64 KB from each mirror, orders the sources by measured speed, shows the chosen source, and tries the next approved mirror if transfer or SHA-256 validation fails. Keep the Gitee repository public so unauthenticated clients can read release metadata and assets.

The publisher also fast-forward pushes GitHub `main` and the exact release tag to Gitee without force. Gitee attachment APIs are called directly from the domestic Windows runner. A workflow retry looks up a checkpointed attachment by its ID and verifies its downloaded bytes; list lookup is reserved for a lost upload response or missing local checkpoint. Keep the `GITEE_TOKEN` Actions secret configured on GitHub; the desktop app never receives that credential.
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
