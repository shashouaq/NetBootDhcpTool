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
- Adapter restart is a selected-adapter operation implemented as disable/enable. The UI may target wireless, virtual, disconnected, or other adapters only when the corresponding settings switch is enabled; the action requires confirmation and logs identity/result.
- MAC changes support validated manual input and locally administered random MAC generation. Each adapter's original MAC is captured before the first change in a session; the per-change restore checkbox defaults on and restoration runs only during normal application exit. Forced termination/crash restoration is not guaranteed.
- If the client DHCP request times out, tell the user to renew IP, disable/enable the client adapter, unplug/replug the client cable, restart the client network service or device, and check cable/switch/link lights.
- Lease status must be shown bilingually in the UI, with hover help explaining `Assigned`, `Online`, and `Offline`.
- Runtime logs should include bilingual explanations for common events so Chinese users can understand packet, adapter, timeout, and cleanup steps.
- Future changes must prioritize Chinese/English bilingual support for all user-visible UI, dialogs, status messages, logs, help text, and maintenance documentation; add language-resource keys instead of hardcoding one language when practical.
- Every application button, including buttons created at runtime, must have a separate compact round `?` help button beside it. Keep it small and visible by default; hovering the action or indicator shows contextual bilingual guidance, while clicking `?` opens the same explanation and never triggers the neighboring business action. Use `HelpButtonService` for new controls.

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
- Favorites "Load configuration" only populates the manual-scan fields and records usage; "Apply and scan" reuses that population step and performs one configuration plus one scan. Keep this separation and do not add a second scan or second configuration pass.
- Favorites require at least name, IP, and mask; optional device/account/memory/custom fields can be added or removed without blocking required network use.
- Closing cleanup must not restore an adapter just because the window opened; restore only when this app actually started DHCP during the session.
- Manual scan should not set DNS. A favorite may include a target IP; when target IP is present, applying the favorite scans only that peer, and full subnet scan is used only when target IP is empty.
- Manual scan should not set gateway either. In target-IP mode, show an immediate configured row and keep pinging until the target becomes reachable or the user stops the scan.
- Test Ethernet configuration must not break Wi-Fi or other adapters: only touch the user-selected wired debug adapter, keep it without gateway/DNS, and force a high IPv4 metric such as 9000.
- Never modify WLAN/Wi-Fi/Wireless/无线 adapters as part of DHCP or manual scan setup. The separately-confirmed restart and MAC features may target them only when their settings switches are enabled.
- Never write WcmSvc coexistence policy keys or `CurrentVersion\\Policies\\WcmSvc` keys from this tool.
- Never run `netsh wlan connect` from this tool.
- Before DHCP start, run read-only WLAN preflight checks for current WLAN connection, USB wired NIC presence, and recent `WLAN-AutoConfig` policy-blocked auto-connect events; if found, point users to `docs/troubleshooting/windows-wifi-wired-conflict.md`.
- PowerShell and `netsh` child-process output must use UTF-8 to avoid Chinese mojibake in logs.
- Bottom log panel should show concise summarized operation results; avoid dumping full PowerShell scripts unless an unexpected unparsed failure occurs.
- Default startup must request administrator through the application manifest instead of showing a custom elevation prompt every run. Only warn if admin privilege is unavailable.
- Favorites should support JSON import/export, include BMC management-port presets for common server vendors, and merge imports without overwriting unrelated field notes.
- Manufacturer-published BMC presets are marked as public defaults and may show documented plaintext credentials; model-dependent values must point to the chassis label/manual. Personal favorites continue to use DPAPI on disk, and credential-free export remains available.
- Adapter summary should show both current IP and the previous IP after the tool changes an adapter address; restore original IP should be enabled by default.
- Favorite grids must remain read-only when binding computed fields such as CustomFieldsSummary; otherwise WPF may throw a TwoWay binding exception when the user clicks the cell.
- Grid headers should switch by language instead of showing bilingual labels simultaneously. Favorite columns should support drag reorder and a checkbox-based show/hide dialog.
- Keep `Defaults.cs`, `i18n\zh-CN.json`, and `i18n\en-US.json` key sets identical; use a lightweight key-set audit before handoff.
- DHCP gateway and DNS should be optional hidden fields by default; do not emit router/DNS DHCP options unless the user explicitly adds values.
- Switching between Auto DHCP and Manual IP/Scan should remind the user to click Refresh and re-confirm adapter state before continuing.
- Open/close operations should show a wait overlay so users know adapter cleanup or startup enumeration is still running.
- Update manifests carry `releaseNotes` and `changes` extracted from the `Unreleased` section of `docs/FEATURE_CHANGELOG.md`. Clicking a validated new-version link shows those changes and downloads the archive in the background without installation; the archive is checksum-verified and a sustained below-3-KB/s-for-10-seconds condition only displays the author email.
- Primary UI controls, cards, dialogs, inputs, and containers use rounded corners while virtualized DataGrid surfaces retain their performance-oriented template.
- WPF `ApplicationIcon` must point to a real ICO saved by `Icon.Save`; PNG bytes inside an `.ico` file can make resource compilation fail.
- UI language resources and XAML labels must be checked for mojibake before release, especially Chinese strings edited through PowerShell.
- On exit, persist favorites before cleanup starts, and never delete/overwrite favorites or runtime logs as part of close flow.
- Favorites and logs must use a durable per-user path (`%LOCALAPPDATA%\NetBootDhcpTool`) with one-time migration from legacy app-folder data.
- Static routes are session-owned: accept multiple IPv4 destination prefixes, normalize host bits to the network boundary, use a blank gateway for direct routes (`0.0.0.0` at the Windows API boundary), and use a nonblank IPv4 gateway for next-hop routes.
- The static-route page initially displays all existing IPv4 routes across local interfaces as current-state indications, including Local/DHCP/virtual-interface routes with protocol labels; it does not require the top adapter selection and does not automatically apply anything.
- For a large existing-route set, keep the route grid display template lightweight: show the adapter name as text and create the adapter ComboBox only in `CellEditingTemplate`; retain row/column virtualization and recycling. A local frontend run with 47 routes measured 339 ms for the first route-tab render and 27 ms on repeat after this change.
- Static route application must refuse WLAN, virtual, disconnected, and ambiguous adapter targets; do not overwrite a matching non-system route. Allow `0.0.0.0/0` only with an explicit extra confirmation because it changes the default IPv4 path.
- Record every route created by this process with adapter identity, interface index, next hop, metric, policy store, and instance ID. On exit remove only those recorded routes; if cleanup fails or adapter identity changes, retain the recovery journal and offer cleanup on next startup.

## Validation Checklist

- Build: `dotnet build .\NetBootDhcpTool.sln -c Release --no-restore`
- Unit smoke test: `dotnet run --project .\src\NetBootDhcpTool.Tests\NetBootDhcpTool.Tests.csproj -c Release --no-build`
- Adapter view: `.\build\list-adapters.ps1`
- Publish: `.\build\publish.ps1`
- Cleanup stuck test processes: `.\build\stop-test-processes.ps1`
- DHCP packet capture: `.\build\verify-dhcp-pktmon.ps1`
- Static route service smoke test: run `.\build\route-smoke.ps1` as administrator; it creates two uniquely named Hyper-V internal switches, tests direct and gateway routes, then removes the switches in `finally`.

## Change and publication boundary

- Unless the user explicitly requests publication/release, after each completed change build the project and open the local program for preview; do not push source or release artifacts externally.
- The preview step is mandatory after every completed change: check for a running `NetBootDhcpTool` process, restart it from the current source `Release` output when present, otherwise start it. `build\run-app-admin.ps1` prefers `src\NetBootDhcpTool.App\bin\Release\net8.0-windows` and falls back to the packaged release output only when the source build is absent.
- Only explicit publication authorization permits pushing to GitHub or creating/updating a GitHub Release. After publication, verify and report the exact remote commit/release/assets; local success must never be described as GitHub publication.
- Every application run creates a new UTF-8 session log named with its start timestamp; user button/tab actions, slow-operation start/completion, elapsed time, and errors must be logged so UI freezes can be diagnosed from one run without mixing historical sessions.

## Future Maintenance

- Collection state used by the main window is now held by `MainWindowViewModel`; keep new UI state out of the code-behind where practical.
- Favorite passwords use current-user Windows DPAPI; keep exports credential-free and ask the user to re-enter a password when a different Windows identity cannot decrypt it.
- Favorite JSON import/export and per-favorite HTTPS preference are implemented; preserve the credential-free export boundary.
- The in-app version check only opens the validated direct GitHub download. Do not add automatic installation without a signed-update and rollback design.
- If Wi-Fi coexistence complaints reappear, inspect `Microsoft-Windows-WLAN-AutoConfig/Operational` before changing adapter code again; this tool should remain read-only toward WLAN state.
