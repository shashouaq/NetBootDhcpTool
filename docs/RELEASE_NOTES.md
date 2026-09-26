# Release Notes

## v1.0.15

NetBoot DHCP Tool v1.0.15 hardens DHCP lease recovery and route adapter identity checks, and makes update selection and downloads easier to recover.

### Changes

- Stop DHCP before acknowledging a lease when its journal cannot be saved; unexpected-stop recovery waits for active network operations.
- Verify a static-route target by stable adapter GUID immediately before writing the route.
- Select the newest valid update manifest across Gitee and GitHub, even when one mirror is stale.
- Fall back when a download source is idle for 30 seconds; explain and provide cancellation in the bilingual update flow.

### Validation

- Pinned .NET 10.0.401 Release solution build: 0 warnings/errors; MSTest: 150/150; console smoke: `OK`; non-admin WPF UI smoke: `UI_SMOKE_OK`; maintenance checks: 295 localization keys and 63/63 static help buttons.
- Physical adapter and independent DHCP client acceptance are not included in this release's automated evidence.

## v1.0.14

NetBoot DHCP Tool v1.0.14 adds Gitee release distribution for mainland users and selects the faster available update mirror after a bounded download-speed probe.

### Changes

- Publishes the same SHA-256-verified archive to Gitee and GitHub and keeps both download URLs in each update manifest.
- Checks Gitee release metadata first, then falls back to GitHub when Gitee metadata is unavailable.
- When an update is found, concurrently samples up to 64 KB from each source and prioritizes the faster mirror; failed sources remain available as automatic fallbacks.
- Shows source-speed results and the selected mirror beside the update link and in the confirmation; the download status refreshes its live rate and identifies source changes.
- Preserves SHA-256 verification, safe replacement, bilingual UI, and manual installation.

### Validation

- Release solution build passed with 0 warnings and 0 errors; all 144 MSTest tests passed; console smoke returned `OK`; non-admin WPF UI smoke verified the speed display, selected mirror, live fallback status, and confirmation details. Maintenance checks reported 294 localization keys and 62/62 static help buttons.
- The self-contained x64 archive is about 55 MiB and passed 7-Zip integrity testing; its SHA-256 sidecar and `latest.json` agree. The packaged executable file version is `1.0.14.0`.

## v1.0.13

NetBoot DHCP Tool v1.0.13 bundles the approved maintenance work through T17. Startup and refresh now read network adapters and current IPv4/IPv6 routes concurrently, reducing the measured network-discovery wait while preserving the full current route list and fresh checks before route changes.

### Changes

- Added visible operation phases and elapsed timing for startup discovery, network actions, cancellation waits, and normal exit recovery.
- Strengthened DHCP session/interface ownership, lease persistence and client identity handling, firewall cleanup, adapter restoration, and recovery records.
- Improved input/range validation, configuration/favorites persistence and comparison, and final download verification.
- Kept current route reads uncached; both parallel startup reads must finish before the existing UI snapshot is populated.

### Validation

- Release solution build: 0 warnings/errors; MSTest 140/140; console smoke `OK`; non-admin WPF UI smoke `UI_SMOKE_OK`; maintenance checks passed.
- On the connected Linux client, real DORA, same-address renewal after application process restart, DHCPRELEASE, address reuse under a temporary second MAC, lease UI isolation, and normal adapter/firewall restoration passed. Startup discovery samples were 2.75 and 3.29 seconds versus the T16 single-run serial baseline of 4.44 seconds; timings vary with system load.

## v1.0.12

NetBoot DHCP Tool v1.0.12 moves the project to .NET 10, adds repeatable Windows CI and focused regression tests, and makes recovery safer to inspect and apply item by item.

### Changes

- Migrated all projects to .NET 10 and pinned the SDK and Microsoft Testing Platform runner used by local and CI tests.
- Added unit coverage for CIDR and route validation, DHCP lease behavior, JSON backups, legacy-data migration failures, and adapter identity matching; added a Windows build/test workflow using the Node 24-compatible `setup-dotnet@v5` action and a maintenance consistency gate.
- Split Recovery Center into its own UI and `MainWindow` partial. Each adapter snapshot, pending MAC restore, and session route is listed independently with adapter identity, capture time, confirmation, and operation history; restore checks the resulting state.
- Made legacy configuration/log migration report individual copy failures, continue with later files, and retain the original data.
- Reduced the main-window minimum to 900x560 DIP and enabled layout rounding for compact/high-DPI screens.
- Tightened release cleanup so it does not stop machine-wide PktMon capture or filters and refuses to package while the application is open.

### Validation

- .NET 10.0.401 Release solution build passed with 0 warnings and 0 errors; all 17 MSTest tests passed; the existing protocol/persistence smoke returned `OK`; non-admin WPF UI automation passed at compact bounds. Maintenance checks reported 265 synchronized localization keys and 60/60 static buttons with help keys; the dependency audit found no vulnerable packages. The approximately 56.7 MB self-contained archive passed 7-Zip testing; its SHA-256 sidecar and manifest match, and its file version is `1.0.12.0`. The administrator-only Hyper-V route smoke was not run because this terminal is not elevated.

## v1.0.10

NetBoot DHCP Tool v1.0.10 adds temporary multi-adapter IPv4/IPv6 static routes with automatic route metrics and safer route cleanup, while hardening HTTPS reachability probes to use normal certificate validation.

### Changes

- Added multiple temporary IPv4/IPv6 route rules, including single-address normalization, per-rule adapter selection, on-link gateway validation, overlap preview, automatic effective metrics, and exact session-owned cleanup.
- Kept existing system routes read-only and protected the local default route from being overwritten or removed by the static-route feature.
- Changed HTTPS reachability probes to use the Windows/.NET certificate trust policy; untrusted self-signed certificates no longer count as successful HTTPS without explicit system trust.

### Validation

- Release build, unit smoke, dependency vulnerability audit, secret-pattern audit, route safety audit, and packaged archive verification are recorded in `docs/FEATURE_CHANGELOG.md`. The self-contained archive is `63,497,821` bytes, passed 7-Zip testing, has file version `1.0.10.0`, and uses SHA256 `d84b55b9f3cc44f9f4b55c2644514a89bc93fdfa13d44fc26f4b60378e4a1798`; the local package matches the remote manifest and a freshly downloaded remote archive.

## v1.0.9

NetBoot DHCP Tool v1.0.9 adds adapter controls, complete local static-route visibility, responsive route rendering, bilingual contextual help, and safer update/release workflows.

### Changes

- Added selected-adapter restart and MAC address changes with manual or locally administered random values, configurable adapter safety switches, and normal-exit restoration.
- Added manufacturer-published BMC default presets as explicit public plaintext records while keeping personal favorite credentials protected.
- Displayed all current IPv4 static routes across local interfaces by default, kept existing routes display-only, and optimized route rendering to avoid UI freezes.
- Added bilingual labels/help coverage, compact contextual `?` controls, hover explanations, rounded UI surfaces, run-scoped logs, and update manifest/download behavior.
- Recorded the complete change set in `docs/FEATURE_CHANGELOG.md`; the release archive is not auto-installed by the updater.

### Validation

- Release build and smoke tests passed before packaging. The self-contained archive is `63,486,110` bytes, passed 7-Zip testing, has file version `1.0.9.0`, and uses SHA256 `2ad3144c5686042017426800657215b597b723ae897cb88e5d6775e40676b3c9`; local and remote archive/manifest verification is recorded in `docs/FEATURE_CHANGELOG.md`.

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
