# Release Notes

## v1.0.9

NetBoot DHCP Tool v1.0.9 adds adapter controls, complete local static-route visibility, responsive route rendering, bilingual contextual help, and safer update/release workflows.

### Changes

- Added selected-adapter restart and MAC address changes with manual or locally administered random values, configurable adapter safety switches, and normal-exit restoration.
- Added manufacturer-published BMC default presets as explicit public plaintext records while keeping personal favorite credentials protected.
- Displayed all current IPv4 static routes across local interfaces by default, kept existing routes display-only, and optimized route rendering to avoid UI freezes.
- Added bilingual labels/help coverage, compact contextual `?` controls, hover explanations, rounded UI surfaces, run-scoped logs, and update manifest/download behavior.
- Recorded the complete change set in `docs/FEATURE_CHANGELOG.md`; the release archive is not auto-installed by the updater.

### Validation

- Release build and smoke tests passed before packaging; the self-contained archive, SHA256 sidecar, `latest.json`, GitHub tag, release, and assets are verified in the v1.0.9 release verification entry of `docs/FEATURE_CHANGELOG.md`.

## v1.0.8

NetBoot DHCP Tool v1.0.8 closes the maintenance backlog around local credential safety, network rollback completeness, DHCP lifecycle cleanup, and release update visibility.

### Changes

- Protected favorite passwords with current-user Windows DPAPI; automatically migrated legacy plaintext favorites and made JSON exports credential-free.
- Added per-favorite HTTPS preference and an explicit Open Web action; password values are masked everywhere in the main UI and details dialog.
- Captured and restored all non-APIPA IPv4 addresses and all default routes, including route metrics and policy stores, with backward-compatible legacy snapshots.
- Made DHCP startup transactional and scoped firewall rules to the application; rules created by the current session are removed on stop, failed startup, and window cleanup.
- Added a background GitHub `latest.json` check. The current version is shown at startup, and a validated newer release appears as a clickable direct download link.
- Moved main-window binding collections into `MainWindowViewModel` and expanded the smoke suite with DPAPI, updater, DHCP UDP, lease, and snapshot checks.

### Validation

- Release build passed with 0 warnings and 0 errors; the expanded smoke suite passed DPAPI migration/export, updater-manifest validation, DHCP UDP Offer/ACK and bind-failure recovery, lease exhaustion, route normalization, and legacy snapshot checks. The self-contained archive is 63,449,475 bytes, passed 7-Zip testing, has file version 1.0.8.0, and uses SHA256 `adbc955aa2367853321c12f9cbe6c7801879ef3bde9d15df0ec838934b9040a5` in the sidecar and manifest. Remote GitHub release verification is recorded in the feature change log after publication; elevated Hyper-V route smoke was not run because this terminal is not administrator.

## v1.0.7

NetBoot DHCP Tool v1.0.7 adds session-scoped static route management for isolated Windows network diagnostics.

### Changes

- Added a Static Routes tab supporting multiple IPv4 destination prefixes and per-rule wired adapter selection.
- Blank Gateway creates a directly connected route; a nonblank IPv4 Gateway creates a next-hop route.
- Canonicalized CIDR input, validated route metrics, rejected duplicate/conflicting routes, and allowed `0.0.0.0/0` with an explicit extra warning.
- Journaled only routes created by the current session and removed them on exit; retained failed cleanup records for next-start recovery.
- Added UTF-8 PowerShell route handling, typed route verification, rollback on partial batch failure, and a controlled Hyper-V vNIC smoke-test script.
- Updated the project-local SDK resolver to bootstrap and prefer .NET 8, refreshed the README and one-page guide, and bumped the application to v1.0.7.

### Validation

- Project-local .NET SDK `8.0.424` restore and Release build passed with 0 warnings and 0 errors.
- Existing test smoke returned `OK`.
- Elevated isolated Hyper-V vNIC route smoke returned `ROUTE_SMOKE_OK`; the script confirmed `LEFTOVER_SWITCHES=0`.
- The self-contained v1.0.7 package passed 7-Zip testing, SHA256 sidecar verification, `latest.json` verification, and assembly file-version verification.
- Remote GitHub Release verification is completed below as part of the v1.0.7 release.

## v1.0.6

NetBoot DHCP Tool v1.0.6 focuses on release consistency, update-readiness, and UI polish.

### Changes

- Standardized the release process around the application project version.
- Added `latest.json` manifest generation for future GitHub-based update checks.
- Added SHA256 checksum generation for the `.7z` release archive.
- Documented the GitHub Release asset standard and future updater download URL pattern.
- Optimized the top adapter summary area by tightening spacing and removing a duplicate current/last IP label.
- Fixed Open Logs so it opens Windows Explorer directly instead of routing through editor/debugger file associations.

### Validation

- Release build passed with 0 warnings and 0 errors.
- Smoke test returned `OK`.
- Local secret scan found no matches outside generated/release/log folders.
- Publish script rebuilt the portable app, versioned archive, checksum, and update manifest.
- 7-Zip archive testing passed.
