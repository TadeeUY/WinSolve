using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed record UpdateInfo(Version Version, string Tag, string SetupUrl, string? Sha256Url, string ReleaseUrl, string Notes);

/// <summary>
/// Checks GitHub Releases for a newer version and installs it with WinSolveSetup.exe.
/// The installer is only downloaded from GitHub over HTTPS and must match the SHA-256
/// published next to it in the same release.
/// </summary>
public static class UpdateService
{
    public const string Repository = "TadeeUY/WinSolve";
    private const string LatestApi = $"https://api.github.com/repos/{Repository}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("WinSolve-Updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Returns the latest release if it is newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(LatestApi, ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));

        AppSettings.Current.LastUpdateCheck = DateTime.Now;
        AppSettings.Current.Save();
        if (latest <= CurrentVersion) return null;

        string? setup = null, sha = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (string.Equals(name, "WinSolveSetup.exe", StringComparison.OrdinalIgnoreCase)) setup = url;
            if (string.Equals(name, "WinSolveSetup.exe.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }
        if (setup is null) return null;

        return new UpdateInfo(latest, tag, setup, sha,
            root.GetProperty("html_url").GetString() ?? $"https://github.com/{Repository}/releases",
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "");
    }

    /// <summary>Automatic check when WinSolve starts and every few hours while it runs.</summary>
    public static async Task<UpdateInfo?> CheckIfDueAsync()
    {
        var s = AppSettings.Current;
        if (!s.CheckForUpdates) return null;
        if (s.LastUpdateCheck is { } last && DateTime.Now - last < TimeSpan.FromHours(4)) return null;
        try
        {
            return await CheckAsync();
        }
        catch (Exception ex)
        {
            Logger.Write($"Update check failed: {ex.Message}");
            return null;
        }
    }

    private static bool IsGitHubHost(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps &&
           (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Downloads the new installer into an administrators-only folder, verifies its SHA-256 and
    /// starts it in automatic mode for the same install scope. WinSolve then exits.
    /// </summary>
    public static async Task InstallAsync(UpdateInfo update, Action<string> log, Action<int, int> progress, CancellationToken ct)
    {
        var uri = new Uri(update.SetupUrl);
        if (!IsGitHubHost(uri)) throw new InvalidOperationException("Update URL is not on github.com.");

        string? expectedHash = null;
        if (update.Sha256Url is { } shaUrl && IsGitHubHost(new Uri(shaUrl)))
        {
            var text = await Http.GetStringAsync(shaUrl, ct);
            expectedHash = text.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
        }
        if (expectedHash is null || expectedHash.Length != 64)
            throw new InvalidOperationException("The release has no valid SHA-256 checksum; update stopped. Download it manually from GitHub.");

        var file = Path.Combine(SafePath.CreateAdminOnlyFolder("Updates"), "WinSolveSetup.exe");
        log($"Downloading WinSolve {update.Tag}...");
        using (var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            if (!IsGitHubHost(response.RequestMessage?.RequestUri ?? uri))
                throw new InvalidOperationException("The download was redirected outside GitHub.");
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress((int)(done * 100 / total), 100);
            }
        }

        // Keep the file locked from hashing until the installer has started.
        var lockHandle = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(lockHandle, ct)).ToLowerInvariant();
            if (hash != expectedHash)
                throw new InvalidOperationException("The downloaded installer does not match the published SHA-256. Update stopped.");
            log("Checksum verified.");

            var exe = Environment.ProcessPath ?? Application.ExecutablePath;
            var allUsers = !SafePath.IsUserWritableLocation(exe);
            // Update the folder this copy runs from (not one derived from the elevated account's
            // profile), and let setup wait for this process to exit instead of killing it.
            var dir = Path.GetDirectoryName(exe)!;
            var args = "--install --auto --launch" + (allUsers ? " --allusers" : "") +
                       $" --update-dir \"{dir}\" --wait {Environment.ProcessId}";
            log("Starting the installer. WinSolve will restart.");
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, WorkingDirectory = Environment.SystemDirectory });
            await Task.Delay(1500, ct); // let the installer open the file before the lock is released
        }
        finally
        {
            await lockHandle.DisposeAsync();
        }
    }
}
