using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;

namespace WinSolve.Setup
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Contains("--uninstall"))
            {
                Uninstall(args);
                return;
            }

            var preset = args.Contains("--install")
                ? InstallOptions.FromArgs(args)
                : new InstallOptions { AllUsers = Installer.IsInstalled(true) && !Installer.IsInstalled(false) };
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
                    "Remove WinSolve from this PC?\n\nSelect Yes to also delete WinSolve's settings and logs, or No to keep them.",
                    "Uninstall WinSolve", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return;
                removeData = answer == DialogResult.Yes;
            }

            Installer.Uninstall(allUsers, removeData, _ => { });
            if (!quiet)
                MessageBox.Show("WinSolve was removed.", "Uninstall WinSolve", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
