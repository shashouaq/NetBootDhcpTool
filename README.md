# NetBoot DHCP Tool

Version: 1.0.9

Authors: Joel & Codex

Windows green portable IPv4 DHCP, adapter IP configuration, static route rules, adapter restart/MAC tools, ping scan, web open, favorites, bilingual UI, contextual `?` help buttons, and logs.

## Static Routes

The Static Routes tab accepts multiple IPv4 destination prefixes and assigns each one to a selected wired adapter. Enter a gateway for a next-hop route; leave Gateway blank for a direct route. Prefixes are normalized to the network boundary, route metrics must be between 1 and 65535, and `0.0.0.0/0` is allowed but requires an additional confirmation because it can change all IPv4 traffic.

Only routes created by this session are removed when the application closes. Existing system routes are not overwritten or removed. If the original adapter identity is no longer available, the route is retained in a recovery journal under `%LOCALAPPDATA%\NetBootDhcpTool` and the next startup offers cleanup after the adapter is available again.

## Notes

Favorite records can store device name, device number, serial number, remark, account/password text, and free-form memory text. Personal favorite passwords remain protected with Windows DPAPI; manufacturer-published BMC presets are explicitly marked public and show their documented plaintext defaults or model/label guidance. Credential-free JSON export remains available. A favorite can also mark HTTPS as its preferred web scheme.

Every application action button has a small, visible-by-default round `?` help indicator beside it. Hover the action or indicator for a contextual Chinese/English explanation, or click `?` to open the same explanation in a dialog. The help control does not execute the neighboring action. Hovering the main adapter and result fields also explains what their values represent.

## Version Check

The application displays its running version when it starts. It checks the GitHub `latest.json` manifest in the background; when a newer validated release is available, `有新版本！` / `New version available!` appears beside the version. Clicking it shows the release changes and downloads the archive in the background to Downloads, with SHA-256 verification and no automatic installation. If speed remains below 3 KB/s for 10 seconds, the app only displays `1406829360@qq.com`.

## Build

Run:

```powershell
.\build\build.ps1
```

## Publish

```powershell
.\build\publish.ps1
```

The portable output is `release\NetBootDhcpTool`. Copy this folder to another Windows x64 computer and run `NetBootDhcpTool.exe`.

For maintenance, packaging, and GitHub release standards, see `docs\MAINTENANCE_GUIDE.md` and `docs\RELEASE_PROCESS.md`.

The build scripts use an existing .NET SDK when available. If no SDK is installed, they download a .NET 8 SDK into `.dotnet` under this project.

Run as administrator:

```powershell
.\build\run-app-admin.ps1
```

Check what adapters the app can enumerate:

```powershell
.\build\list-adapters.ps1
```

## DHCP Protocol Check

Without a real device, run the app as administrator, start DHCP on an isolated adapter, then run:

```powershell
.\release\NetBootDhcpTool-tools\NetBootDhcpTool.DhcpVerifier.exe 255.255.255.255
```

This sends a DHCP Discover from UDP 68 and expects a DHCP Offer from UDP 67. Use Wireshark, tshark, or Windows pktmon to capture UDP 67/68 if packet evidence is needed. The verifier is published separately so the main portable app stays smaller.

Windows built-in packet capture:

```powershell
.\build\verify-dhcp-pktmon.ps1
```

Run PowerShell as administrator. Start DHCP in the app first, then run the script. The decoded capture is written to `logs\dhcp-verify\dhcp.txt`.

## Runtime

Administrator permission is required for adapter IP changes and DHCP UDP 67. The app checks permission and restarts elevated.

DHCP must only be used on isolated test networks. Do not run it on office, production, or existing DHCP networks.

## Files

`config\appsettings.json` stores settings.
`config\favorites.json` stores manual IP favorites.
`i18n\zh-CN.json` and `i18n\en-US.json` store UI text.
`logs\yyyy-MM-dd.log` stores logs.

From v1.0.6, favorites, network history, and runtime logs are persisted in `%LOCALAPPDATA%\NetBootDhcpTool`.
Static route recovery state is also stored there temporarily while routes created by the current session exist; it is deleted after successful cleanup.
On first start after upgrade, legacy data under the app folder is migrated automatically if the new store is empty.

## Notes

The publish script uses self-contained .NET 8 so the target machine does not need .NET installed. This is larger than native tools such as Tftpd64 because WPF and .NET runtime files are included.
