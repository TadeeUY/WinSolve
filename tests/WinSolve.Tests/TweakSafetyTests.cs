using Microsoft.Win32;
using WinSolve.Core;
using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public sealed class PreviousValueStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "winsolve-store-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "tweak-backup.json");

    public PreviousValueStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Missing_file_is_an_empty_store()
        => Assert.Empty(new PreviousValueStore(FilePath).Load());

    [Fact]
    public void First_recorded_value_is_kept()
    {
        var store = new PreviousValueStore(FilePath);
        store.RecordIfMissing([new("a", StoredValue.From(5, RegistryValueKind.DWord))]);
        store.RecordIfMissing([new("a", StoredValue.From(9, RegistryValueKind.DWord)), new("b", StoredValue.Missing())]);

        var loaded = new PreviousValueStore(FilePath).Load();
        Assert.Equal(5, loaded["a"].ToObject());
        Assert.True(loaded["b"].Absent);
        Assert.Null(loaded["b"].ToObject());
    }

    [Fact]
    public void Keys_are_case_insensitive_and_removable()
    {
        var store = new PreviousValueStore(FilePath);
        store.Set("Tweak|machine|LocalMachine\\SOFTWARE\\X|Name", StoredValue.Missing());
        Assert.NotNull(store.Get("tweak|MACHINE|localmachine\\software\\x|name"));
        store.Remove(["TWEAK|machine|LocalMachine\\SOFTWARE\\X|Name"]);
        Assert.Null(store.Get("Tweak|machine|LocalMachine\\SOFTWARE\\X|Name"));
        Assert.False(File.Exists(FilePath)); // an empty store leaves no file
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_and_store_keeps_working()
    {
        File.WriteAllText(FilePath, "{ this is not json");
        var store = new PreviousValueStore(FilePath);

        Assert.Empty(store.Load());
        Assert.Single(Directory.GetFiles(_dir, "tweak-backup.json.corrupt-*"));

        store.Set("k", StoredValue.From("x", RegistryValueKind.String));
        Assert.Equal("x", new PreviousValueStore(FilePath).Get("k")!.ToObject());
    }

    [Fact]
    public void Saves_are_atomic_and_leave_no_temporary_file()
    {
        var store = new PreviousValueStore(FilePath);
        for (int i = 0; i < 5; i++) store.Set("k" + i, StoredValue.From(i, RegistryValueKind.DWord));
        Assert.Equal(5, store.Load().Count);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Values_round_trip_with_their_type()
    {
        var store = new PreviousValueStore(FilePath);
        store.Set("dword", StoredValue.From(unchecked((int)0xFFFFFFFF), RegistryValueKind.DWord));
        store.Set("qword", StoredValue.From(1L, RegistryValueKind.QWord));
        store.Set("expand", StoredValue.From("%SystemRoot%\\x", RegistryValueKind.ExpandString));
        store.Set("multi", StoredValue.From(new[] { "a", "b c" }, RegistryValueKind.MultiString));
        store.Set("binary", StoredValue.From(new byte[] { 0x90, 0x12, 0x00 }, RegistryValueKind.Binary));

        var s = new PreviousValueStore(FilePath).Load();
        Assert.Equal(-1, s["dword"].ToObject());
        Assert.Equal(RegistryValueKind.DWord, s["dword"].ValueKind);
        Assert.Equal(1L, s["qword"].ToObject());
        Assert.Equal("%SystemRoot%\\x", s["expand"].ToObject());
        Assert.Equal(RegistryValueKind.ExpandString, s["expand"].ValueKind);
        Assert.Equal(new[] { "a", "b c" }, (string[])s["multi"].ToObject()!);
        Assert.Equal(new byte[] { 0x90, 0x12, 0x00 }, (byte[])s["binary"].ToObject()!);
    }

    [Fact]
    public void Undecodable_value_falls_back_instead_of_throwing()
    {
        var bad = new StoredValue { Kind = "DWord", Data = "not a number" };
        Assert.Null(bad.ToObject());
        Assert.Null(StoredValue.Marker().ToObject());
    }

    [Fact]
    public void Value_with_the_wrong_type_for_its_kind_is_kept_as_its_real_type()
    {
        var v = StoredValue.From("1", RegistryValueKind.DWord);
        Assert.Equal(RegistryValueKind.String, v.ValueKind);
        Assert.Equal("1", v.ToObject());
    }
}

public class TweakSafetyTests
{
    [Fact]
    public void Policy_paths_are_detected()
    {
        Assert.True(TweakCatalog.IsPolicyPath(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection"));
        Assert.True(TweakCatalog.IsPolicyPath(@"Software\Policies\Microsoft\Edge"));
        Assert.True(TweakCatalog.IsPolicyPath(@"SOFTWARE\Microsoft\PolicyManager\default\WiFi\AllowWiFiHotSpotReporting"));
        Assert.False(TweakCatalog.IsPolicyPath(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"));
    }

    [Fact]
    public void Policy_tweaks_are_flagged()
    {
        Assert.True(TweakCatalog.Find("priv-telemetry")!.TouchesPolicies);
        Assert.True(TweakCatalog.Find("ess-edge-debloat")!.TouchesPolicies);
        Assert.False(TweakCatalog.Find("perf-menu-delay")!.TouchesPolicies);
    }

    [Fact]
    public void Risky_tweaks_carry_warnings()
    {
        Assert.Contains("DirectAccess", TweakCatalog.Find("adv-ipv6-off")!.Warning);
        Assert.Contains("Linux", TweakCatalog.Find("adv-utc-time")!.Warning);
    }

    [Fact]
    public void Services_backup_reads_old_and_new_formats()
    {
        var old = TweakCatalog.ParseServicesBackup("""{"DiagTrack":2,"iphlpsvc":2}""");
        Assert.Equal(new TweakCatalog.ServiceBackup(2, false), old["diagtrack"]);

        var current = TweakCatalog.ParseServicesBackup("""{"DiagTrack":{"Start":2,"Delayed":true},"Fax":{"Start":3,"Delayed":false}}""");
        Assert.Equal(new TweakCatalog.ServiceBackup(2, true), current["DiagTrack"]);
        Assert.Equal(new TweakCatalog.ServiceBackup(3, false), current["Fax"]);
    }

    [Fact]
    public void Sc_start_modes_include_delayed_auto()
    {
        Assert.Equal("delayed-auto", TweakCatalog.ScStartMode(2, delayed: true));
        Assert.Equal("auto", TweakCatalog.ScStartMode(2, delayed: false));
        Assert.Equal("demand", TweakCatalog.ScStartMode(3, delayed: false));
        Assert.Equal("disabled", TweakCatalog.ScStartMode(4, delayed: false));
        Assert.Null(TweakCatalog.ScStartMode(0, delayed: false));
    }

    [Fact]
    public void Known_folder_move_is_detected()
    {
        const string profile = @"C:\Users\Ana";
        var roots = new[] { @"C:\Users\Ana\OneDrive - Contoso", "" };

        var moved = TweakCatalog.FoldersInsideOneDrive(new Dictionary<string, string>
        {
            ["Desktop"] = @"C:\Users\Ana\OneDrive - Contoso\Desktop",
            ["Personal"] = @"%USERPROFILE%\Documents",
            ["My Pictures"] = @"%userprofile%\OneDrive - Contoso\Pictures",
        }, roots, profile);
        Assert.Equal(new[] { "Desktop", "Pictures" }, moved);

        var untouched = TweakCatalog.FoldersInsideOneDrive(new Dictionary<string, string>
        {
            ["Desktop"] = @"%USERPROFILE%\Desktop",
            ["Personal"] = @"C:\Users\Ana\OneDriveBackup\Documents", // not inside the OneDrive folder
        }, roots, profile);
        Assert.Empty(untouched);
    }

    [Fact]
    public void Default_dns_restores_saved_servers_and_never_strands_static_adapters()
    {
        var dhcp = new DnsAdapter { Name = "Wi-Fi", Dhcp = true };
        var fixedIp = new DnsAdapter { Name = "Ethernet", Dhcp = false, V4 = ["1.1.1.1"] };

        var saved = new DnsBackupEntry { ManualV4 = true, V4 = ["192.168.1.10", "bogus"], ManualV6 = false, V6 = ["fe80::1"] };
        var p1 = DnsService.PlanDefault(dhcp, saved);
        Assert.Equal(DnsRestoreKind.SetSaved, p1.Kind);
        Assert.Equal(new[] { "192.168.1.10" }, p1.Servers);
        Assert.True(p1.ResetFirst); // IPv6 came from DHCP: reset first

        Assert.Equal(DnsRestoreKind.ResetToDhcp, DnsService.PlanDefault(dhcp, null).Kind);
        Assert.Equal(DnsRestoreKind.ResetToDhcp, DnsService.PlanDefault(dhcp, new DnsBackupEntry()).Kind);

        // Static IP: never reset (it would have no DNS at all).
        Assert.Equal(DnsRestoreKind.Keep, DnsService.PlanDefault(fixedIp, null).Kind);
        Assert.Equal(DnsRestoreKind.Keep, DnsService.PlanDefault(fixedIp, new DnsBackupEntry()).Kind);
        var p2 = DnsService.PlanDefault(fixedIp, new DnsBackupEntry { ManualV4 = true, V4 = ["10.0.0.1"] });
        Assert.Equal(DnsRestoreKind.SetSaved, p2.Kind);
        Assert.False(p2.ResetFirst);
    }

    [Fact]
    public void Autostart_task_runs_on_battery_without_time_limit()
    {
        var xml = AutoStart.TaskXml(@"C:\Program Files\WinSolve\WinSolve.exe", "S-1-5-21-1-2-3-1001");
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var settings = doc.Root!.Element(ns + "Settings")!;
        Assert.Equal("false", settings.Element(ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", settings.Element(ns + "StopIfGoingOnBatteries")!.Value);
        Assert.Equal("PT0S", settings.Element(ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("HighestAvailable", doc.Descendants(ns + "RunLevel").Single().Value);
        Assert.Equal("S-1-5-21-1-2-3-1001", doc.Descendants(ns + "LogonTrigger").Single().Element(ns + "UserId")!.Value);
        Assert.Equal("--tray", doc.Descendants(ns + "Arguments").Single().Value);
    }
}

public class HibernationPlanTests
{
    private static HardwareProfile Hw(bool battery) => new()
    {
        IsLaptop = false, SystemOnSsd = true, RamGb = 16, CpuThreads = 8, GpuVendor = "NVIDIA", HasDedicatedGpu = true, HasBattery = battery,
    };

    [Fact]
    public void Hibernation_is_only_turned_off_without_a_battery()
    {
        var without = OptimizationPlanner.Build(DeviceKind.PC, OptimizationLevel.Maximum, Hw(battery: false)).Tweaks.Select(t => t.Id).ToList();
        Assert.Contains("sys-hibernation-off", without);
        Assert.Contains("perf-fast-startup-off", without);

        var with = OptimizationPlanner.Build(DeviceKind.PC, OptimizationLevel.Maximum, Hw(battery: true)).Tweaks.Select(t => t.Id).ToList();
        Assert.DoesNotContain("sys-hibernation-off", with);
        Assert.DoesNotContain("perf-fast-startup-off", with);
    }
}
