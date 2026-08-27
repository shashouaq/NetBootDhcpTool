# Release Notes

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
