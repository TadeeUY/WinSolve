using WinSolve.Core;
using WinSolve.UI;

namespace WinSolve;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Logger.Write($"Unobserved task exception: {e.Exception}"); e.SetObserved(); };

        Logger.Write($"WinSolve started. Admin: {Admin.IsElevated}. OS: {Environment.OSVersion}");
        // Single instance: if WinSolve is already running (e.g. in the notification area), don't start another.
        using var mutex = new Mutex(true, @"Local\WinSolve.SingleInstance", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("WinSolve is already running. Look for its icon in the notification area next to the clock.", "WinSolve",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(startHidden));
    }

    private static void Report(Exception? ex)
    {
        Logger.Write($"Unhandled exception: {ex}");
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{ex?.Message}\n\nDetails were saved to {Logger.LogDirectory}",
            "WinSolve", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
