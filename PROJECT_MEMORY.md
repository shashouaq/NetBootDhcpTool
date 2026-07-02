# NetBoot DHCP Tool Project Memory

## Product Requirements

- Authors shown in title/about/readme: Joel & Codex, contact 1406829360@qq.com.
- Main use case is isolated field debugging through a selected Ethernet adapter.
- Workspace root: `D:\project\NetBootDhcpTool`.
- Release script: `build\publish.ps1`.
- Release output: `release\NetBootDhcpTool-v<version>` and `release\NetBootDhcpTool-v<version>.7z`.
- Default adapter selection should prefer connected physical Ethernet, then other physical adapters, then virtual adapters.
- Show all enumerable adapters in the UI, but block unsafe DHCP startup by policy.
- DHCP startup must remain guarded by isolated-network confirmation.
- Wi-Fi, virtual adapters, disconnected adapters, and adapters with real default gateways are blocked by default.
- Treat `0.0.0.0` gateway as no usable gateway.
- Do not set the host adapter default gateway to its own DHCP server IP.
- If the client DHCP request times out, tell the user to renew IP, disable/enable the client adapter, unplug/replug the client cable, restart the client network service or device, and check cable/switch/link lights.
- Lease status must be shown bilingually in the UI, with hover help explaining `Assigned`, `Online`, and `Offline`.
- Runtime logs should include bilingual explanations for common events so Chinese users can understand packet, adapter, timeout, and cleanup steps.

## Implementation Lessons

- Some Windows pseudo interfaces throw `NetworkInformationException 10043` from `GetIPv4Properties`; skip only that adapter and keep enumerating.
- Windows PowerShell may display UTF-8 logs incorrectly unless files are created with UTF-8 BOM.
- `dotnet publish` can leave files locked if the GUI or verifier is still running; always run `build/stop-test-processes.ps1` before publish/clean.
- `pktmon` can remain active after diagnostics; stop it and clear filters before/after tests.
- A GUI process launched during Codex testing can make the IDE show `timeout waiting for child process to exit`; close/kill test GUI processes before returning.
- Hyper-V may bind DHCP on `172.22.32.1:67`; log UDP receive details and verify actual DHCP traffic reaches this tool.
- DHCP `Discover` then `Request` can produce two service events for one client; UI must dedupe by MAC, update the existing row, and serialize lease UI updates so parallel ping probes do not briefly flip one MAC from `Online` back to `Offline`.
- If logs show repeated `Discover` and `Offer` but no `Request`/`Ack`, the client did not accept or receive the Offer. On multi-NIC Windows hosts, limited broadcast `255.255.255.255` can leave through the wrong adapter; also send DHCP replies to the selected subnet broadcast address and log reply targets.
- `Ack` means DHCP acknowledgement, but users understand `Online`, `Offline`, and `Assigned` better.
- Last online time should update only when a host transitions to reachable online, not on every ping tick.
- Ping latency should keep probing assigned/offline leases until they become reachable; once online, continue low-cost periodic ping and update last-online only on offline-to-online transition.
- Manual IP scan should not set gateway or DNS; only configure local IP and subnet mask, then scan either the target IP or the whole subnet.
- Manual scan and favorites actions must disable/highlight by workflow state to prevent duplicate scans or applying favorites while a scan is running.
- Favorites require at least name, IP, and mask; optional device/account/memory/custom fields can be added or removed without blocking required network use.
- Closing cleanup must not restore an adapter just because the window opened; restore only when this app actually started DHCP during the session.
- Manual scan should not set DNS. A favorite may include a target IP; when target IP is present, applying the favorite scans only that peer, and full subnet scan is used only when target IP is empty.
- Manual scan should not set gateway either. In target-IP mode, show an immediate configured row and keep pinging until the target becomes reachable or the user stops the scan.
- Test Ethernet configuration must not break Wi-Fi or other adapters: only touch the user-selected wired debug adapter, keep it without gateway/DNS, and force a high IPv4 metric such as 9000.
- Never modify WLAN/Wi-Fi/Wireless/无线 adapters as part of DHCP or manual scan setup.
- Never write WcmSvc coexistence policy keys or `CurrentVersion\\Policies\\WcmSvc` keys from this tool.
- Never run `netsh wlan connect` from this tool.
- Before DHCP start, run read-only WLAN preflight checks for current WLAN connection, USB wired NIC presence, and recent `WLAN-AutoConfig` policy-blocked auto-connect events; if found, point users to `docs/troubleshooting/windows-wifi-wired-conflict.md`.
- PowerShell and `netsh` child-process output must use UTF-8 to avoid Chinese mojibake in logs.
- Bottom log panel should show concise summarized operation results; avoid dumping full PowerShell scripts unless an unexpected unparsed failure occurs.
- Default startup must request administrator through the application manifest instead of showing a custom elevation prompt every run. Only warn if admin privilege is unavailable.
- Favorites should support JSON import/export, include BMC management-port presets for common server vendors, and merge imports without overwriting unrelated field notes.
- Adapter summary should show both current IP and the previous IP after the tool changes an adapter address; restore original IP should be enabled by default.
- Favorite grids must remain read-only when binding computed fields such as CustomFieldsSummary; otherwise WPF may throw a TwoWay binding exception when the user clicks the cell.
- Grid headers should switch by language instead of showing bilingual labels simultaneously. Favorite columns should support drag reorder and a checkbox-based show/hide dialog.
- DHCP gateway and DNS should be optional hidden fields by default; do not emit router/DNS DHCP options unless the user explicitly adds values.
- Switching between Auto DHCP and Manual IP/Scan should remind the user to click Refresh and re-confirm adapter state before continuing.
- Open/close operations should show a wait overlay so users know adapter cleanup or startup enumeration is still running.
- WPF `ApplicationIcon` must point to a real ICO saved by `Icon.Save`; PNG bytes inside an `.ico` file can make resource compilation fail.
- UI language resources and XAML labels must be checked for mojibake before release, especially Chinese strings edited through PowerShell.
- On exit, persist favorites before cleanup starts, and never delete/overwrite favorites or runtime logs as part of close flow.
- Favorites and logs must use a durable per-user path (`%LOCALAPPDATA%\NetBootDhcpTool`) with one-time migration from legacy app-folder data.

## Validation Checklist

- Build: `dotnet build .\NetBootDhcpTool.sln -c Release --no-restore`
- Unit smoke test: `dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release --no-build`
- Adapter view: `.\build\list-adapters.ps1`
- Publish: `.\build\publish.ps1`
- Cleanup stuck test processes: `.\build\stop-test-processes.ps1`
- DHCP packet capture: `.\build\verify-dhcp-pktmon.ps1`

## Future Maintenance

- Keep business logic out of `MainWindow.xaml.cs` when features grow; move lease view updates, favorites, and settings into ViewModels.
- Consider DPAPI encryption for stored passwords before production use.
- Consider import/export for favorite memory records.
- Consider optional HTTPS default behavior per favorite/device.
- If Wi-Fi coexistence complaints reappear, inspect `Microsoft-Windows-WLAN-AutoConfig/Operational` before changing adapter code again; this tool should remain read-only toward WLAN state.
