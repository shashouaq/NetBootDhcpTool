# Maintenance Guide

This guide is the operating standard for maintaining NetBoot DHCP Tool. Follow it for every code, configuration, documentation, packaging, and release change.

## Repository

- GitHub repository: `https://github.com/shashouaq/NetBootDhcpTool`
- Default branch: `main`
- Release tag format: `v<version>`
- Current release manifest URL:

```text
https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json
```

## Non-Negotiable Maintenance Rules

- Inspect the existing code and documents before editing.
- Keep changes scoped to the requested work.
- Preserve unrelated user changes.
- Record every code, config, document, test, build, packaging, or release change in `docs/FEATURE_CHANGELOG.md`.
- Do not mark work complete until the change log entry exists.
- Run relevant validation when feasible.
- For documentation-only changes, run lightweight checks instead of a full build unless the docs affect packaging or release behavior.
- Every finished change must be committed and pushed to GitHub before handoff.
- Every version upgrade must update local release artifacts and GitHub Release assets.

## Standard Change Workflow

1. Check local state:

```powershell
git status -sb
git pull --ff-only
```

2. Make the scoped change.
3. Update `docs/FEATURE_CHANGELOG.md` with:

- date
- type
- affected files/modules
- concrete change
- verification
- user impact

4. Run validation:

```powershell
$dotnet = .\build\resolve-dotnet.ps1
& $dotnet build .\NetBootDhcpTool.sln -c Release --no-restore
& $dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release
```

For documentation-only changes, at minimum run:

```powershell
git diff --check
git status -sb
```

5. Commit and push:

```powershell
git add <changed-files>
git commit -m "<short change summary>"
git push
```

## Version Upgrade Workflow

Use this workflow when changing the published version.

1. Update the application version in `src/NetBootDhcpTool.App/NetBootDhcpTool.App.csproj`.
2. Search for the old version and update intentional references:

```powershell
rg -n "1\.0\.6|v1\.0\.6" .
```

3. Add a complete change log entry in `docs/FEATURE_CHANGELOG.md`.
4. Generate release notes from the change log in `docs/RELEASE_NOTES.md`.
5. Run validation:

```powershell
$dotnet = .\build\resolve-dotnet.ps1
& $dotnet build .\NetBootDhcpTool.sln -c Release --no-restore
& $dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release
```

6. Publish local artifacts:

```powershell
.\build\stop-test-processes.ps1
.\build\publish.ps1 -GitHubRepository shashouaq/NetBootDhcpTool
```

7. Commit and push source:

```powershell
git add .
git commit -m "release v<version>"
git push
git tag v<version>
git push origin v<version>
```

8. Create or update the GitHub Release:

```powershell
& "C:\Program Files\GitHub CLI\gh.exe" release create v<version> `
  release\NetBootDhcpTool-v<version>.7z `
  release\NetBootDhcpTool-v<version>.7z.sha256 `
  release\latest.json `
  --repo shashouaq/NetBootDhcpTool `
  --title "NetBoot DHCP Tool v<version>" `
  --notes-file docs\RELEASE_NOTES.md
```

If the release already exists, upload assets with overwrite:

```powershell
& "C:\Program Files\GitHub CLI\gh.exe" release upload v<version> `
  release\NetBootDhcpTool-v<version>.7z `
  release\NetBootDhcpTool-v<version>.7z.sha256 `
  release\latest.json `
  --repo shashouaq/NetBootDhcpTool `
  --clobber
```

9. Verify GitHub assets and upgrade manifest:

```powershell
& "C:\Program Files\GitHub CLI\gh.exe" release view v<version> --repo shashouaq/NetBootDhcpTool
$r = Invoke-WebRequest -Uri "https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json" -UseBasicParsing
[System.Text.Encoding]::UTF8.GetString($r.Content)
```

## Local Cleanup Policy

- Keep only the current release in `release/` unless an older version is needed for active troubleshooting.
- Keep:
  - `release\NetBootDhcpTool`
  - `release\NetBootDhcpTool-tools`
  - `release\NetBootDhcpTool-v<current-version>`
  - `release\NetBootDhcpTool-v<current-version>.7z`
  - `release\NetBootDhcpTool-v<current-version>.7z.sha256`
  - `release\latest.json`
  - `release\README_RUN.txt`
- Remove stale `src/**/bin` and `src/**/obj` caches when preparing a clean workspace.
- Do not commit `release/`, `.dotnet/`, `logs/`, `bin/`, or `obj/`.

## Upgrade Detection Contract

Future in-app upgrade detection must consume:

```text
https://github.com/shashouaq/NetBootDhcpTool/releases/latest/download/latest.json
```

The app must:

- compare `version` with the running app version
- open `releasePageUrl` for manual upgrade, or download `downloadUrl`
- verify the downloaded archive with `archiveSha256`
- never install a download that fails checksum verification

## Troubleshooting

- If `gh` is not found, use the installed path:

```powershell
& "C:\Program Files\GitHub CLI\gh.exe" auth status
```

- If the token is invalid, re-authenticate:

```powershell
& "C:\Program Files\GitHub CLI\gh.exe" auth logout -h github.com -u shashouaq
& "C:\Program Files\GitHub CLI\gh.exe" auth login -h github.com -w
```

- If publish fails because files are locked, stop app processes:

```powershell
.\build\stop-test-processes.ps1
```
