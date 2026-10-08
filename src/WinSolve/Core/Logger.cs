namespace WinSolve.Core;

/// <summary>Plain text log in %LocalAppData%\WinSolve\logs.</summary>
public static class Logger
{
    private static readonly object Gate = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSolve", "logs");

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(Path.Combine(LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
