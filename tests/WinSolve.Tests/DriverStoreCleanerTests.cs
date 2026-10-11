using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public class DriverStoreCleanerTests
{
    private const string Repo = @"C:\Windows\System32\DriverStore\FileRepository\";

    [Fact]
    public void Only_older_unused_versions_of_the_same_driver_are_offered()
    {
        var packages = new List<(string, string, string, string, string, string?)>
        {
            ("oem10.inf", Repo + @"nvlt.inf_amd64_a\nvlt.inf", "NVIDIA", "Display", "32.0.15.6094", null),
            ("oem11.inf", Repo + @"nvlt.inf_amd64_b\nvlt.inf", "NVIDIA", "Display", "32.0.15.6590", null), // newest, in use
            ("oem12.inf", Repo + @"nvlt.inf_amd64_c\nvlt.inf", "NVIDIA", "Display", "31.0.15.5222", null),
            ("oem20.inf", Repo + @"rt640x64.inf_amd64_d\rt640x64.inf", "Realtek", "Net", "10.68.0.0", null), // only version
            ("oem30.inf", Repo + @"usbx.inf_amd64_e\usbx.inf", "Acme", "USB", "1.0.0.0", null),
            ("oem31.inf", Repo + @"usbx.inf_amd64_f\usbx.inf", "Acme", "USB", "2.0.0.0", null),
        };
        var inUse = new HashSet<string>(["oem11.inf", "oem20.inf", "oem30.inf"], StringComparer.OrdinalIgnoreCase);

        var old = DriverStoreCleaner.Pick(packages, inUse, _ => 100);

        Assert.Equal(["oem10.inf", "oem12.inf"], old.Select(d => d.Inf).OrderBy(x => x));
        Assert.All(old, d => Assert.Equal("32.0.15.6590", d.NewerVersion));
    }

    [Fact]
    public void Inbox_drivers_are_never_offered()
    {
        var packages = new List<(string, string, string, string, string, string?)>
        {
            ("machine.inf", Repo + @"machine.inf_amd64_a\machine.inf", "Microsoft", "System", "10.0.1", null),
            ("machine2.inf", Repo + @"machine.inf_amd64_b\machine.inf", "Microsoft", "System", "10.0.2", null),
        };
        Assert.Empty(DriverStoreCleaner.Pick(packages, [], _ => 1));
    }
}
