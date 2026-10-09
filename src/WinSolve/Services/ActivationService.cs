using WinSolve.Core;

namespace WinSolve.Services;

public sealed class ActivationStatus
{
    public string Product { get; set; } = "";
    public string Channel { get; set; } = "";
    public string PartialKey { get; set; } = "";
    public int LicenseStatus { get; set; } = -1;
    public int GraceMinutes { get; set; }
    public string? FirmwareKey { get; set; }
    public string? FirmwareKeyEdition { get; set; }

    public bool IsActivated => LicenseStatus == 1;

    /// <summary>Digital/permanent license rather than an expiring KMS activation.</summary>
    public bool IsPermanent => IsActivated && GraceMinutes == 0;

    public string StatusText => LicenseStatus switch
    {
        0 => "Unlicensed",
        1 => GraceMinutes > 0 ? $"Activated (expires in {GraceMinutes / 1440} days)" : "Permanently activated",
        2 => "Initial grace period",
        3 => "Additional grace period (hardware change)",
        4 => "Non-genuine grace period",
        5 => "Not activated (notification mode)",
        6 => "Extended grace period",
        _ => "Unknown",
    };
}

/// <summary>
/// Windows license status and activation through official channels:
/// the OEM key embedded in the firmware, a user-entered key and Microsoft's troubleshooter.
/// </summary>
public static class ActivationService
{
    private const string WindowsAppId = "55c92734-d682-4d71-983e-d6ec3f16059f";

    public static ActivationStatus GetStatus()
    {
        var status = new ActivationStatus();

        var products = Wmi.Query(
            $"SELECT Name, Description, PartialProductKey, LicenseStatus, GracePeriodRemaining FROM SoftwareLicensingProduct " +
            $"WHERE ApplicationID = '{WindowsAppId}' AND PartialProductKey IS NOT NULL");

        // There can be several; prefer the activated one.
        var p = products.OrderByDescending(x => x.Get<int>("LicenseStatus") == 1).FirstOrDefault();
        if (p is not null)
        {
            status.Product = p.Str("Name").Replace("Windows(R), ", "Windows ");
            status.PartialKey = p.Str("PartialProductKey");
            status.LicenseStatus = p.Get<int>("LicenseStatus");
            status.GraceMinutes = p.Get<int>("GracePeriodRemaining");
            var desc = p.Str("Description");
            status.Channel = desc.Contains("OEM", StringComparison.OrdinalIgnoreCase) ? "OEM"
                : desc.Contains("RETAIL", StringComparison.OrdinalIgnoreCase) ? "Retail"
                : desc.Contains("VOLUME_KMSCLIENT", StringComparison.OrdinalIgnoreCase) ? "Volume (KMS)"
                : desc.Contains("VOLUME_MAK", StringComparison.OrdinalIgnoreCase) ? "Volume (MAK)"
                : desc;
        }

        foreach (var svc in Wmi.Query("SELECT OA3xOriginalProductKey, OA3xOriginalProductKeyDescription FROM SoftwareLicensingService"))
        {
            var key = svc.Str("OA3xOriginalProductKey");
            if (key.Length == 29)
            {
                status.FirmwareKey = key;
                status.FirmwareKeyEdition = svc.Str("OA3xOriginalProductKeyDescription");
            }
        }

        return status;
    }

    private static string Slmgr(string args) => $"//nologo \"{Path.Combine(Environment.SystemDirectory, "slmgr.vbs")}\" {args}";

    /// <summary>Installs a product key and activates online.</summary>
    public static async Task<bool> ActivateWithKeyAsync(string key, Action<string> log, CancellationToken ct = default)
    {
        key = key.Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^([A-Z0-9]{5}-){4}[A-Z0-9]{5}$"))
        {
            log("Invalid key format. Expected XXXXX-XXXXX-XXXXX-XXXXX-XXXXX.");
            return false;
        }

        log("Installing the product key...");
        // slmgr echoes the full key: mask it before it reaches the screen and the log file.
        var install = await ProcessRunner.RunAsync("cscript.exe", Slmgr($"/ipk {key}"), l => log(Logger.MaskSecrets(l)), ct, ConsoleEncoding());
        if (!install.Success) return false;

        return await ActivateOnlineAsync(log, ct);
    }

    public static async Task<bool> ActivateOnlineAsync(Action<string> log, CancellationToken ct = default)
    {
        log("Activating online with Microsoft's servers...");
        await ProcessRunner.RunAsync("cscript.exe", Slmgr("/ato"), l => log(Logger.MaskSecrets(l)), ct, ConsoleEncoding());
        var ok = GetStatus().IsActivated;
        log(ok ? "Windows is activated." : "Activation did not complete. Try the Activation troubleshooter in Settings.");
        return ok;
    }

    /// <summary>cscript writes in the OEM code page (850 on Spanish Windows), not UTF-8.</summary>
    private static System.Text.Encoding ConsoleEncoding()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return System.Text.Encoding.GetEncoding((int)GetOEMCP());
        }
        catch { return System.Text.Encoding.UTF8; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    public static void OpenActivationSettings() => ProcessRunner.ShellOpen("ms-settings:activation");
}
