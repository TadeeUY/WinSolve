using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace WinSolve.Setup
{
    public sealed class InstallOptions
    {
        public bool AllUsers;
        public bool Clean;
        public bool DesktopShortcut = true;
        public bool Launch = true;
        /// <summary>Update in place: the folder of the running installation (set by WinSolve's updater).</summary>
        public string UpdateDir;
        /// <summary>Process id of the WinSolve that started the update; setup waits for it to exit.</summary>
        public int WaitPid;
        /// <summary>"es" or "en" picked in the installer; WinSolve opens in it. Null keeps WinSolve's own setting.</summary>
        public string Language;

        public string ToArgs() =>
            "--install" + (AllUsers ? " --allusers" : "") + (Clean ? " --clean" : "") +
            (DesktopShortcut ? " --desktop" : "") + (Launch ? " --launch" : "") +
            (UpdateDir != null ? " --update-dir \"" + UpdateDir + "\"" : "") + (WaitPid > 0 ? " --wait " + WaitPid : "") +
            (Language != null ? " --lang " + Language : "");

        public static InstallOptions FromArgs(string[] args)
        {
            string Value(string name)
            {
                var i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
            }
            int.TryParse(Value("--wait"), out var pid);
            return new InstallOptions
            {
                AllUsers = args.Contains("--allusers"),
                Clean = args.Contains("--clean"),
                DesktopShortcut = args.Contains("--desktop"),
                Launch = args.Contains("--launch"),
                UpdateDir = Value("--update-dir"),
                WaitPid = pid,
                Language = Value("--lang") is string l && (l == "es" || l == "en") ? l : null,
            };
        }
    }

    /// <summary>Install / uninstall logic, independent of the UI.</summary>
    public static class Installer
    {
        public const string AppName = "WinSolve";
        public const string ExeName = "WinSolve.exe";
        public const string UninstallerName = "Uninstall WinSolve.exe";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinSolve";
        private const string RuntimeUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";

        public static string Version =>
            Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public static bool IsElevated =>
            new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        public static string PerUserDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

        public static string AllUsersDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);

        public static string InstallDir(bool allUsers) => allUsers ? AllUsersDir : PerUserDir;

        private static RegistryKey Hive(bool allUsers) =>
            RegistryKey.OpenBaseKey(allUsers ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

        private static string StartMenuDir(bool allUsers) =>
            Environment.GetFolderPath(allUsers ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs);

        private static string DesktopDir(bool allUsers) =>
            Environment.GetFolderPath(allUsers ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory);

        // ═══════════════════ .NET runtime ═══════════════════

        public static bool IsRuntimeInstalled()
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
                using (var key = hklm.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App"))
                {
                    if (key != null && key.GetValueNames().Any(v => v.StartsWith("8.", StringComparison.Ordinal))) return true;
                }
            }
            catch { }

            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            return Directory.Exists(dir) && Directory.GetDirectories(dir).Any(d => Path.GetFileName(d).StartsWith("8.", StringComparison.Ordinal));
        }

        /// <summary>Downloads the official .NET 8 Desktop Runtime from Microsoft and installs it silently.</summary>
        public static void InstallRuntime(Action<string> status, Action<int> progress, CancellationToken ct)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)12288 /* TLS 1.3 */;
            // Unpredictable name, created fresh, so nothing can be planted in its place.
            var file = Path.Combine(Path.GetTempPath(), "WinSolve-dotnet-" + Guid.NewGuid().ToString("N") + ".exe");
            status(Lang.T("Downloading the .NET 8 Desktop Runtime from Microsoft..."));
            using (var wc = new WebClient())
            {
                wc.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage);
                using (ct.Register(wc.CancelAsync))
                    wc.DownloadFileTaskAsync(new Uri(RuntimeUrl), file).GetAwaiter().GetResult();
            }

            // Keep the file open (read sharing only) from verification until it has run,
            // so it cannot be replaced in between.
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!Signature.IsSignedBy(file, "Microsoft Corporation"))
                    throw new InvalidOperationException("The downloaded .NET runtime is not signed by Microsoft. Installation stopped.");

                status(Lang.T("Installing the .NET 8 Desktop Runtime..."));
                var psi = new ProcessStartInfo(file, "/install /quiet /norestart") { UseShellExecute = true };
                if (!IsElevated) psi.Verb = "runas";
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    // 0 = OK, 3010 = OK but restart required, 1638 = newer version already installed
                    if (p.ExitCode != 0 && p.ExitCode != 3010 && p.ExitCode != 1638)
                        throw new InvalidOperationException($".NET runtime setup failed with exit code {p.ExitCode}.");
                }
            }
            TryDelete(file);
        }

        // ═══════════════════ Install ═══════════════════

        public static void Install(InstallOptions o, Action<string> status, Action<int> progress, CancellationToken ct)
        {
            progress(5);
            if (!IsRuntimeInstalled())
                InstallRuntime(status, p => progress(5 + p * 60 / 100), ct);
            progress(65);

            status(Lang.T("Closing WinSolve if it is running..."));
            // The updater passes its own process id: let it exit by itself (tray icon, settings).
            if (o.WaitPid > 0)
            {
                try { using (var p = Process.GetProcessById(o.WaitPid)) p.WaitForExit(30000); }
                catch (ArgumentException) { /* already gone */ }
            }
            KillRunning();

            if (o.Clean)
            {
                status(Lang.T("Removing the previous installation and settings..."));
                RemoveInstallation(allUsers: false, removeData: true, status);
                if (IsElevated) RemoveInstallation(allUsers: true, removeData: true, status);
            }
            progress(75);

            // An update goes into the folder the running copy came from. Under "over-the-shoulder"
            // elevation (a standard user typing an admin password) this process sees the admin's
            // profile, so the per-user folder computed here would be the wrong account's.
            var updateDir = ValidUpdateDir(o.UpdateDir);
            var dir = updateDir ?? InstallDir(o.AllUsers);
            status(Lang.F("Copying files to {0}...", dir));
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, ExeName);
            // Write next to the old file, then swap: a failure (disk full, file locked) leaves the
            // previous version working instead of a truncated WinSolve.exe.
            ReplaceFile(exe, fs =>
            {
                using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(ExeName))
                    res.CopyTo(fs);
            });

            // The installer doubles as the uninstaller.
            var uninstaller = Path.Combine(dir, UninstallerName);
            ReplaceFile(uninstaller, fs =>
            {
                using (var self = File.OpenRead(Assembly.GetExecutingAssembly().Location))
                    self.CopyTo(fs);
            });
            progress(85);

            if (updateDir != null && UpdateRegistration(updateDir, exe))
            {
                // Shortcuts and the uninstall entry already exist for the right account.
                progress(100);
                status(Lang.T("WinSolve was updated successfully."));
                return;
            }

            status(Lang.T("Creating shortcuts..."));
            Shortcuts.Create(Path.Combine(StartMenuDir(o.AllUsers), AppName + ".lnk"), exe, dir, "Windows diagnostics, repair and optimization");
            if (o.DesktopShortcut)
                Shortcuts.Create(Path.Combine(DesktopDir(o.AllUsers), AppName + ".lnk"), exe, dir, "Windows diagnostics, repair and optimization");

            status(Lang.T("Registering the uninstaller..."));
            using (var root = Hive(o.AllUsers))
            using (var key = root.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", AppName);
                key.SetValue("DisplayIcon", exe);
                key.SetValue("InstallLocation", dir);
                key.SetValue("UninstallString", $"\"{uninstaller}\" --uninstall" + (o.AllUsers ? " --allusers" : ""));
                key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --uninstall --quiet" + (o.AllUsers ? " --allusers" : ""));
                key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
            progress(100);
            status(Lang.T("WinSolve was installed successfully."));
        }

        public static void LaunchApp(InstallOptions o)
        {
            var exe = Path.Combine(ValidUpdateDir(o.UpdateDir) ?? InstallDir(o.AllUsers), ExeName);
            try { Process.Start(new ProcessStartInfo(exe, o.Language != null ? "--lang " + o.Language : "") { UseShellExecute = true }); } catch { /* UAC declined */ }
        }

        // ═══════════════════ Uninstall ═══════════════════

        public static void Uninstall(bool allUsers, bool removeData, Action<string> status)
        {
            KillRunning();
            RemoveInstallation(allUsers, removeData, status);
        }

        private static void RemoveInstallation(bool allUsers, bool removeData, Action<string> status)
        {
            var dir = InstallDir(allUsers);
            TryDelete(Path.Combine(StartMenuDir(allUsers), AppName + ".lnk"));
            TryDelete(Path.Combine(DesktopDir(allUsers), AppName + ".lnk"));

            try
            {
                using (var root = Hive(allUsers)) root.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            }
            catch { }

            // Scheduled task created by WinSolve's "Start with Windows" option.
            RunHidden("schtasks.exe", "/delete /f /tn \"WinSolve\"");
            if (IsElevated) RestorePendingSettings();

            if (removeData)
            {
                TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName));
                TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));
                if (IsElevated) TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppName));
            }

            if (!Directory.Exists(dir)) return;
            var self = Assembly.GetExecutingAssembly().Location;
            if (self.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                // We are running from the folder being removed: delete it after this process exits.
                status(Lang.T("Files will be removed when the uninstaller closes."));
                // Wait for this process to really exit (the user may leave the final message open),
                // then remove the folder.
                var pid = Process.GetCurrentProcess().Id;
                var script = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1; " +
                             $"Remove-Item -LiteralPath '{dir.Replace("'", "''")}' -Recurse -Force -ErrorAction SilentlyContinue";
                var ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                Process.Start(new ProcessStartInfo(ps, $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{script}\"")
                {
                    CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Environment.SystemDirectory,
                });
            }
            else
            {
                TryDeleteDir(dir);
            }
        }

        /// <summary>
        /// Puts back a Windows setting WinSolve changes only temporarily (automatic driver
        /// downloads, paused during a graphics driver clean install) if WinSolve was closed before
        /// restoring it, then removes WinSolve's own machine key.
        /// </summary>
        private static void RestorePendingSettings()
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    using (var pending = hklm.OpenSubKey(@"SOFTWARE\WinSolve\Pending"))
                    using (var searching = hklm.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching"))
                    {
                        if (pending?.GetValue("SearchOrderConfig") is int previous)
                        {
                            if (previous == -1) searching.DeleteValue("SearchOrderConfig", throwOnMissingValue: false);
                            else searching.SetValue("SearchOrderConfig", previous, RegistryValueKind.DWord);
                        }
                    }
                    hklm.DeleteSubKeyTree(@"SOFTWARE\WinSolve", throwOnMissingSubKey: false);
                }
            }
            catch { }
        }

        public static bool IsInstalled(bool allUsers) => File.Exists(Path.Combine(InstallDir(allUsers), ExeName));

        // ═══════════════════ Helpers ═══════════════════

        private static void KillRunning()
        {
            foreach (var p in Process.GetProcessesByName("WinSolve"))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
                finally { p.Dispose(); }
            }
            // WinSolve runs as administrator; a non-elevated setup can't close it. Say so now
            // instead of failing later with "file in use".
            var still = Process.GetProcessesByName("WinSolve");
            var running = still.Length > 0;
            foreach (var p in still) p.Dispose();
            if (running)
                throw new InvalidOperationException("WinSolve is still running. Close it (right-click its icon next to the clock > Exit) and try again.");
        }

        /// <summary>Writes <paramref name="target"/> through a temporary file and swaps it in, keeping the old one if anything fails.</summary>
        private static void ReplaceFile(string target, Action<Stream> write)
        {
            var tmp = target + ".new";
            try
            {
                using (var fs = File.Create(tmp)) write(fs);
                if (File.Exists(target))
                {
                    var backup = target + ".old";
                    TryDelete(backup);
                    File.Replace(tmp, target, backup, ignoreMetadataErrors: true);
                    TryDelete(backup);
                }
                else
                {
                    File.Move(tmp, target);
                }
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }

        /// <summary>The updater's folder, only if it really holds a WinSolve installation.</summary>
        private static string ValidUpdateDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;
            try
            {
                var full = Path.GetFullPath(dir.Trim().Trim('"')).TrimEnd('\\');
                return File.Exists(Path.Combine(full, ExeName)) ? full : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Updates the version in the existing uninstall entry for <paramref name="dir"/>, wherever it
        /// is (all users, this account, or another signed-in account). False if none was found.
        /// </summary>
        private static bool UpdateRegistration(string dir, string exe)
        {
            var found = false;
            void Update(RegistryKey root, string path)
            {
                try
                {
                    using (var key = root.OpenSubKey(path, writable: true))
                    {
                        if (key == null) return;
                        var location = (key.GetValue("InstallLocation") as string ?? "").TrimEnd('\\');
                        if (!string.Equals(location, dir, StringComparison.OrdinalIgnoreCase)) return;
                        key.SetValue("DisplayVersion", Version);
                        key.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
                        found = true;
                    }
                }
                catch { }
            }

            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)) Update(hklm, UninstallKey);
            using (var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64))
            {
                foreach (var sid in users.GetSubKeyNames())
                {
                    if (sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)) continue;
                    Update(users, sid + "\\" + UninstallKey);
                }
            }
            return found;
        }

        /// <summary>Full System32 path for a Windows tool (never the folder setup was started from).</summary>
        internal static string Sys(string exe) => Path.Combine(Environment.SystemDirectory, exe);

        private static void RunHidden(string file, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(Sys(file), args) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory }))
                    p?.WaitForExit(10000);
            }
            catch { }
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); } catch { }
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>.lnk creation through the WScript.Shell COM object.</summary>
    internal static class Shortcuts
    {
        public static void Create(string lnk, string target, string workingDir, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            var shell = Activator.CreateInstance(shellType);
            try
            {
                var shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var t = shortcut.GetType();
                t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
                t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                t.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { description });
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { target + ",0" });
                t.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                Marshal.FinalReleaseComObject(shortcut);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    /// <summary>Authenticode verification (WinVerifyTrust) plus a signer name check.</summary>
    internal static class Signature
    {
        public static bool IsSignedBy(string file, string subject)
        {
            if (!Verify(file)) return false;
            try
            {
                var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                    System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file));
                // Exact match of the organization or common name (not a substring).
                var cn = cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
                var org = cert.Subject.Split(',').Select(p => p.Trim())
                    .Where(p => p.StartsWith("O=", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Substring(2).Trim('"')).FirstOrDefault();
                return string.Equals(cn, subject, StringComparison.OrdinalIgnoreCase) || string.Equals(org, subject, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private static bool Verify(string file)
        {
            var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)), pcwszFilePath = file };
            var pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = 2,          // WTD_UI_NONE
                    fdwRevocationChecks = 1, // WTD_REVOKE_WHOLECHAIN
                    dwUnionChoice = 1,       // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,
                    dwProvFlags = 0x80,      // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
                };
                return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, ref data) == 0;
            }
            finally
            {
                Marshal.FreeHGlobal(pFile);
            }
        }
    }
}
