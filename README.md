# Hotspot Watchdog

Windows tray app that keeps the laptop's Mobile Hotspot alive.

> Built and tested with Claude's help (Anthropic's AI). I designed it, use it daily on Windows 11 25H2, and maintain it.

- Turn the hotspot on/off from the tray, a small dashboard, or Ctrl+Alt+H
- Watchdog: turns it back on after it drops (sleep, Wi-Fi reconnect, Windows auto-off), retrying less and less often if it keeps failing
- Restarts it when the shared network changes
- Connected devices with IP/MAC, nicknames, ADB (5555) check, join/leave notifications
- Keeps the PC awake while devices are connected
- Edit SSID / password / band, start with Windows

## What it does / doesn't do
**Does**
- Uses the Windows Mobile Hotspot API (`Windows.Networking.NetworkOperators`) to read, start and stop the hotspot
- Reads the local ARP table to show connected devices' IPs
- Writes one registry value only if you enable *Start with Windows* (`HKCU\...\Run`)
- Stores settings and a log in `%APPDATA%\HotspotWatchdog\`

**Doesn't**
- Need admin rights
- Connect to the internet, send telemetry, or update itself
- Ship a prebuilt `.exe` — you compile it yourself from the single source file with `build.cmd`

## Build
Run `build.cmd`. It uses the .NET Framework 4.x compiler built into Windows, so there's nothing to install.
Single file, C# 5. Reaches the WinRT hotspot API (`Windows.Networking.NetworkOperators`) through reflection, so no Windows SDK is needed.

## Data
Settings and log: `%APPDATA%\HotspotWatchdog\`
