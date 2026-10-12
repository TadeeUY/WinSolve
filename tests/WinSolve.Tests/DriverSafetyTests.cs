using WinSolve.Services;
using Xunit;
using static WinSolve.Services.HardwareService;

namespace WinSolve.Tests;

public class DeviceRepairTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(28)]
    [InlineData(43)]
    public void Fixable_codes_are_restarted_and_reinstalled(int code)
        => Assert.Equal(DeviceRepairAction.RestartThenReinstall, RepairActionFor("Media", code));

    [Theory]
    [InlineData(12)] // resource conflict
    [InlineData(14)] // needs a restart
    [InlineData(21)] // being removed
    [InlineData(22)] // disabled by the user
    [InlineData(29)] // disabled in firmware
    [InlineData(32)] // service disabled
    [InlineData(45)] // unplugged
    [InlineData(47)] // prepared for safe removal
    [InlineData(48)] // blocked driver
    [InlineData(52)] // unsigned driver
    public void Other_codes_are_never_touched(int code)
        => Assert.Equal(DeviceRepairAction.None, RepairActionFor("Media", code));

    [Theory]
    [InlineData("Net")]
    [InlineData("DiskDrive")]
    [InlineData("SCSIAdapter")]
    [InlineData("HDC")]
    [InlineData("Volume")]
    [InlineData("Keyboard")]
    [InlineData("Mouse")]
    [InlineData("HIDClass")]
    [InlineData("System")]
    [InlineData("Processor")]
    [InlineData("Battery")]
    [InlineData("Display")]
    [InlineData("USB")]
    [InlineData("usb")]
    public void Critical_classes_are_only_restarted(string cls)
        => Assert.Equal(DeviceRepairAction.RestartOnly, RepairActionFor(cls, 43));

    [Fact]
    public void Plan_skips_devices_that_cannot_be_fixed()
    {
        var plans = PlanRepairs(
        [
            new ProblemDevice("Webcam", "Camera", @"USB\VID_1", 10, ""),
            new ProblemDevice("Wi-Fi", "Net", @"PCI\VEN_1", 10, ""),
            new ProblemDevice("Disabled thing", "Media", @"ROOT\1", 22, ""),
            new ProblemDevice("Restart pending", "Media", @"ROOT\2", 14, ""),
        ]);
        Assert.Equal(2, plans.Count);
        Assert.Equal(DeviceRepairAction.RestartThenReinstall, plans.Single(p => p.Device.Name == "Webcam").Action);
        Assert.Equal(DeviceRepairAction.RestartOnly, plans.Single(p => p.Device.Name == "Wi-Fi").Action);
    }
}

public class GraphicsInstallerTests
{
    [Theory]
    [InlineData("AMD Software", "AMD Software: Adrenalin Edition", "amd-software-adrenalin-edition-24.12.1-win10-win11-dec-rdna.exe")]
    [InlineData("", "", "whql-amd-software-adrenalin-edition-24.12.1-win10-win11.exe")]
    [InlineData("AMD Radeon Software", "Radeon Software Installer", "setup.exe")]
    public void Full_amd_packages_are_accepted(string product, string description, string file)
        => Assert.Null(DriverService.InstallerProblem(GpuVendor.Amd, product, description, file));

    [Theory]
    [InlineData("Intel® Graphics Driver", "Intel® Graphics Driver Installer", "gfx_win_101.6129.exe")]
    [InlineData("Intel(R) Arc(TM) Graphics", "", "driver.exe")]
    [InlineData("", "", "gfx_win_101.5762.exe")]
    public void Full_intel_packages_are_accepted(string product, string description, string file)
        => Assert.Null(DriverService.InstallerProblem(GpuVendor.Intel, product, description, file));

    [Theory]
    [InlineData(GpuVendor.Amd, "AMD Chipset Software", "AMD Chipset Software", "amd_chipset_software_6.10.17.152.exe")]
    [InlineData(GpuVendor.Amd, "AMD Software", "AMD Software Installer", "amd-software-adrenalin-edition-24.12.1-minimalsetup-241204_web.exe")]
    [InlineData(GpuVendor.Amd, "AMD Auto-Detect and Install Tool", "", "radeon-software-auto-detect.exe")]
    [InlineData(GpuVendor.Intel, "Intel® Driver & Support Assistant", "Intel DSA Installer", "Intel-Driver-and-Support-Assistant-Installer.exe")]
    [InlineData(GpuVendor.Intel, "Intel® Chipset Device Software", "", "SetupChipset.exe")]
    [InlineData(GpuVendor.Intel, "Intel® Wireless Bluetooth®", "", "BT-23.60.5-64UWD-Win10-Win11.exe")]
    [InlineData(GpuVendor.Amd, "", "", "random-tool.exe")]
    [InlineData(GpuVendor.Intel, "Some Other App", "Setup", "setup.exe")]
    public void Other_installers_are_rejected_before_anything_is_removed(GpuVendor vendor, string product, string description, string file)
        => Assert.NotNull(DriverService.InstallerProblem(vendor, product, description, file));

    [Fact]
    public void Nvidia_installers_are_not_checked_by_name()
        => Assert.Null(DriverService.InstallerProblem(GpuVendor.Nvidia, "", "", "566.36-desktop-win10-win11-64bit-international-dch-whql.exe"));
}

public class HybridGraphicsTests
{
    [Theory]
    [InlineData(GpuVendor.Intel, "Intel(R) UHD Graphics 620", true)]
    [InlineData(GpuVendor.Intel, "Intel(R) Iris(R) Xe Graphics", true)]
    [InlineData(GpuVendor.Intel, "Intel(R) Arc(TM) Graphics", true)]
    [InlineData(GpuVendor.Intel, "Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData(GpuVendor.Amd, "AMD Radeon(TM) Graphics", true)]
    [InlineData(GpuVendor.Amd, "AMD Radeon(TM) Vega 8 Graphics", true)]
    [InlineData(GpuVendor.Amd, "AMD Radeon 780M", true)]
    [InlineData(GpuVendor.Amd, "AMD Radeon RX 7900 XTX", false)]
    [InlineData(GpuVendor.Amd, "AMD Radeon RX 6800M", false)]
    [InlineData(GpuVendor.Nvidia, "NVIDIA GeForce RTX 4060 Laptop GPU", false)]
    public void Integrated_gpus_are_recognised(GpuVendor vendor, string name, bool integrated)
        => Assert.Equal(integrated, DriverService.IsLikelyIntegrated(vendor, name));

    [Fact]
    public void Removing_the_integrated_gpu_of_a_hybrid_pc_warns()
    {
        var gpus = new List<GpuInfo>
        {
            new() { Name = "Intel(R) UHD Graphics 630", Vendor = GpuVendor.Intel },
            new() { Name = "NVIDIA GeForce RTX 3060 Laptop GPU", Vendor = GpuVendor.Nvidia },
        };
        Assert.NotNull(DriverService.HybridWarning(GpuVendor.Intel, gpus));
        Assert.Null(DriverService.HybridWarning(GpuVendor.Nvidia, gpus));
        Assert.Null(DriverService.HybridWarning(GpuVendor.Intel, gpus.Take(1).ToList()));
    }
}

public class DiskScheduleTests
{
    [Fact]
    public void Drive_letters_of_page_files_profiles_and_programs()
    {
        var system = char.ToUpperInvariant((Path.GetPathRoot(Environment.SystemDirectory) ?? "C:")[0]);
        var letters = DiskRepairService.DriveLetters(["D:\\pagefile.sys 0 0", "?:\\pagefile.sys", "e:\\Users\\ana", "\\\\?\\F:\\Program Files", "", null, "relative\\path"]);
        Assert.Contains('D', letters);
        Assert.Contains(system, letters);
        Assert.Contains('E', letters);
        Assert.Contains('F', letters);
        Assert.Equal(new[] { 'D', 'E', 'F', system }.Distinct().Count(), letters.Count);
    }

    [Fact]
    public void Boot_schedule_is_read_from_boot_execute()
    {
        string[] entries = ["autocheck autochk /r \\??\\C:", "autocheck autochk *"];
        Assert.True(DiskRepairService.IsCheckScheduled(entries, 'C', surfaceScan: true));
        Assert.True(DiskRepairService.IsCheckScheduled(entries, 'c', surfaceScan: false));
        Assert.False(DiskRepairService.IsCheckScheduled(entries, 'D', surfaceScan: true));
        Assert.False(DiskRepairService.IsCheckScheduled(["autocheck autochk *"], 'C', surfaceScan: false));
        Assert.False(DiskRepairService.IsCheckScheduled(["autocheck autochk /p \\??\\C:"], 'C', surfaceScan: true));
    }

    [Theory]
    [InlineData("Would you like to schedule this volume to be checked the next time the system restarts? (Y/N)", 'Y')]
    [InlineData("¿Desea programar este volumen para que se compruebe la próxima vez que se reinicie el sistema? (S/N)", 'S')]
    [InlineData("Soll die Überprüfung beim nächsten Neustart ausgeführt werden (J/N)?", 'J')]
    [InlineData("Chkdsk cannot run because the volume is in use.", null)]
    public void Yes_letter_is_read_from_the_localized_prompt(string output, char? expected)
        => Assert.Equal(expected, DiskRepairService.YesLetter(output));
}

public class UpdateFailureAlertTests
{
    private static readonly DateTime Now = new(2026, 3, 10, 12, 0, 0);

    [Fact]
    public void A_single_failure_does_not_suggest_the_reset()
        => Assert.False(ErrorMonitor.ShouldSuggestUpdateReset([(Now, "kb1")], Now));

    [Fact]
    public void Retries_of_the_same_update_on_the_same_day_count_once()
        => Assert.False(ErrorMonitor.ShouldSuggestUpdateReset([(Now, "kb1"), (Now.AddMinutes(-5), "KB1"), (Now.AddMinutes(-30), "kb1")], Now));

    [Fact]
    public void Repeated_failures_within_a_week_suggest_the_reset()
    {
        Assert.True(ErrorMonitor.ShouldSuggestUpdateReset([(Now, "kb1"), (Now.AddDays(-2), "kb1"), (Now.AddDays(-4), "kb1")], Now));
        Assert.True(ErrorMonitor.ShouldSuggestUpdateReset([(Now, "kb1"), (Now.AddHours(-1), "kb2"), (Now.AddHours(-2), "kb3")], Now));
    }

    [Fact]
    public void Old_failures_do_not_count()
        => Assert.False(ErrorMonitor.ShouldSuggestUpdateReset([(Now, "kb1"), (Now.AddDays(-8), "kb1"), (Now.AddDays(-10), "kb1")], Now));
}
