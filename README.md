# WinSolve

**The multitool for Windows** · *La navaja suiza para Windows*

All-in-one Windows 10/11 maintenance tool: diagnostics, repair, cleanup, drivers, one-click optimization and a toolbox of everyday utilities. Press **Ctrl+K** anywhere to find any page, task, tweak or tool.

## Features

| Area | What it does |
| --- | --- |
| **Home** | Health score, hardware summary and a list of detected problems, each with a fix button. |
| **One-click optimization** | Desktop / Laptop profiles with *Light*, *Balanced* and *Maximum performance* levels. The plan adapts to the detected hardware (SSD vs HDD, RAM, GPU vendor, battery). A restore point is created first. |
| **Cleanup & repair** | Temp files, Windows Update cache, DISM + SFC, CHKDSK, Windows Update reset, network stack reset, Store / Start menu repair, Defender scans... |
| **Disk space** | WizTree-style analyzer: parallel scan, squarified treemap colored by file type, folder tree, file type breakdown, duplicate file finder, delete to Recycle Bin. |
| **Hardware** | CrystalDiskInfo-style S.M.A.R.T. health (temperature, wear, power-on hours, raw attributes) with a per-volume repair panel (CHKDSK check, file system repair, bad sector scan and isolation, with advice based on the drive's health), devices with errors (restart / reinstall), RAM modules, battery health, critical events (BSOD, WHEA, unexpected shutdowns). Background alerts when a drive's health gets worse, its SSD life drops below 20% / 10%, it wears out unusually fast or runs hot. |
| **Drivers** | Detects NVIDIA / AMD / Intel GPUs and the installed version, looks up the latest NVIDIA Game Ready driver, downloads it and performs a **clean install** (removes every old display driver package first). AMD Adrenalin clean install from the official installer. Windows Update driver updates, device repair and driver backup. |
| **Tweaks** | Laid out like Chris Titus Tech's WinUtil: Essential and Advanced tweaks with Standard/Minimal presets, Run and Undo, instant preference switches, Ultimate Performance plan, DNS presets (Cloudflare, Google, Quad9, AdGuard, OpenDNS), Windows Update policy (Default / Security) and optional Windows features (WSL, Hyper-V, Sandbox, .NET 3.5). |
| **Toolbox** | Disk speed test (sequential and 4K), locked file finder (what's using a file, close it or delete it at restart), secure delete, network test (ping, download/upload speed, public IP), saved Wi-Fi passwords, hosts file editor with site blocking, right-click menu cleanup, restore point manager, battery report (capacity left, charge cycles, runtime and capacity history) and a shareable PC report (PDF spec sheet, no serial numbers or keys). |
| **Windows Update** | Pause updates for 1, 2 or 5 weeks and resume, hide a problem update so it stops reinstalling (and show it again), full update history with failures highlighted, roll back a device's driver to the previous one. |
| **Command palette** | **Ctrl+K** searches pages, maintenance tasks, tweaks and tools in English and Spanish, accent-insensitive. |
| **Notification area** | Quick actions from the tray icon: clean temporary files, free up memory (standby list), flush DNS, restart Explorer. |
| **Startup apps** | Registry, Startup folder and logon scheduled tasks. Disable (reversible) or remove. |
| **Apps** | Uninstall or **force uninstall** desktop programs (silent uninstall + leftover folders, shortcuts and registry) and Microsoft Store apps (all users + deprovisioned). |
| **Activation** | License status, activation with the OEM key stored in the firmware or a product key, Microsoft's activation troubleshooter. |
| **Monitor** | Live CPU, RAM, GPU, disk and network usage with charts; GPU temperature (same source as Task Manager) and the ACPI CPU temperature when the firmware reports it. |
| **Updates & maintenance** | Checks GitHub Releases at startup and every 4 hours; one-click "Update now" bar (or fully automatic updates), SHA-256 verified. Optional scheduled background cleanup (daily/weekly/monthly). Before/after comparison of every optimization. Bug report zip from Settings. |
| **Languages & look** | English and Spanish, dark or light theme (or the same as Windows), a short welcome tour on first start. |
| **Error alerts** | Watches the event log in the background and pops up an alert with **Fix** / **Close** when Windows reports an error. Runs from the notification area. |

## Install

Download `WinSolveSetup.exe` from the [Releases](../../releases) page (or the latest build artifact under **Actions**).
The installer lets you choose **Only for me** or **For all users**, offers a clean install (removes previous settings) and installs the .NET 8 Desktop Runtime if it is missing.

**Portable:** `WinSolve-Portable.exe` runs without installing anything, for example from a USB stick. It carries its own .NET runtime (bigger download) and leaves nothing scheduled on the PC: no Start with Windows, no scheduled maintenance and no self-installing updates.


## Build

```
dotnet publish src/WinSolve/WinSolve.csproj -c Release -o out
```

The UI is Windows Forms written entirely in code, so it builds on Windows, Linux or CI (`EnableWindowsTargeting`).

## Tests

```
dotnet test tests/WinSolve.Tests
```

Tests run on Windows (CI runs them on every push).

## Notes

- WinSolve requires administrator rights (most repairs do).
- Every system change is logged to `%ProgramData%\WinSolve\logs`.
- Settings live in `%ProgramData%\WinSolve\settings.json`.
- See [SECURITY.md](SECURITY.md) for the security review.
