using WinSolve.Core;
using Xunit;

namespace WinSolve.Tests;

public class SafePathTests
{
    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    [Fact]
    public void System_folders_are_protected()
    {
        Assert.True(SafePath.IsProtectedFolder(Windows));
        Assert.True(SafePath.IsProtectedFolder(Path.Combine(Windows, "System32")));
        Assert.True(SafePath.IsProtectedFolder(ProgramFiles));
        Assert.True(SafePath.IsProtectedFolder(Path.GetPathRoot(Windows)!));
        Assert.True(SafePath.IsProtectedFolder(Path.Combine(ProgramFiles, "Windows Defender")));
        Assert.True(SafePath.IsProtectedFolder(Path.Combine(ProgramFiles, "WindowsApps", "Something")));
    }

    [Fact]
    public void Profile_roots_and_shell_folders_are_protected()
    {
        Assert.True(SafePath.IsProtectedFolder(Profile));
        Assert.True(SafePath.IsProtectedFolder(Path.Combine(Path.GetDirectoryName(Profile)!, "SomeoneElse")));
        Assert.True(SafePath.IsProtectedFolder(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        Assert.True(SafePath.IsProtectedFolder(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        // An ancestor of a shell folder is protected too.
        Assert.True(SafePath.IsProtectedFolder(Path.Combine(Profile, "AppData")));
    }

    [Fact]
    public void Regular_program_folders_are_not_protected()
    {
        Assert.False(SafePath.IsProtectedFolder(Path.Combine(ProgramFiles, "SomeVendor", "SomeApp")));
        Assert.False(SafePath.IsProtectedFolder(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SomeApp")));
    }

    [Fact]
    public void Same_or_inside_does_not_match_prefix_siblings()
    {
        Assert.True(SafePath.IsSameOrInside(@"C:\App\bin", @"C:\App"));
        Assert.True(SafePath.IsSameOrInside(@"C:\App", @"C:\App\"));
        Assert.False(SafePath.IsSameOrInside(@"C:\Apple", @"C:\App"));
    }

    [Fact]
    public void Program_files_is_not_user_writable_but_profile_is()
    {
        Assert.False(SafePath.IsUserWritableLocation(Path.Combine(ProgramFiles, "WinSolve", "WinSolve.exe")));
        Assert.True(SafePath.IsUserWritableLocation(Path.Combine(Profile, "AppData", "Local", "Programs", "WinSolve", "WinSolve.exe")));
    }

    [Fact]
    public void Junction_is_detected()
    {
        var root = Path.Combine(Path.GetTempPath(), "winsolve-test-" + Guid.NewGuid().ToString("N"));
        var target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        var link = Path.Combine(root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception)
        {
            return; // creating links needs Developer Mode or admin; skip where unavailable
        }
        try
        {
            Assert.True(SafePath.HasReparsePoint(link));
            Assert.True(SafePath.HasReparsePoint(Path.Combine(link, "inner")));
            Assert.False(SafePath.HasReparsePoint(target));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }
}
