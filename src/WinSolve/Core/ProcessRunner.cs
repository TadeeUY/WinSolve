using System.Diagnostics;
using System.Text;

namespace WinSolve.Core;

/// <summary>Result of an external process.</summary>
public sealed record ProcessResult(int ExitCode, string Output)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Runs external tools (cmd, PowerShell, sfc, dism...) without console windows,
/// streaming their output line by line.
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        Action<string>? onLine = null,
        CancellationToken ct = default,
        Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };

        var sb = new StringBuilder();
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        void Handle(string? data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;
            // sfc/dism interleave NUL characters when writing UTF-16 to a pipe.
            var line = data.Replace("\0", string.Empty).TrimEnd();
            if (line.Length == 0) return;
            lock (sb) sb.AppendLine(line);
            onLine?.Invoke(line);
        }

        proc.OutputDataReceived += (_, e) => Handle(e.Data);
        proc.ErrorDataReceived += (_, e) => Handle(e.Data);

        try
        {
            proc.Start();
        }
        catch (Exception ex)
        {
            onLine?.Invoke($"Could not start {fileName}: {ex.Message}");
            return new ProcessResult(-1, ex.Message);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw;
        }

        return new ProcessResult(proc.ExitCode, sb.ToString());
    }

    /// <summary>Runs a Windows PowerShell 5.1 script (always available on Windows 10/11).</summary>
    public static Task<ProcessResult> PowerShellAsync(
        string script,
        Action<string>? onLine = null,
        CancellationToken ct = default)
    {
        var full = "[Console]::OutputEncoding=[Text.Encoding]::UTF8;$ProgressPreference='SilentlyContinue';$ErrorActionPreference='Continue';" + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return RunAsync("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            onLine, ct);
    }

    /// <summary>Runs a cmd.exe command line.</summary>
    public static Task<ProcessResult> CmdAsync(string command, Action<string>? onLine = null, CancellationToken ct = default)
        => RunAsync("cmd.exe", $"/d /c chcp 65001 >nul & {command}", onLine, ct);

    /// <summary>Restarts the computer after a short delay.</summary>
    public static void Reboot() => _ = RunAsync("shutdown.exe", "/r /t 5 /c \"WinSolve: restarting to finish applying changes\"");

    /// <summary>Opens a document, folder, URL or ms-settings: URI through the shell.</summary>
    public static void ShellOpen(string target, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = arguments ?? "" });
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not open {target}: {ex.Message}");
        }
    }
}
