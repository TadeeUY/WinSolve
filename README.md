# WinSolve

All-in-one Windows 10/11 maintenance tool: diagnostics, repair, cleanup, drivers and one-click optimization.

## Features

| Area | What it does |
| --- | --- |
| **Home** | Health score, hardware summary and a list of detected problems, each with a fix button. |
| **One-click optimization** | Desktop / Laptop profiles with *Light*, *Balanced* and *Maximum performance* levels. The plan adapts to the detected hardware (SSD vs HDD, RAM, GPU vendor, battery). A restore point is created first. |
| **Cleanup & repair** | Temp files, Windows Update cache, DISM + SFC, CHKDSK, Windows Update reset, network stack reset, Store / Start menu repair, Defender scans... |
| **Disk space** | WizTree-style analyzer: parallel scan, squarified treemap colored by file type, folder tree, file type breakdown, duplicate file finder, delete to Recycle Bin. |
| **Hardware** | CrystalDiskInfo-style S.M.A.R.T. health (temperature, wear, power-on hours, raw attributes), devices with errors (restart / reinstall), RAM modules, battery health, critical events (BSOD, WHEA, unexpected shutdowns). |
| **Drivers** | Detects NVIDIA / AMD / Intel GPUs and the installed version, looks up the latest NVIDIA Game Ready driver, downloads it and performs a **clean install** (removes every old display driver package first). AMD Adrenalin clean install from the official installer. Windows Update driver updates, device repair and driver backup. |
| **Tweaks** | Wintoys-style reversible privacy, performance and interface settings. |
| **Startup apps** | Registry, Startup folder and logon scheduled tasks. Disable (reversible) or remove. |
| **Apps** | Uninstall or **force uninstall** desktop programs (silent uninstall + leftover folders, shortcuts and registry) and Microsoft Store apps (all users + deprovisioned). |
| **Activation** | License status, activation with the OEM key stored in the firmware or a product key, Microsoft's activation troubleshooter. |
| **Monitor** | Live CPU, RAM, GPU, disk and network usage with charts; GPU temperature (same source as Task Manager) and the ACPI CPU temperature when the firmware reports it. |
| **Updates & maintenance** | Checks GitHub Releases at startup and every 4 hours; one-click "Update now" bar (or fully automatic updates), SHA-256 verified. Optional scheduled background cleanup (daily/weekly/monthly). Before/after comparison of every optimization. Bug report zip from Settings. |
| **Languages** | English and Spanish (Settings → Language). |
| **Error alerts** | Watches the event log in the background and pops up an alert with **Fix** / **Close** when Windows reports an error. Runs from the notification area. |

## Install

Download `WinSolveSetup.exe` from the [Releases](../../releases) page (or the latest build artifact under **Actions**).
The installer lets you choose **Only for me** or **For all users**, offers a clean install (removes previous settings) and installs the .NET 8 Desktop Runtime if it is missing.

`WinSolve.exe` alone is a ~2 MB single file that needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).

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
