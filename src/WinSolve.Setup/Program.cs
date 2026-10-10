using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// The installer is usually started from the Downloads folder: never load native DLLs from there.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace WinSolve.Setup
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern bool SetDefaultDllDirectories(uint flags);

        [STAThread]
        private static void Main(string[] args)
        {
            SetDefaultDllDirectories(0x00000800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */);
            Environment.CurrentDirectory = Environment.SystemDirectory;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var langIndex = Array.IndexOf(args, "--lang");
            if (langIndex >= 0 && langIndex + 1 < args.Length) Lang.Spanish = args[langIndex + 1] == "es";

            if (args.Contains("--uninstall"))
            {
                Uninstall(args);
                return;
            }

            var preset = args.Contains("--install")
                ? InstallOptions.FromArgs(args)
                : new InstallOptions { AllUsers = !Installer.IsInstalled(false) || Installer.IsInstalled(true) };
            Application.Run(new SetupForm(preset, autoStart: args.Contains("--auto")));
        }

        private static void Uninstall(string[] args)
        {
            var allUsers = args.Contains("--allusers");
            var quiet = args.Contains("--quiet");

            if (allUsers && !Installer.IsElevated)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath, string.Join(" ", args)) { UseShellExecute = true, Verb = "runas" });
                }
                catch (System.ComponentModel.Win32Exception) { /* UAC declined */ }
                return;
            }

            var removeData = true;
            if (!quiet)
            {
                var answer = MessageBox.Show(
                    Lang.T("Remove WinSolve from this PC?\n\nSelect Yes to also delete WinSolve's settings and logs, or No to keep them."),
                    Lang.T("Uninstall WinSolve"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                removeData = answer == DialogResult.Yes;
            }

            Installer.Uninstall(allUsers, removeData, _ => { });
            if (!quiet)
                MessageBox.Show(Lang.T("WinSolve was removed."), Lang.T("Uninstall WinSolve"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
