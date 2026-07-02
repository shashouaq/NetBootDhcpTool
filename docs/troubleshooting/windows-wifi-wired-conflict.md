# Windows Wi-Fi And Wired Conflict

## Symptoms

- Plugging in the debug Ethernet adapter causes Wi-Fi to disconnect or stop auto-connecting.
- Manual Wi-Fi reconnect fails until the wired cable is removed.
- `Microsoft-Windows-WLAN-AutoConfig/Operational` contains messages similar to `策略禁止在该接口上自动连接`.

## What This Tool Now Does

- Only changes the user-selected target wired adapter.
- Never changes WLAN, Wi-Fi, Wireless, or `无线` adapters.
- Never writes WcmSvc wired/Wi-Fi coexistence registry policy keys.
- Never runs `netsh wlan connect`.
- Only applies `192.168.100.1/24`, no default gateway, no DNS, and metric `9000` to the selected debug adapter.

## What To Check On Windows

1. Confirm the selected debug adapter is the intended isolated test NIC.
2. Keep the debug NIC without a default gateway and without DNS.
3. Review recent WLAN events:
   - Event log: `Microsoft-Windows-WLAN-AutoConfig/Operational`
   - Look for policy or auto-connect blocking messages within the last 30 minutes.
4. If the issue persists after this tool no longer changes WLAN behavior:
   - reboot the PC once to clear stale WcmSvc connection policy cache
   - forget and reconnect the Wi-Fi profile
   - disable/enable the Wi-Fi adapter
   - disable/enable the USB wired adapter
   - replace the USB Ethernet adapter or driver if the issue reproduces only with that device

## Field Guidance

- For isolated maintenance work, prefer a dedicated USB Ethernet adapter for the device under test.
- If office Wi-Fi is business-critical, avoid bridging or Internet Connection Sharing on the debug NIC.
- If a customer image or security baseline forces wired-priority policy, clear that policy outside this tool before testing.
