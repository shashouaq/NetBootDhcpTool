# NetBoot DHCP Tool

Version: 1.0.6

Authors: Joel & Codex

Windows green portable IPv4 DHCP, adapter IP configuration, ping scan, web open, favorites, bilingual UI, and logs.

## Notes

Favorite records can store device name, device number, serial number, remark, account/password text, and free-form memory text. Passwords are currently stored in local JSON as plain text; keep the portable folder private.

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
On first start after upgrade, legacy data under the app folder is migrated automatically if the new store is empty.

## Notes

The publish script uses self-contained .NET 8 so the target machine does not need .NET installed. This is larger than native tools such as Tftpd64 because WPF and .NET runtime files are included.
