using WinSolve.Core;
using Xunit;

namespace WinSolve.Tests;

public class DataLocationTests
{
    [Fact]
    public void Installed_edition_keeps_everything_in_ProgramData()
    {
        Assert.Equal(DataMode.Installed, DataLocation.Decide(portable: false, "NTFS", besideExeUsable: true));
        Assert.Equal(("pd", "pd"), DataLocation.Folders(DataMode.Installed, "pd", "exe", "fallback"));
    }

    [Theory]
    [InlineData("NTFS")]
    [InlineData("ReFS")]
    public void Portable_on_a_drive_with_permissions_keeps_everything_next_to_the_exe(string fileSystem)
    {
        var mode = DataLocation.Decide(portable: true, fileSystem, besideExeUsable: true);
        Assert.Equal(DataMode.BesideExe, mode);
        Assert.Equal(("exe", "exe"), DataLocation.Folders(mode, "pd", "exe", "fallback"));
    }

    [Theory]
    [InlineData("FAT32")]
    [InlineData("FAT")]
    [InlineData("exFAT")]
    public void Portable_on_a_stick_without_permissions_keeps_backups_it_applies_elevated_away_from_it(string fileSystem)
    {
        // Anyone can change files on FAT/exFAT: a planted DNS or startup backup would be applied as administrator.
        var mode = DataLocation.Decide(portable: true, fileSystem, besideExeUsable: true);
        Assert.Equal(DataMode.Split, mode);
        Assert.Equal(("fallback", "exe"), DataLocation.Folders(mode, "pd", "exe", "fallback"));
    }

    [Theory]
    [InlineData("NTFS")]
    [InlineData(null)]
    public void Portable_where_the_exe_folder_cant_be_used_falls_back(string? fileSystem)
    {
        var mode = DataLocation.Decide(portable: true, fileSystem, besideExeUsable: false);
        Assert.Equal(DataMode.Fallback, mode);
        Assert.Equal(("fallback", "fallback"), DataLocation.Folders(mode, "pd", "exe", "fallback"));
    }

    [Fact]
    public void The_portable_fallback_never_shares_the_installed_folder()
    {
        Assert.NotEqual(DataLocation.InstalledFolder, DataLocation.PortableFallbackFolder, StringComparer.OrdinalIgnoreCase);
        Assert.False(SafePath.IsSameOrInside(DataLocation.PortableFallbackFolder, DataLocation.InstalledFolder));
    }

    [Fact]
    public void Tests_run_as_the_installed_edition()
    {
        Assert.False(Edition.IsPortable);
        Assert.Equal(DataMode.Installed, DataLocation.Mode);
        Assert.Equal(DataLocation.InstalledFolder, DataLocation.TrustedPath);
    }

    [Fact]
    public void A_marker_file_next_to_the_exe_turns_on_portable_mode()
    {
        var dir = Directory.CreateTempSubdirectory("winsolve-marker").FullName;
        try
        {
            Assert.False(Edition.HasPortableMarker(dir));
            File.WriteAllText(Path.Combine(dir, DataLocation.PortableMarker), "");
            Assert.True(Edition.HasPortableMarker(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Exit_cleanup_removes_working_folders_and_keeps_what_can_still_be_undone()
    {
        var root = Directory.CreateTempSubdirectory("winsolve-portable").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Updates", "abc"));
            File.WriteAllText(Path.Combine(root, "Updates", "abc", "WinSolveSetup.exe"), "x");
            Directory.CreateDirectory(Path.Combine(root, "Report", "def"));
            File.WriteAllText(Path.Combine(root, "dns-backup.json"), "[]");

            DataLocation.CleanUp(root, removeWhenEmpty: true);

            Assert.False(Directory.Exists(Path.Combine(root, "Updates")));
            Assert.False(Directory.Exists(Path.Combine(root, "Report")));
            Assert.True(File.Exists(Path.Combine(root, "dns-backup.json"))); // Undo still possible
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Exit_cleanup_removes_the_fallback_folder_when_nothing_is_left_to_undo()
    {
        var root = Directory.CreateTempSubdirectory("winsolve-portable").FullName;
        Directory.CreateDirectory(Path.Combine(root, "Drivers", "x"));
        DataLocation.CleanUp(root, removeWhenEmpty: true);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Exit_cleanup_next_to_the_exe_keeps_the_folder()
    {
        var root = Directory.CreateTempSubdirectory("winsolve-portable").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Battery"));
            DataLocation.CleanUp(root, removeWhenEmpty: false);
            Assert.True(Directory.Exists(root));
            Assert.False(Directory.Exists(Path.Combine(root, "Battery")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("FAT32", true)]
    [InlineData("exFAT", true)]
    [InlineData("NTFS", false)]
    [InlineData("ReFS", false)]
    [InlineData(null, false)]
    public void Linkless_file_systems(string? fileSystem, bool expected)
        => Assert.Equal(expected, DataLocation.IsLinklessFileSystem(fileSystem));
}
