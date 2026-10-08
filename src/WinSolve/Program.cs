using System.Runtime.InteropServices;
using WinSolve.Core;
using WinSolve.Services;
using WinSolve.UI;

// Native DLLs are only loaded from System32, never from the application folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace WinSolve;

internal static class Program
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetDefaultDllDirectories(uint flags);

    [STAThread]
    private static void Main(string[] args)
    {
        // Protect implicit LoadLibrary calls (WinForms, COM) against DLL planting.
        SetDefaultDllDirectories(0x00000800 /* LOAD_LIBRARY_SEARCH_SYSTEM32 */);
        // Child processes and relative paths never resolve against a user-controlled folder.
        Environment.CurrentDirectory = Environment.SystemDirectory;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Logger.Write($"Unobserved task exception: {e.Exception}"); e.SetObserved(); };

        Logger.Write($"WinSolve started. Admin: {Admin.IsElevated}. OS: {Environment.OSVersion}");
        if (args.Any(a => a.Equals("--maintenance", StringComparison.OrdinalIgnoreCase)))
        {
            // Headless scheduled run: no window, no single-instance check.
            Maintenance.RunAsync().GetAwaiter().GetResult();
            return;
        }

        // Single instance: if WinSolve is already running (e.g. in the notification area), don't start another.
        using var mutex = new Mutex(true, @"Local\WinSolve.SingleInstance", out var isNew);
        if (!isNew)
        {
            Localization.Loc.Show(null, "WinSolve is already running. Look for its icon in the notification area next to the clock.", "WinSolve",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        Application.Run(new MainForm(startHidden));
    }

    private static void Report(Exception? ex)
    {
        Logger.Write($"Unhandled exception: {ex}");
        Localization.Loc.Show(null, 
            $"An unexpected error occurred:\n\n{ex?.Message}\n\nDetails were saved to {Logger.LogDirectory}",
            "WinSolve", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
