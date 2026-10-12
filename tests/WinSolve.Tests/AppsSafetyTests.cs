using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public class StoreAppRiskTests
{
    [Theory]
    [InlineData("Microsoft.WindowsStore")]
    [InlineData("Microsoft.DesktopAppInstaller")]
    [InlineData("Microsoft.StorePurchaseApp")]
    [InlineData("Microsoft.Winget.Source")]
    [InlineData("Microsoft.SecHealthUI")]
    [InlineData("Microsoft.XboxIdentityProvider")]
    [InlineData("Microsoft.HEVCVideoExtension")]
    [InlineData("Microsoft.HEIFImageExtension")]
    [InlineData("Microsoft.WebpImageExtension")]
    [InlineData("Microsoft.VP9VideoExtensions")]
    [InlineData("Microsoft.RawImageExtension")]
    [InlineData("Microsoft.AV1VideoExtension")]
    [InlineData("Microsoft.MPEG2VideoExtension")]
    [InlineData("Microsoft.WebMediaExtensions")]
    [InlineData("Microsoft.Windows.ShellExperienceHost")]
    [InlineData("Microsoft.Windows.StartMenuExperienceHost")]
    [InlineData("Microsoft.AAD.BrokerPlugin")]
    [InlineData("Microsoft.AccountsControl")]
    [InlineData("Microsoft.LockApp")]
    [InlineData("Microsoft.Windows.CloudExperienceHost")]
    [InlineData("Microsoft.UI.Xaml.2.8")]
    [InlineData("Microsoft.VCLibs.140.00.UWPDesktop")]
    [InlineData("Microsoft.NET.Native.Framework.2.2")]
    [InlineData("Microsoft.WindowsAppRuntime.1.5")]
    [InlineData("MicrosoftWindows.Client.CBS")]
    public void Windows_parts_are_required(string name)
        => Assert.Equal(StoreAppRisk.Required, AppsService.ClassifyStoreApp(name, out _));

    [Theory]
    [InlineData("Microsoft.Windows.Photos")]
    [InlineData("Microsoft.WindowsCalculator")]
    [InlineData("Microsoft.WindowsNotepad")]
    [InlineData("Microsoft.Paint")]
    [InlineData("Microsoft.WindowsTerminal")]
    [InlineData("Microsoft.ScreenSketch")]
    [InlineData("Microsoft.WindowsCamera")]
    public void Built_in_apps_warn_with_a_consequence(string name)
    {
        Assert.Equal(StoreAppRisk.Warn, AppsService.ClassifyStoreApp(name, out var why));
        Assert.False(string.IsNullOrWhiteSpace(why));
    }

    [Fact]
    public void Bloatware_is_not_required_or_warned()
    {
        foreach (var name in AppsService.KnownBloat.Keys)
            Assert.Equal(StoreAppRisk.Normal, AppsService.ClassifyStoreApp(name, out _));
    }
}

public class ProgramKindTests
{
    [Theory]
    [InlineData("Microsoft Windows Desktop Runtime - 8.0.8 (x64)", "Microsoft Corporation")]
    [InlineData("Microsoft .NET Runtime - 6.0.33 (x64)", "Microsoft Corporation")]
    [InlineData("Microsoft ASP.NET Core 8.0.8 - Shared Framework (x64)", "Microsoft Corporation")]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.40.33810", "Microsoft Corporation")]
    [InlineData("Microsoft Edge WebView2 Runtime", "Microsoft Corporation")]
    [InlineData("Microsoft DirectX for Something", "Microsoft Corporation")]
    public void Shared_runtimes_are_recognized(string name, string publisher)
        => Assert.Equal(ProgramKind.SharedRuntime, AppsService.ClassifyProgram(name, publisher, "{GUID}", out _));

    [Theory]
    [InlineData("NVIDIA Graphics Driver 560.94", "NVIDIA Corporation")]
    [InlineData("AMD Software", "Advanced Micro Devices, Inc.")]
    [InlineData("Realtek High Definition Audio Driver", "Realtek Semiconductor Corp.")]
    [InlineData("Intel(R) Chipset Device Software", "Intel(R) Corporation")]
    [InlineData("Windows Driver Package - Logitech (USB) HIDClass", "Logitech")]
    public void Driver_packages_are_recognized(string name, string publisher)
        => Assert.Equal(ProgramKind.Driver, AppsService.ClassifyProgram(name, publisher, "x", out _));

    [Fact]
    public void Desktop_runtime_8_names_WinSolve_in_the_warning()
    {
        AppsService.ClassifyProgram("Microsoft Windows Desktop Runtime - 8.0.8 (x64)", "Microsoft Corporation", "x", out var why);
        Assert.Contains("WinSolve", why);
    }

    [Fact]
    public void WinSolve_itself_is_recognized()
    {
        Assert.Equal(ProgramKind.WinSolve, AppsService.ClassifyProgram("WinSolve", "WinSolve", "WinSolve", out _));
        Assert.Equal(ProgramKind.WinSolve, AppsService.ClassifyProgram("Anything", "", "WinSolve", out _));
    }

    [Theory]
    [InlineData("7-Zip 24.08 (x64)", "Igor Pavlov")]
    [InlineData("NVIDIA App", "NVIDIA Corporation")]
    [InlineData("Docker Desktop", "Docker Inc.")]
    public void Ordinary_programs_are_normal(string name, string publisher)
        => Assert.Equal(ProgramKind.Normal, AppsService.ClassifyProgram(name, publisher, "x", out _));
}

public class LeftoverRuleTests
{
    private const string Local = @"C:\Users\Ann\AppData\Local";
    private const string Roaming = @"C:\Users\Ann\AppData\Roaming";

    [Theory]
    [InlineData(@"C:\Users\Ann\AppData\Local\Programs\MyApp", true)]
    [InlineData(@"C:\Users\Ann\AppData\Local\Programs\MyApp\bin", true)]
    [InlineData(@"C:\Users\Ann\AppData\Local\MyApp", true)]
    [InlineData(@"C:\Users\Ann\AppData\Roaming\MyApp", true)]
    [InlineData(@"C:\Users\Ann\AppData\Local\Programs", false)]
    [InlineData(@"C:\Users\Ann\AppData\Local\Programs\Common", false)]
    [InlineData(@"C:\Users\Ann\AppData\Local", false)]
    [InlineData(@"C:\Users\Ann\AppData\Roaming", false)]
    [InlineData(@"C:\Users\Ann\AppData\Local\Microsoft\Teams", false)]
    [InlineData(@"C:\Users\Ann\AppData\Local\Packages\Foo", false)]
    [InlineData(@"C:\Users\Ann\AppData\Local\Temp\Foo", false)]
    [InlineData(@"C:\Users\Ann\Documents\MyApp", false)]
    [InlineData(@"C:\Users\Ann", false)]
    [InlineData(@"C:\Program Files\MyApp", false)]
    [InlineData(@"C:\Users\Ann\AppData\LocalLow\MyApp", false)]
    public void Per_user_entries_may_only_point_at_their_own_AppData_folder(string path, bool allowed)
        => Assert.Equal(allowed, AppsService.IsAllowedPerUserFolder(path, Local, Roaming));

    [Fact]
    public void Only_the_install_folder_is_ticked_by_default()
    {
        var p = new InstalledProgram { DisplayName = "X", KeyPath = @"SOFTWARE\x" };
        Assert.True(new Leftover(p, @"C:\Program Files\X", LeftoverKind.InstallFolder).CheckedByDefault);
        Assert.False(new Leftover(p, @"C:\Users\Ann\AppData\Local\X", LeftoverKind.UserData).CheckedByDefault);
        Assert.Contains("your data", new Leftover(p, "y", LeftoverKind.UserData).Note);
    }

    [Fact]
    public void A_folder_another_entry_points_into_is_shared()
    {
        var others = new List<IReadOnlyList<string>>
        {
            AppsService.ReferencedPaths("", "\"C:\\Program Files\\Vendor\\Other\\uninst.exe\" /S", "C:\\Program Files\\Vendor\\Other\\o.exe,0", ""),
        };
        Assert.True(AppsService.IsReferencedByOthers(@"C:\Program Files\Vendor", others));
        Assert.True(AppsService.IsReferencedByOthers(@"C:\Program Files\Vendor\Other", others));
        Assert.False(AppsService.IsReferencedByOthers(@"C:\Program Files\Vendor\Mine", others));
    }

    [Fact]
    public void Referenced_paths_cover_location_uninstaller_icon_and_source()
    {
        var paths = AppsService.ReferencedPaths(@"C:\A", "\"C:\\B\\u.exe\" /x", "\"C:\\C\\i.ico\",3", @"C:\D\");
        Assert.Equal([@"C:\A", @"C:\B\u.exe", @"C:\C\i.ico", @"C:\D"], paths);
        Assert.Empty(AppsService.ReferencedPaths("", "MsiExec.exe /X{11111111-1111-1111-1111-111111111111}", "", "relative\\path"));
    }

    [Theory]
    [InlineData(true, true, true, true, false)]    // MSI still registered: never by hand
    [InlineData(true, false, false, false, true)]  // MSI gone: the entry is an orphan
    [InlineData(false, false, true, true, true)]
    [InlineData(false, false, null, true, true)]   // no uninstaller to run, files gone
    [InlineData(false, false, false, true, false)] // uninstaller failed
    [InlineData(false, false, true, false, false)] // files still there
    public void Uninstall_entry_is_deleted_only_when_the_program_is_gone(bool isMsi, bool registered, bool? succeeded, bool filesGone, bool expected)
        => Assert.Equal(expected, AppsService.MayDeleteUninstallEntry(isMsi, registered, succeeded, filesGone));

    [Fact]
    public void Msi_product_code_is_packed_like_Windows_Installer()
        => Assert.Equal("00002109110000000000000000F01FEC", AppsService.PackedGuid("{90120000-0011-0000-0000-0000000FF1CE}"));

    [Fact]
    public void Success_exit_codes()
    {
        Assert.True(AppsService.IsSuccessExitCode(0));
        Assert.True(AppsService.IsSuccessExitCode(3010));
        Assert.False(AppsService.IsSuccessExitCode(1603));
    }
}

public class CriticalProcessTests
{
    [Theory]
    [InlineData("csrss")]
    [InlineData("winlogon.exe")]
    [InlineData("dwm")]
    [InlineData("lsass")]
    [InlineData("svchost")]
    [InlineData("MsMpEng")]
    [InlineData("Memory Compression")]
    [InlineData("")]
    public void Windows_processes_are_never_killed(string name)
        => Assert.Equal(KillVerdict.Critical, LockFinder.CanKill(name, 1, false, 12345));

    [Fact]
    public void Restart_Manager_critical_type_is_refused()
        => Assert.Equal(KillVerdict.Critical, LockFinder.CanKill("someapp", 1000, false, 12345));

    [Fact]
    public void Explorer_is_restarted_not_killed()
    {
        Assert.Equal(KillVerdict.Explorer, LockFinder.CanKill("explorer", 4, false, 12345));
        Assert.Equal(KillVerdict.Explorer, LockFinder.CanKill("explorer.exe", 1, false, 12345));
    }

    [Fact]
    public void Services_self_and_ordinary_programs()
    {
        Assert.Equal(KillVerdict.Service, LockFinder.CanKill("someservice", 3, true, 12345));
        Assert.Equal(KillVerdict.Self, LockFinder.CanKill("whatever", 1, false, Environment.ProcessId));
        Assert.Equal(KillVerdict.Allowed, LockFinder.CanKill("notepad", 1, false, 12345));
    }

    [Fact]
    public void Start_times_must_match()
    {
        Assert.True(LockFinder.IsSameStartTime(1000, 1000 + 50));
        Assert.False(LockFinder.IsSameStartTime(1000, 1000 + 2 * TimeSpan.TicksPerSecond));
    }
}

public class StartupProtectionTests
{
    private const string Win = @"C:\Windows", Pf = @"C:\Program Files", Pd = @"C:\ProgramData";

    [Theory]
    [InlineData("SecurityHealth", @"%windir%\system32\SecurityHealthSystray.exe")]
    [InlineData("Whatever", @"""C:\Program Files\Windows Defender\MSASCuiL.exe""")]
    public void Defender_is_protected(string name, string command)
        => Assert.Equal(StartupService.DefenderReason, StartupService.ProtectionReasonFor(name, command, Win, Pf, Pd));

    [Fact]
    public void Windows_programs_are_protected_but_script_hosts_are_not()
    {
        Assert.Equal(StartupService.WindowsReason, StartupService.ProtectionReasonFor("x", @"C:\Windows\System32\something.exe /a", Win, Pf, Pd));
        Assert.Null(StartupService.ProtectionReasonFor("x", @"C:\Windows\System32\rundll32.exe C:\ProgramData\bad\x.dll,Run", Win, Pf, Pd));
        Assert.Null(StartupService.ProtectionReasonFor("x", @"""C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"" -File C:\x.ps1", Win, Pf, Pd));
        Assert.Null(StartupService.ProtectionReasonFor("Steam", @"""C:\Program Files (x86)\Steam\steam.exe"" -silent", Win, Pf, Pd));
    }

    [Theory]
    [InlineData("S-1-5-18", true)]
    [InlineData("SYSTEM", true)]
    [InlineData(@"NT AUTHORITY\SYSTEM", true)]
    [InlineData("LOCAL SERVICE", true)]
    [InlineData("Ann", false)]
    [InlineData("", false)]
    public void System_tasks_are_recognized(string user, bool expected)
        => Assert.Equal(expected, StartupService.IsServiceAccount(user));
}

public class ContextMenuOverrideTests
{
    [Fact]
    public void Only_a_key_WinSolve_created_and_nothing_else_uses_is_deleted()
    {
        Assert.True(ContextMenuItems.MayDeleteOverrideKey(1, ["LegacyDisable", "WinSolveOverride"], 0));
        Assert.False(ContextMenuItems.MayDeleteOverrideKey(2, ["LegacyDisable", "WinSolveOverride"], 0)); // existed before
        Assert.False(ContextMenuItems.MayDeleteOverrideKey(1, ["LegacyDisable", "WinSolveOverride", "MUIVerb"], 0));
        Assert.False(ContextMenuItems.MayDeleteOverrideKey(1, ["LegacyDisable", "WinSolveOverride"], 1));
        Assert.False(ContextMenuItems.MayDeleteOverrideKey(null, ["LegacyDisable"], 0));
    }
}
