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

        public string ToArgs() =>
            "--install" + (AllUsers ? " --allusers" : "") + (Clean ? " --clean" : "") +
            (DesktopShortcut ? " --desktop" : "") + (Launch ? " --launch" : "");

        public static InstallOptions FromArgs(string[] args) => new InstallOptions
        {
            AllUsers = args.Contains("--allusers"),
            Clean = args.Contains("--clean"),
            DesktopShortcut = args.Contains("--desktop"),
            Launch = args.Contains("--launch"),
        };
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
            var file = Path.Combine(Path.GetTempPath(), "windowsdesktop-runtime-8-win-x64.exe");
            status("Downloading the .NET 8 Desktop Runtime from Microsoft...");
            using (var wc = new WebClient())
            {
                wc.DownloadProgressChanged += (s, e) => progress(e.ProgressPercentage);
                using (ct.Register(wc.CancelAsync))
                    wc.DownloadFileTaskAsync(new Uri(RuntimeUrl), file).GetAwaiter().GetResult();
            }

            if (!Signature.IsSignedBy(file, "Microsoft Corporation"))
            {
                TryDelete(file);
                throw new InvalidOperationException("The downloaded .NET runtime is not signed by Microsoft. Installation stopped.");
            }

            status("Installing the .NET 8 Desktop Runtime...");
            var psi = new ProcessStartInfo(file, "/install /quiet /norestart") { UseShellExecute = true };
            if (!IsElevated) psi.Verb = "runas";
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                // 0 = OK, 3010 = OK but restart required, 1638 = newer version already installed
                if (p.ExitCode != 0 && p.ExitCode != 3010 && p.ExitCode != 1638)
                    throw new InvalidOperationException($".NET runtime setup failed with exit code {p.ExitCode}.");
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

            status("Closing WinSolve if it is running...");
            KillRunning();

            if (o.Clean)
            {
                status("Removing the previous installation and settings...");
                RemoveInstallation(allUsers: false, removeData: true, status);
                if (IsElevated) RemoveInstallation(allUsers: true, removeData: true, status);
            }
            progress(75);

            var dir = InstallDir(o.AllUsers);
            status($"Copying files to {dir}...");
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, ExeName);
            using (var res = Assembly.GetExecutingAssembly().GetManifestResourceStream(ExeName))
            using (var fs = File.Create(exe))
                res.CopyTo(fs);

            // The installer doubles as the uninstaller.
            var uninstaller = Path.Combine(dir, UninstallerName);
            File.Copy(Assembly.GetExecutingAssembly().Location, uninstaller, overwrite: true);
            progress(85);

            status("Creating shortcuts...");
            Shortcuts.Create(Path.Combine(StartMenuDir(o.AllUsers), AppName + ".lnk"), exe, dir, "Windows diagnostics, repair and optimization");
            if (o.DesktopShortcut)
                Shortcuts.Create(Path.Combine(DesktopDir(o.AllUsers), AppName + ".lnk"), exe, dir, "Windows diagnostics, repair and optimization");

            status("Registering the uninstaller...");
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
            status("WinSolve was installed successfully.");
        }

        public static void LaunchApp(bool allUsers)
        {
            var exe = Path.Combine(InstallDir(allUsers), ExeName);
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); } catch { /* UAC declined */ }
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

            if (removeData)
            {
                TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName));
                TryDeleteDir(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));
            }

            if (!Directory.Exists(dir)) return;
            var self = Assembly.GetExecutingAssembly().Location;
            if (self.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                // We are running from the folder being removed: delete it after this process exits.
                status("Files will be removed when the uninstaller closes.");
                var cmd = $"/d /c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{dir}\"";
                Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            else
            {
                TryDeleteDir(dir);
            }
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
        }

        private static void RunHidden(string file, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false }))
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
                var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file);
                return cert.Subject.IndexOf("O=" + subject, StringComparison.OrdinalIgnoreCase) >= 0
                       || cert.Subject.IndexOf("CN=" + subject, StringComparison.OrdinalIgnoreCase) >= 0;
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
                    fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                    dwUnionChoice = 1,       // WTD_CHOICE_FILE
                    pFile = pFile,
                    dwStateAction = 0,
                    dwProvFlags = 0x1000,    // WTD_CACHE_ONLY_URL_RETRIEVAL
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
