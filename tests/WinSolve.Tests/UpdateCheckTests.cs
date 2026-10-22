using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public class UpdateCheckTests
{
    private static string Release(string tag, params string[] assets) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/TadeeUY/WinSolve/releases/tag/{{tag}}",
          "body": "notes",
          "assets": [{{string.Join(",", assets.Select(a => $$"""{ "name": "{{a}}", "browser_download_url": "https://github.com/TadeeUY/WinSolve/releases/download/{{tag}}/{{a}}" }"""))}}]
        }
        """;

    [Fact]
    public void A_newer_complete_release_is_offered()
    {
        var u = UpdateService.Parse(Release("v0.16.2", "WinSolveSetup.exe", "WinSolveSetup.exe.sha256", "WinSolve-Portable.exe"), new Version(0, 16, 1));
        Assert.NotNull(u);
        Assert.Equal(new Version(0, 16, 2), u!.Version);
        Assert.EndsWith("/WinSolveSetup.exe", u.SetupUrl);
        Assert.EndsWith("/WinSolveSetup.exe.sha256", u.Sha256Url);
        Assert.Equal("https://github.com/TadeeUY/WinSolve/releases/tag/v0.16.2", u.ReleaseUrl);
    }

    [Fact]
    public void The_same_or_an_older_version_is_not_offered()
    {
        Assert.Null(UpdateService.Parse(Release("v0.16.1", "WinSolveSetup.exe", "WinSolveSetup.exe.sha256"), new Version(0, 16, 1)));
        Assert.Null(UpdateService.Parse(Release("v0.15.5", "WinSolveSetup.exe", "WinSolveSetup.exe.sha256"), new Version(0, 16, 1)));
        Assert.Null(UpdateService.Parse(Release("nightly", "WinSolveSetup.exe", "WinSolveSetup.exe.sha256"), new Version(0, 16, 1)));
    }

    [Fact]
    public void A_release_still_uploading_its_files_waits_for_the_next_check()
    {
        Assert.Null(UpdateService.Parse(Release("v0.16.2", "WinSolveSetup.exe"), new Version(0, 16, 1)));
        Assert.Null(UpdateService.Parse(Release("v0.16.2", "WinSolveSetup.exe.sha256"), new Version(0, 16, 1)));
        Assert.Null(UpdateService.Parse(Release("v0.16.2"), new Version(0, 16, 1)));
    }

    [Fact]
    public void New_versions_are_looked_for_every_couple_of_minutes()
        => Assert.True(UpdateService.CheckInterval <= TimeSpan.FromMinutes(5));
}
