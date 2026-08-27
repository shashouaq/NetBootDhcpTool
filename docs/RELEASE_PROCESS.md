# Release Process

This project uses a single repeatable release path for local packaging and GitHub distribution.

For day-to-day maintenance, required change-log practice, GitHub synchronization, and upgrade work, start with `docs\MAINTENANCE_GUIDE.md`.

## Version Source

- The application version is defined in `src/NetBootDhcpTool.App/NetBootDhcpTool.App.csproj`.
- `build/publish.ps1` reads that version and must not use a separate hard-coded version.
- Release tags must use `v<version>`, for example `v1.0.8`.
- The resolver selects a project-local or system .NET 8 SDK and bootstraps the local SDK when neither is available.

## Required Checks

Run these checks before publishing:

```powershell
$dotnet = .\build\resolve-dotnet.ps1
& $dotnet build .\NetBootDhcpTool.sln -c Release --no-restore
& $dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release
```

If only documentation changed, a lightweight Markdown/content review is acceptable, but the change must still be recorded in `docs/FEATURE_CHANGELOG.md`.

## Packaging

Stop test processes and publish from the repository root:

```powershell
.\build\stop-test-processes.ps1
.\build\publish.ps1 -GitHubRepository owner/repo
```

When `-GitHubRepository` is supplied, `release\latest.json` includes GitHub download URLs for the versioned archive. If the repository is not known yet, omit the parameter and rerun the publish command before uploading a GitHub release.

Expected outputs:

- `release\NetBootDhcpTool`
- `release\NetBootDhcpTool-tools`
- `release\NetBootDhcpTool-v<version>`
- `release\NetBootDhcpTool-v<version>.7z`
- `release\NetBootDhcpTool-v<version>.7z.sha256`
- `release\latest.json`

The publish script must test the `.7z` archive before the release is considered valid.
For static route changes, run `build\route-smoke.ps1` as administrator when Hyper-V is available; the script owns and removes only its uniquely named test switches.

## GitHub Release Standard

Every finished source change must be committed and pushed to `https://github.com/shashouaq/NetBootDhcpTool` before handoff. Every version upgrade must also push its tag and update the GitHub Release assets.

Create a GitHub Release with:

- Tag: `v<version>`
- Title: `NetBoot DHCP Tool v<version>`
- Assets:
  - `NetBootDhcpTool-v<version>.7z`
  - `NetBootDhcpTool-v<version>.7z.sha256`
  - `latest.json`

Release notes must be extracted and polished from `docs/FEATURE_CHANGELOG.md`. Do not write release notes without a matching change log entry.

After creating or updating a release, verify:

```powershell
& "C:\Program Files\GitHub CLI\gh.exe" release view v<version> --repo shashouaq/NetBootDhcpTool
$r = Invoke-WebRequest -Uri "https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json" -UseBasicParsing
[System.Text.Encoding]::UTF8.GetString($r.Content)
```

## Upgrade Detection

The application checks this URL in the background after the main window is ready:

```text
https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json
```

The manifest contains:

- `version`: latest available version.
- `archiveName`: release archive file name.
- `archiveSha256`: SHA256 checksum for download verification.
- `downloadUrl`: direct GitHub asset URL.
- `releasePageUrl`: user-facing GitHub release page.
- `minimumSupportedVersion`: oldest version allowed to use this update path.

When `version` is newer than the running version, the UI shows a clickable `有新版本！` / `New version available!` link beside the current version and opens `downloadUrl` in the system browser. The current release does not install or replace files automatically; a future installer must verify `archiveSha256` and provide rollback before adding that behavior.
