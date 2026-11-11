# Hotspot Watchdog

Windows tray app that keeps the laptop's Mobile Hotspot alive.

- Turn the hotspot on/off from the tray, a small dashboard, or Ctrl+Alt+H
- Watchdog: turns it back on after it drops (sleep, Wi-Fi reconnect, Windows auto-off), retrying less and less often if it keeps failing
- Restarts it when the shared network changes
- Connected devices with IP/MAC, nicknames, ADB (5555) check, join/leave notifications
- Keeps the PC awake while devices are connected
- Edit SSID / password / band, start with Windows

## Build
Run `build.cmd`. It uses the .NET Framework 4.x compiler built into Windows, so there's nothing to install.
Single file, C# 5. Reaches the WinRT hotspot API (`Windows.Networking.NetworkOperators`) through reflection, so no Windows SDK is needed.

## Data
Settings and log: `%APPDATA%\HotspotWatchdog\`
