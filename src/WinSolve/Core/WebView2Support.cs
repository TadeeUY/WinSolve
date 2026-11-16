using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
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

    /// <summary>True when this run keeps the browser profile in the account's own folder (see ChromiumProfileInUserFolder).</summary>
    internal static bool ProfileInUserFolder { get; private set; }

    /// <summary>One browser environment for the whole app.</summary>
    internal static Task<CoreWebView2Environment> GetEnvironmentAsync()
        => _environment ??= CreateEnvironmentAsync();

    private static Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        // First the administrators-only data folder. If the engine ever failed to start on this PC, its browser process
        // evidently can't use that folder (it runs without the administrator rights WinSolve has), so it gets a folder
        // of the account's own instead: protected so only that account, SYSTEM and administrators can use it, and
        // only a browser process that can't use the admin-only folder ends up there.
        var settings = AppSettings.Current;
        string? profile = null;
        if (settings.ChromiumProfileInUserFolder || settings.ChromiumHomeFailedOn is not null)
            profile = UserProfileFolder();
        ProfileInUserFolder = profile is not null;
        profile ??= Path.Combine(SafePath.DataFolder, "WebView2");

        var options = new CoreWebView2EnvironmentOptions
        {
            Language = settings.Language == "es" ? "es" : "en-US",
            // A problem with the engine is handled by WinSolve (it shows the classic page): no Edge dialogs.
            AdditionalBrowserArguments = "--noerrdialogs",
        };
        return CoreWebView2Environment.CreateAsync(null, profile, options);
    }

    /// <summary>The browser profile didn't work in the admin-only folder: use the account's own folder from the next start.</summary>
    internal static void RememberProfileProblem()
    {
        if (ProfileInUserFolder || AppSettings.Current.ChromiumProfileInUserFolder) return;
        AppSettings.Current.ChromiumProfileInUserFolder = true;
        AppSettings.Current.Save();
    }

    /// <summary>%LOCALAPPDATA%\WinSolve\WebView2 of the running account, readable and writable only by it, SYSTEM and administrators.</summary>
    private static string? UserProfileFolder()
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinSolve");
            var dir = Path.Combine(root, "WebView2");
            Directory.CreateDirectory(dir);
            // Never through a link someone put there.
            foreach (var d in new[] { root, dir })
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                {
                    Logger.Write($"WebView2 profile folder {d} is a link; not using it.");
                    return null;
                }

            var me = WindowsIdentity.GetCurrent().User;
            if (me is null) return null;
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var sid in new[] { me, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(security);
            return dir;
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not prepare the WebView2 profile folder: {ex.Message}");
            return null;
        }
    }
}
