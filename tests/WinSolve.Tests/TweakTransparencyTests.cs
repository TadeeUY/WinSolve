using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinSolve.Localization;
using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public class TweakTransparencyTests
{
    [Fact]
    public void Every_tweak_says_what_it_changes()
    {
        var missing = TweakCatalog.All.Where(t => t.Changes.Count == 0).Select(t => t.Id).ToList();
        Assert.True(missing.Count == 0, "Tweaks without a list of changes: " + string.Join(", ", missing));
    }

    [Fact]
    public void Registry_changes_name_the_hive_and_the_value()
    {
        foreach (var t in TweakCatalog.All)
            foreach (var c in t.Changes.Where(c => c.Kind is TweakChangeKind.RegistryValue or TweakChangeKind.RegistryKey))
            {
                Assert.Matches(@"^(HKCU|HKLM|HKU)\\[^\\]", c.Target);
                Assert.False(string.IsNullOrWhiteSpace(c.NewValue), $"{t.Id}: {c.Target}");
                Assert.False(string.IsNullOrWhiteSpace(c.Undo), $"{t.Id}: {c.Target}");
            }
    }

    [Fact]
    public void Tweaks_that_change_machine_settings_show_HKLM_or_a_service_or_command()
    {
        foreach (var t in TweakCatalog.All.Where(t => t.AllUsers))
            Assert.Contains(t.Changes, c => c.Target.StartsWith(@"HKLM\") || c.Target.StartsWith(@"HKU\") || c.Kind is TweakChangeKind.Service or TweakChangeKind.Command);
    }

    [Fact]
    public void The_services_tweak_lists_each_service()
    {
        var t = TweakCatalog.Find("ess-services-manual")!;
        Assert.True(t.Changes.Count >= 20);
        Assert.All(t.Changes, c => Assert.Equal(TweakChangeKind.Service, c.Kind));
        Assert.Contains(t.Changes, c => c.Target == "DiagTrack" && c.NewValue == "Startup: Disabled");
    }

    [Fact]
    public void Visual_effects_undo_puts_back_the_users_own_values()
    {
        // It used to write Windows' defaults; now it remembers the previous values like the other tweaks.
        var t = TweakCatalog.Find("perf-visual-effects")!;
        Assert.Contains(t.Changes, c => c.Target.EndsWith(@"\UserPreferencesMask") && c.Undo.StartsWith("Puts back the previous value"));
    }

    [Theory]
    [InlineData(0, RegistryValueKind.DWord, "DWORD 0 (0x0)")]
    [InlineData(1, RegistryValueKind.DWord, "DWORD 1 (0x1)")]
    [InlineData(-1, RegistryValueKind.DWord, "DWORD 4294967295 (0xFFFFFFFF)")]
    [InlineData("Disabled", RegistryValueKind.String, "SZ \"Disabled\"")]
    public void Values_are_shown_as_registry_data(object value, RegistryValueKind kind, string expected)
        => Assert.Equal(expected, TweakChange.Describe(value, kind));

    [Fact]
    public void Binary_values_are_shown_in_hex()
        => Assert.Equal("BINARY 9012038010000000", TweakChange.Describe(new byte[] { 0x90, 0x12, 0x03, 0x80, 0x10, 0, 0, 0 }, RegistryValueKind.Binary));

    private static readonly Regex Technical = new(@"^(DWORD|QWORD|SZ|BINARY|MULTI_SZ) |^powercfg |^DISM ", RegexOptions.Compiled);

    private static bool Translatable(string text) =>
        Spanish.Table.ContainsKey(text)
        || Spanish.Table.Keys.Where(k => k.Contains("{0}")).Any(k => Regex.IsMatch(text, "^" + Regex.Escape(k).Replace(@"\{0}", ".+") + "$"));

    [Fact]
    public void Every_change_description_is_in_Spanish_too()
    {
        var untranslated = TweakCatalog.All.SelectMany(t => t.Changes)
            .SelectMany(c => new[] { c.NewValue, c.Undo })
            .Where(text => !Technical.IsMatch(text) && !Translatable(text))
            .Distinct().ToList();
        Assert.True(untranslated.Count == 0, "Missing Spanish: " + string.Join(" | ", untranslated));
    }
}
