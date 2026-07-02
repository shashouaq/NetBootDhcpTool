# Release Process

This project uses a single repeatable release path for local packaging and GitHub distribution.

## Version Source

- The application version is defined in `src/NetBootDhcpTool.App/NetBootDhcpTool.App.csproj`.
- `build/publish.ps1` reads that version and must not use a separate hard-coded version.
- Release tags must use `v<version>`, for example `v1.0.6`.

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

## GitHub Release Standard

Create a GitHub Release with:

- Tag: `v<version>`
- Title: `NetBoot DHCP Tool v<version>`
- Assets:
  - `NetBootDhcpTool-v<version>.7z`
  - `NetBootDhcpTool-v<version>.7z.sha256`
  - `latest.json`

Release notes must be extracted and polished from `docs/FEATURE_CHANGELOG.md`. Do not write release notes without a matching change log entry.

## Future Upgrade Detection

The application should later check this URL:

```text
https://github.com/owner/repo/releases/latest/download/latest.json
```

The manifest contains:

- `version`: latest available version.
- `archiveName`: release archive file name.
- `archiveSha256`: SHA256 checksum for download verification.
- `downloadUrl`: direct GitHub asset URL.
- `releasePageUrl`: user-facing GitHub release page.
- `minimumSupportedVersion`: oldest version allowed to use this update path.

The updater must download the archive from `downloadUrl`, verify `archiveSha256`, and only then replace or extract files.
