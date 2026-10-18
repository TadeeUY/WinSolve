using WinSolve.Core;
using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public class ProfileTests
{
    private static Tweak FakeTweak(string id, bool available = true, string? warning = null, bool action = false) => new()
    {
        Id = id, Category = "Test", Title = "Tweak " + id, Description = "", Warning = warning, IsAction = action,
        IsApplied = () => false, Apply = () => { }, Revert = () => { },
        IsAvailable = () => available,
        Changes = [new TweakChange(TweakChangeKind.RegistryValue, @"HKCU\Software\Test\" + id, "DWORD 1 (0x1)", "Puts back the previous value (or removes it)")],
    };

    private static readonly Tweak[] Tweaks =
    [
        FakeTweak("a"), FakeTweak("b"), FakeTweak("hidden", available: false), FakeTweak("risky", warning: "Careful."), FakeTweak("once", action: true),
    ];

    private static readonly SystemTask[] Tasks =
    [
        new() { Id = "clean-temp-user", Title = "Temp", Description = "", Category = TaskCategory.Cleanup, Run = _ => Task.CompletedTask },
    ];

    private static ProfilePlan Check(string json) => ProfileService.Check(ProfileService.Parse(json), Tweaks, Tasks);

    [Fact]
    public void A_profile_survives_a_round_trip()
    {
        var p = new WinSolveProfile
        {
            Name = "Office PCs",
            Tweaks = new ProfileTweaks { Apply = ["a"], Revert = ["b"] },
            Dns = new ProfileDns { Preset = "Cloudflare" },
            Optimization = new ProfileOptimization { Device = "PC", Level = "Maximum", Run = true },
        };
        var back = ProfileService.Parse(ProfileService.Serialize(p));
        Assert.Equal("Office PCs", back.Name);
        Assert.Equal(["a"], back.Tweaks!.Apply);
        Assert.Equal(["b"], back.Tweaks.Revert);
        Assert.Equal("Cloudflare", back.Dns!.Preset);
        Assert.True(back.Optimization!.Run);
        Assert.Contains("\"format\": \"winsolve-profile\"", ProfileService.Serialize(p));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "format": "something-else", "version": 1 }""")]
    [InlineData("""{ "version": 1 }""")]
    [InlineData("null")]
    [InlineData("""{ "format": "winsolve-profile" }""")]
    public void Files_that_are_not_profiles_are_refused(string json)
        => Assert.Throws<InvalidDataException>(() => ProfileService.Parse(json));

    [Fact]
    public void A_profile_from_a_newer_WinSolve_is_refused()
    {
        var ex = Assert.Throws<InvalidDataException>(() => ProfileService.Parse("""{ "format": "winsolve-profile", "version": 99 }"""));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed_in_hand_written_profiles()
    {
        var plan = Check("""
            {
              // made by hand
              "format": "winsolve-profile", "version": 1,
              "tweaks": { "apply": ["a", "b",], },
            }
            """);
        Assert.False(plan.HasErrors);
        Assert.Equal(["a", "b"], plan.Apply.Select(t => t.Id));
    }

    [Fact]
    public async Task Unknown_ids_and_presets_are_errors_so_nothing_is_applied()
    {
        var plan = Check("""
            { "format": "winsolve-profile", "version": 1,
              "tweaks": { "apply": ["a", "nope"] },
              "dns": { "preset": "MyDns" },
              "optimization": { "device": "Tablet", "level": "Ultra", "tasks": ["clean-temp-user", "bogus"] } }
            """);
        Assert.True(plan.HasErrors);
        var errors = plan.Issues.Where(i => i.IsError).Select(i => i.Message).ToList();
        Assert.Contains(errors, m => m.Contains("nope"));
        Assert.Contains(errors, m => m.Contains("MyDns"));
        Assert.Contains(errors, m => m.Contains("Tablet"));
        Assert.Contains(errors, m => m.Contains("Ultra"));
        Assert.Contains(errors, m => m.Contains("bogus"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProfileService.ApplyAsync(plan, false, true, _ => { }, default));
    }

    [Fact]
    public void Tweaks_this_PC_doesnt_have_are_skipped_with_a_note()
    {
        var plan = Check("""{ "format": "winsolve-profile", "version": 1, "tweaks": { "apply": ["a", "hidden"] } }""");
        Assert.False(plan.HasErrors);
        Assert.Equal(["a"], plan.Apply.Select(t => t.Id));
        Assert.Contains(plan.Issues, i => !i.IsError && i.Message.Contains("hidden"));
    }

    [Fact]
    public async Task Risky_tweaks_need_their_warning_accepted()
    {
        var plan = Check("""{ "format": "winsolve-profile", "version": 1, "tweaks": { "apply": ["risky"] } }""");
        Assert.False(plan.HasErrors);
        Assert.Single(plan.Warnings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProfileService.ApplyAsync(plan, false, warningsAccepted: false, _ => { }, default));
    }

    [Fact]
    public void A_tweak_cant_be_applied_and_undone_and_actions_cant_be_undone()
    {
        var plan = Check("""{ "format": "winsolve-profile", "version": 1, "tweaks": { "apply": ["a"], "revert": ["a", "once"] } }""");
        Assert.Contains(plan.Issues, i => i.IsError && i.Message.Contains("both"));
        Assert.Contains(plan.Issues, i => i.IsError && i.Message.Contains("once"));
    }

    [Fact]
    public void An_empty_profile_is_an_error()
        => Assert.True(Check("""{ "format": "winsolve-profile", "version": 1 }""").HasErrors);

    [Fact]
    public void DNS_presets_and_settings_are_read_case_insensitively()
    {
        var plan = Check("""{ "format": "winsolve-profile", "version": 1, "dns": { "preset": "quad9" }, "optimization": { "device": "laptop", "level": "light" } }""");
        Assert.False(plan.HasErrors);
        Assert.Equal("Quad9", plan.Dns!.Name);
        Assert.Equal(DeviceKind.Laptop, plan.Device);
        Assert.Equal(OptimizationLevel.Light, plan.Level);
    }

    [Fact]
    public void The_dry_run_lists_every_change()
    {
        var plan = Check("""{ "format": "winsolve-profile", "version": 1, "tweaks": { "apply": ["a"], "revert": ["b"] }, "dns": { "preset": "Default (DHCP)" }, "optimization": { "run": true } }""");
        var lines = ProfileService.Describe(plan);
        Assert.Contains("Apply: Tweak a", lines);
        Assert.Contains(lines, l => l.Contains(@"HKCU\Software\Test\a -> DWORD 1 (0x1)"));
        Assert.Contains(lines, l => l.StartsWith("Undo: Tweak b"));
        Assert.Contains("DNS: back to the previous servers / DHCP", lines);
        Assert.Contains("Then run the one-click optimization", lines);
    }

    [Fact]
    public void Command_line_options()
    {
        var o = ProfileCli.Parse(["--apply-profile", @"C:\p.winsolve", "--silent", "--dry-run", "--accept-warnings", "--no-restore-point"]);
        Assert.Equal(@"C:\p.winsolve", o.Path);
        Assert.True(o.Silent && o.DryRun && o.AcceptWarnings && o.NoRestorePoint);
        Assert.Null(ProfileCli.Parse(["--apply-profile", "--silent"]).Path);
        Assert.True(ProfileCli.IsProfileCommand(["--APPLY-PROFILE", "x"]));
        Assert.False(ProfileCli.IsProfileCommand(["--tray"]));
    }

    [Fact]
    public void Real_catalog_ids_in_a_profile_are_recognized()
    {
        var json = $$"""{ "format": "winsolve-profile", "version": 1, "tweaks": { "apply": ["{{TweakCatalog.All.First(t => !t.IsAction && t.Warning is null && t.SafeIsAvailable()).Id}}"] } }""";
        var plan = ProfileService.Check(ProfileService.Parse(json), TweakCatalog.All, TaskCatalog.All);
        Assert.False(plan.HasErrors);
    }
}
