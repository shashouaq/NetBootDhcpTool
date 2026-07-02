# Release Notes

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
