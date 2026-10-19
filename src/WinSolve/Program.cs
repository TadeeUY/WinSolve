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

        // A copy inside the signed-in user's own folders (an "only me" install, or Downloads) can be
        // changed by that user. Running it with another administrator's password would hand them
        // that administrator's rights, so it only runs for the person it belongs to.
        if (InteractiveUser.Other is { } signedIn && Environment.ProcessPath is { } self && SafePath.IsSameOrInside(self, signedIn.ProfilePath))
        {
            Localization.Loc.Show(null,
                "This copy of WinSolve is in the signed-in user's own folders, where they can change it, so it won't run with another account's administrator rights.\n\nInstall WinSolve for all users (in Program Files) to use it from this account.",
                "WinSolve", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Apply a profile without opening the app (for technicians and scripts).
        if (ProfileCli.IsProfileCommand(args))
        {
            Environment.ExitCode = ProfileCli.Run(args);
            DataLocation.CleanUpOnExit();
            return;
        }

        // Single instance: if WinSolve is already running (e.g. in the notification area), don't start another.
        using var mutex = new Mutex(true, @"Local\WinSolve.SingleInstance", out var isNew);
        if (!isNew && args.Any(a => a.Equals("--restarted", StringComparison.OrdinalIgnoreCase)))
        {
            // Restarting (e.g. to apply the theme): give the previous instance time to close.
            try { isNew = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { isNew = true; }
        }
        if (!isNew)
        {
            Localization.Loc.Show(null, "WinSolve is already running. Look for its icon in the notification area next to the clock.", "WinSolve",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Deletes the one-time Safe Mode boot entry once Windows started normally again.
        _ = Task.Run(() => SafeMode.CleanupAsync());

        // Language picked in the installer ("--lang es").
        var langIndex = Array.FindIndex(args, a => a.Equals("--lang", StringComparison.OrdinalIgnoreCase));
        if (langIndex >= 0 && langIndex + 1 < args.Length && args[langIndex + 1] is "es" or "en" && AppSettings.Current.Language != args[langIndex + 1])
        {
            AppSettings.Current.Language = args[langIndex + 1];
            AppSettings.Current.Save();
        }

        var startHidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        if (!startHidden) SplashScreen.Show("Starting...");
        Application.Run(new MainForm(startHidden));
        ScreenTimeWatcher.Stop();
        // Portable edition: working folders go, and so does the fallback folder when nothing is left to undo.
        DataLocation.CleanUpOnExit();
    }

    private static void Report(Exception? ex)
    {
        Logger.Write($"Unhandled exception: {ex}");
        _ = SplashScreen.CloseAsync();
        Localization.Loc.Show(null, 
            $"An unexpected error occurred:\n\n{ex?.Message}\n\nDetails were saved to {Logger.LogDirectory}",
            "WinSolve", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
