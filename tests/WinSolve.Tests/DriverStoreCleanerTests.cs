using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public class DriverStoreCleanerTests
{
    private const string Repo = @"C:\Windows\System32\DriverStore\FileRepository\";

    [Fact]
    public void Only_unused_versions_older_than_the_previous_one_are_offered()
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

        // 6590 is the newest and 6094 the previous one (kept for rollback): only 5222 goes.
        // Acme has just two versions: both stay.
        Assert.Equal(["oem12.inf"], old.Select(d => d.Inf));
        Assert.All(old, d => Assert.Equal("32.0.15.6590", d.NewerVersion));
    }

    [Fact]
    public void The_newest_and_the_previous_version_are_always_kept()
    {
        var packages = new List<(string, string, string, string, string, string?)>
        {
            ("oem1.inf", Repo + @"a.inf_amd64_1\a.inf", "Acme", "Display", "1.0.0.0", null),
            ("oem2.inf", Repo + @"a.inf_amd64_2\a.inf", "Acme", "Display", "2.0.0.0", null),
            ("oem3.inf", Repo + @"a.inf_amd64_3\a.inf", "Acme", "Display", "3.0.0.0", null),
            ("oem4.inf", Repo + @"a.inf_amd64_4\a.inf", "Acme", "Display", "4.0.0.0", null),
            ("oem5.inf", Repo + @"a.inf_amd64_5\a.inf", "Acme", "Display", "4.0.0.0", null), // same newest version twice
        };

        var old = DriverStoreCleaner.Pick(packages, [], _ => 1);

        Assert.Equal(["oem1.inf", "oem2.inf"], old.Select(d => d.Inf).OrderBy(x => x));
        Assert.All(old, d => Assert.Equal("4.0.0.0", d.NewerVersion));
    }

    [Fact]
    public void Two_versions_of_a_driver_are_both_kept()
    {
        var packages = new List<(string, string, string, string, string, string?)>
        {
            ("oem1.inf", Repo + @"b.inf_amd64_1\b.inf", "Acme", "Net", "1.0.0.0", null),
            ("oem2.inf", Repo + @"b.inf_amd64_2\b.inf", "Acme", "Net", "2.0.0.0", null),
        };
        Assert.Empty(DriverStoreCleaner.Pick(packages, [], _ => 1));
    }

    [Fact]
    public void Cleanup_is_offered_only_after_a_later_boot()
    {
        var boot = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        Assert.False(DriverStoreCleaner.BootedSince(boot, boot));
        Assert.False(DriverStoreCleaner.BootedSince(boot, boot.AddSeconds(30))); // uptime drift, same boot
        Assert.True(DriverStoreCleaner.BootedSince(boot, boot.AddHours(3)));
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
