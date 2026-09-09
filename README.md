# NetBoot DHCP Tool

Version: 1.0.10

Authors: Joel & Codex

Windows green portable IPv4 DHCP, adapter IP configuration, IPv4/IPv6 static route rules, adapter restart/MAC tools, ping scan, web open, favorites, bilingual UI, contextual `?` help buttons, and logs.

## Static Routes

The Static Routes tab accepts multiple temporary IPv4/IPv6 rules. Enter a single address or CIDR network; a single address is normalized to `/32` or `/128`. Select any local adapter, enter an on-link gateway for a next-hop route, or leave Gateway blank for a direct route. The same normalized prefix may be assigned to different adapters, while duplicate use of one prefix on one adapter is blocked. Route metrics are calculated from the current interface metrics and the preview shows the effective same-prefix winner. Different prefixes may overlap; Windows longest-prefix matching is shown in the preview. More-specific existing or planned routes block a broader rule when they use a different path, while existing routes are never overwritten or removed. IPv4 and IPv6 default routes (`0.0.0.0/0` and `::/0`) are not allowed.

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
