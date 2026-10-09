# Security review

WinSolve always runs with administrator rights, so the main threat is a **non-elevated
program on the same PC tricking WinSolve into doing something privileged on its behalf**
(local privilege escalation). Every place where WinSolve consumes data that a standard
user can control (HKCU registry, files under the user profile, `%TEMP%`, downloads,
the current directory) was reviewed.

## Findings fixed

| # | Severity | Issue | Fix |
|---|----------|-------|-----|
| 1 | High | **Uninstall command injection / elevation.** `UninstallString` values were run through `cmd.exe /c` as administrator. Per-user (HKCU) entries can be written by any program the user runs, so a fake entry meant code execution as admin. | Commands are parsed and started directly (no shell). MSI entries only accept a validated `{GUID}`. HKCU uninstallers run with a non-admin token (`runas /trustlevel:0x20000`). |
| 2 | High | **Arbitrary recursive delete.** Force uninstall deleted `InstallLocation` from the registry; a crafted value (e.g. `C:\Users\Someone`, `C:\Program Files\Windows Defender`) would be wiped. | `SafePath.IsProtectedFolder` blocks Windows, Program Files roots, shell folders and profile roots (and their ancestors). Per-user entries may only delete inside the current profile. The exact folders are listed in the confirmation dialog before anything is deleted. |
| 3 | High | **Junction redirection in cleanup.** Cleaning user-writable folders (`%TEMP%`, `AppData\...\WER`, browser caches, crash dumps) as admin followed a planted junction into e.g. `C:\Windows\System32`. | Cleanup refuses any folder that is, or is inside, a junction/symlink and never recurses into reparse points. The same applies to force-uninstall deletes and delete-on-reboot. |
| 4 | High | **Driver installer TOCTOU.** Driver installers were downloaded to `%TEMP%` and run elevated; a non-elevated process could swap the file between the signature check and execution. | Downloads go to a fresh, randomly named folder under `%ProgramData%\WinSolve` that only Administrators/SYSTEM can write. Files picked by the user are copied there first. The file stays open (read-only sharing) from signature verification until the installer exits. |
| 5 | Medium | **Weak signer check.** The vendor check was a substring match on the certificate subject. | In-process `WinVerifyTrust` (with revocation checking) plus an exact match of the certificate CN/O against a per-vendor allow list. Downloads are only accepted from `https://*.nvidia.com`, including after redirects. |
| 6 | Medium | **Binary planting of Windows tools.** `cmd`, `powershell`, `pnputil`, `sfc`, `explorer`... were started by bare name, and Windows searches the application folder and current directory first. With a per-user install (user-writable folder) or the installer run from Downloads, a planted `cmd.exe` would run elevated. | All tools are resolved to full `System32`/Windows paths, the working directory is set to `System32`, `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32)` is called at startup and P/Invoke is restricted to `System32` (app and installer). |
| 7 | Medium | **Silent elevation through "Start with Windows".** The elevated logon task would run `WinSolve.exe` from a per-user install folder, which any program can overwrite → admin rights without a UAC prompt. | Start with Windows is only allowed when WinSolve runs from Program Files (all-users install). The installer now recommends and defaults to all-users. |
| 8 | Medium | **Elevated writes into the user profile.** Logs, settings and the battery report were written as admin under `%AppData%`/`%LocalAppData%`/`%TEMP%`, where links can redirect writes to arbitrary files. | Logs and settings moved to `%ProgramData%\WinSolve` (Administrators/SYSTEM full control, Users read). Reports go to an admin-only folder. Old settings are read once for migration. |
| 9 | Low | Startup-folder entries were enumerated/deleted through a possibly linked Startup folder. | Linked Startup folders are ignored and removal refuses to delete through a link. |
| 10 | Low | Device instance IDs were placed in a quoted `pnputil` argument without validation. | IDs containing quotes or control characters are rejected. |
| 11 | Low | Installer runtime bootstrap: predictable temp file name, no revocation check, substring signer match. | Random file name, file locked until the runtime installer exits, revocation checking, exact `Microsoft Corporation` signer match. |

Fixed in 0.8.0:

- **Junction swap during cleanup:** cleanup and force-uninstall deletions are handle-based (`Core/SafeDelete.cs`). Each entry is opened relative to its parent's handle (`NtCreateFile` with `RootDirectory` and `FILE_OPEN_REPARSE_POINT`) and deleted through that handle, and the root must resolve (`GetFinalPathNameByHandle`) to the expected path. A folder swapped for a junction mid-run is opened as the link itself and skipped, never followed. Profile folders are no longer queued for deletion at reboot.
- **Over-the-shoulder elevation:** when a standard user elevates with an administrator's password, HKCU, %TEMP%, AppData, Startup, Start Menu, per-user uninstallers, Store app removal and Explorer restarts now target the signed-in user (`Core/InteractiveUser.cs`: `HKEY_USERS\<SID>`, their profile folders, and `CreateProcessWithTokenW` with a token from their own session processes) instead of the administrator's account.
- **Updates:** the new `WinSolve.exe` is written to a temporary file and swapped in with `File.Replace` (the old version survives any failure); setup waits for the running WinSolve to exit instead of killing it, updates the folder the running copy came from, and reopens the previous version if an automatic update fails.

## Reviewed and considered safe

- **PowerShell scripts** that embed data (Store package names, scheduled task names, file paths) quote it as single-quoted literals with `'` doubled.
- **Activation:** product keys are validated against `XXXXX-XXXXX-XXXXX-XXXXX-XXXXX` before reaching `slmgr`.
- **Settings JSON** only contains booleans, enums, colors and ids from the built-in catalogs; nothing in it is executed.
- **Network:** only HTTPS to `nvidia.com` (driver lookup/download) and, in the installer, `aka.ms` → Microsoft's .NET download; TLS validation is never disabled. XML/JSON parsing uses the default (DTD-prohibited) settings.
- **Error alerts:** event log text from other programs is only displayed. The **Fix** button only runs fixed tasks from the built-in catalog.
- **Disk space delete** moves to the Recycle Bin, is always user-initiated and warns for system/program folders; `pagefile.sys`/`hiberfil.sys` are blocked.

## Residual risks

- **Per-user installs:** `%LocalAppData%\Programs\WinSolve` is writable by the user, so other programs running as that user could replace `WinSolve.exe` before you launch it (you would still see a UAC prompt for an unsigned app). Prefer the **All users** install.
- **WinSolve itself is not code-signed.** Users cannot verify the publisher in the UAC prompt. Signing releases with a code-signing certificate is recommended.
- **Force uninstall is destructive by design.** It runs the program's own uninstaller (vendor code) and deletes the listed folders; review the list before confirming.
