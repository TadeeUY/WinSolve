using System.Security.Principal;
using WinSolve.Core;
using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

public class ReleaseUrlTests
{
    private const string Releases = $"https://github.com/{UpdateService.Repository}/releases";

    [Theory]
    [InlineData($"https://github.com/{UpdateService.Repository}/releases/tag/v0.15.0")]
    [InlineData($"https://github.com/{UpdateService.Repository}/releases")]
    [InlineData($"https://GitHub.com/{UpdateService.Repository}/releases/tag/v1.0.0")]
    public void Release_page_of_this_repository_is_kept(string url)
        => Assert.Equal(new Uri(url).AbsoluteUri, UpdateService.SafeReleaseUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData($"http://github.com/{UpdateService.Repository}/releases/tag/v1")]
    [InlineData($"https://github.com.evil.example/{UpdateService.Repository}/releases/tag/v1")]
    [InlineData($"https://evil.example/{UpdateService.Repository}/releases")]
    [InlineData($"https://user@github.com/{UpdateService.Repository}/releases")]
    [InlineData($"https://github.com:8443/{UpdateService.Repository}/releases")]
    [InlineData("https://github.com/someone/else/releases/tag/v1")]
    [InlineData($"https://github.com/{UpdateService.Repository}/releasesX")]
    [InlineData($"https://github.com/{UpdateService.Repository}/releases/../../../attacker/repo")]
    [InlineData($"https://github.com/{UpdateService.Repository}/issues")]
    [InlineData(@"C:\Windows\System32\calc.exe")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:")]
    public void Anything_else_falls_back_to_the_releases_page(string? url)
        => Assert.Equal(Releases, UpdateService.SafeReleaseUrl(url));
}

public class TrustedOwnerTests
{
    [Fact]
    public void Administrators_system_and_services_are_trusted_owners()
    {
        Assert.True(SafePath.IsTrustedOwner(new SecurityIdentifier("S-1-5-32-544")));
        Assert.True(SafePath.IsTrustedOwner(new SecurityIdentifier("S-1-5-18")));
        // TrustedInstaller
        Assert.True(SafePath.IsTrustedOwner(new SecurityIdentifier("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464")));
    }

    [Fact]
    public void User_accounts_are_not_trusted_owners()
    {
        // A non-elevated process of the account WinSolve runs as creates folders owned by that account.
        if (!WindowsIdentity.GetCurrent().IsSystem) Assert.False(SafePath.IsTrustedOwner(WindowsIdentity.GetCurrent().User));
        Assert.False(SafePath.IsTrustedOwner(new SecurityIdentifier("S-1-5-21-1-2-3-1001")));
        Assert.False(SafePath.IsTrustedOwner(new SecurityIdentifier("S-1-5-32-545"))); // Users
        Assert.False(SafePath.IsTrustedOwner(null));
    }
}
