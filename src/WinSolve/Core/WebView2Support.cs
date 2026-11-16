using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Web.WebView2.Core;

namespace WinSolve.Core;

/// <summary>
/// The Microsoft Edge WebView2 engine (Chromium), used by the pages that are drawn as web pages.
/// </summary>
/// <remarks>
/// Optional and off unless turned on in Settings: when anything here isn't available the classic
/// pages are used. WinSolve runs elevated and only loads DLLs from System32, so the WebView2 loader
/// DLL (embedded in WinSolve.exe) is written to the Administrators-only data folder, checked against
/// the embedded copy while a read-only handle blocks writers, and loaded by its full path before the
/// WebView2 library asks for it by name. The browser profile also lives in that folder. The portable
/// edition doesn't use it (it must leave nothing on the PC).
/// </remarks>
internal static class WebView2Support
{
    private const string LoaderResource = "WebView2Loader.dll";

    private static readonly Lazy<bool> Ready = new(Prepare);
    private static Task<CoreWebView2Environment>? _environment;

    /// <summary>True when the web pages can be used on this PC, are turned on and haven't failed in this version.</summary>
    internal static bool IsAvailable
        => AppSettings.Current.ChromiumHome && AppSettings.Current.ChromiumHomeFailedOn != CurrentVersion && Ready.Value;

    internal static string CurrentVersion => System.Windows.Forms.Application.ProductVersion.Split('+')[0];

    /// <summary>The web Home page didn't work here: the classic page is used until the next version.</summary>
    internal static void MarkFailed()
    {
        AppSettings.Current.ChromiumHomeFailedOn = CurrentVersion;
        AppSettings.Current.Save();
    }

    private static bool Prepare()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return false;
            if (Environment.GetEnvironmentVariable("WINSOLVE_NO_WEBVIEW") == "1") return false;
            if (Edition.IsPortable || !Admin.IsElevated) return false;

            using var resource = typeof(WebView2Support).Assembly.GetManifestResourceStream(LoaderResource);
            if (resource is null) return false; // built without the loader

            var bytes = new byte[resource.Length];
            resource.ReadExactly(bytes);
            var hash = SHA256.HashData(bytes);

            var native = Path.Combine(SafePath.DataFolder, "Native");
            SafePath.MakeTrusted(native);
            var dir = Path.Combine(native, "webview2-" + Convert.ToHexString(hash)[..16].ToLowerInvariant());
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "WebView2Loader.dll"); // the name must stay: the library asks for it by name
            if (!File.Exists(file))
            {
                var temp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, file, overwrite: true);
            }

            using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!SHA256.HashData(held).AsSpan().SequenceEqual(hash))
                {
                    Logger.Write("WebView2 loader on disk is not the one embedded in WinSolve; using the classic pages.");
                    return false;
                }
                // Stays loaded: a later load of "WebView2Loader.dll" by name finds this module.
                NativeLibrary.Load(file);
            }

            var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            Logger.Write($"WebView2 runtime {version} available.");
            return true;
        }
        catch (Exception ex)
        {
            // Includes WebView2RuntimeNotFoundException: Windows 10 without the Edge WebView2 runtime.
            Logger.Write($"WebView2 not used: {ex.Message}");
            return false;
        }
    }

    /// <summary>One browser environment for the whole app (its profile is in the trusted data folder).</summary>
    internal static Task<CoreWebView2Environment> GetEnvironmentAsync()
        => _environment ??= CreateEnvironmentAsync();

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var profile = Path.Combine(SafePath.DataFolder, "WebView2");
        var options = new CoreWebView2EnvironmentOptions
        {
            Language = AppSettings.Current.Language == "es" ? "es" : "en-US",
        };
        return CoreWebView2Environment.CreateAsync(null, profile, options);
    }
}
