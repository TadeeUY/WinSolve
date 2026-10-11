namespace WinSolve.Core;

/// <summary>Plain text log in %ProgramData%\WinSolve\logs (administrators-only).</summary>
public static class Logger
{
    private static readonly object Gate = new();

    public static string LogDirectory => Path.Combine(SafePath.DataFolder, "logs");

    private static readonly System.Text.RegularExpressions.Regex ProductKey =
        new(@"\b(?:[A-Z0-9]{5}-){4}([A-Z0-9]{5})\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Masks Windows product keys (slmgr prints the full key): only the last 5 characters stay.</summary>
    public static string MaskSecrets(string text) => ProductKey.Replace(text, "XXXXX-XXXXX-XXXXX-XXXXX-$1");

    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> Early = new();

    /// <summary>For messages while the log folder itself is being set up; written with the next line.</summary>
    internal static void WriteEarly(string message) => Early.Enqueue($"[{DateTime.Now:HH:mm:ss}] {MaskSecrets(message)}");

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {MaskSecrets(message)}";
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = Path.Combine(LogDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
                while (Early.TryDequeue(out var early)) File.AppendAllText(file, early + Environment.NewLine);
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
