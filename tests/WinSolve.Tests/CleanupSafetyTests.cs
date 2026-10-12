using WinSolve.Core;
using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

/// <summary>Path checks that keep cleanups, the Space page, duplicates and the shredder away from the wrong folders.</summary>
public class CleanupSafetyTests
{
    // Made-up rules, so these tests don't depend on the machine's folders.
    private static readonly SafePath.ProtectionRules Rules = new(
        KeyFolders:
        [
            @"C:\Windows", @"C:\Windows\System32", @"C:\Program Files", @"C:\ProgramData",
            @"C:\Users\ana", @"C:\Users\ana\Documents", @"C:\Users\ana\AppData\Local", @"C:\Users\ana\AppData\LocalLow",
            @"C:\Users\ana\OneDrive", @"D:\Docs",
        ],
        SystemAreas: [@"C:\Windows", @"C:\ProgramData\Microsoft"],
        ProfileParents: [@"C:\Users"],
        SystemDrive: "C:");

    private static bool Protected(string path, bool anyFirstLevel = true, bool systemAreas = true, SafePath.ProtectionRules? rules = null)
        => SafePath.IsProtected(path, rules ?? Rules, anyFirstLevel, systemAreas);

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\bob")]                       // any profile root
    [InlineData(@"C:\Users\ana\AppData")]               // contains AppData\Local
    [InlineData(@"C:\Users\ana\AppData\Local")]
    [InlineData(@"C:\Users\ana\AppData\LocalLow")]
    [InlineData(@"C:\Users\ana\Documents")]
    [InlineData(@"C:\Users\ana\OneDrive")]
    [InlineData(@"D:\Docs")]                            // a redirected personal folder
    [InlineData(@"C:\Recovery")]
    [InlineData(@"C:\Recovery\WindowsRE")]
    [InlineData(@"C:\$Recycle.Bin\S-1-5-21-1-2-3-1001")]
    [InlineData(@"D:\System Volume Information\x")]
    [InlineData(@"C:\$WinREAgent\Scratch")]
    [InlineData(@"C:\Windows\Temp")]                    // inside Windows
    [InlineData(@"C:\ProgramData\Microsoft\Windows\WER")]
    public void Key_and_system_folders_are_protected(string path) => Assert.True(Protected(path));

    [Theory]
    [InlineData(@"C:\Users\ana\Documents\Old project")]
    [InlineData(@"C:\Users\ana\AppData\Local\SomeApp")]
    [InlineData(@"C:\Program Files\Vendor\App")]
    [InlineData(@"D:\Docs\2019")]
    public void Folders_inside_key_folders_are_not_protected(string path) => Assert.False(Protected(path));

    [Fact]
    public void First_level_folders_of_other_drives_may_be_deleted_by_hand()
    {
        Assert.True(Protected(@"D:\Photos"));
        Assert.False(Protected(@"D:\Photos", anyFirstLevel: false));
        Assert.True(Protected(@"C:\Games", anyFirstLevel: false)); // the Windows drive stays protected
        Assert.True(Protected(@"D:\Recovery", anyFirstLevel: false));
    }

    [Fact]
    public void Critical_check_allows_known_caches_inside_Windows_but_not_key_folders()
    {
        Assert.False(Protected(@"C:\Windows\Temp", systemAreas: false));
        Assert.False(Protected(@"C:\Windows\SoftwareDistribution\Download", systemAreas: false));
        Assert.True(Protected(@"C:\Windows", systemAreas: false));
        Assert.True(Protected(@"C:\Windows\System32", systemAreas: false));
        Assert.True(Protected(@"C:\Users\ana", systemAreas: false));
        Assert.True(Protected(@"C:\Users\ana\Documents", systemAreas: false));
        Assert.False(Protected(@"C:\Users\ana\AppData\Local\Temp", systemAreas: false));
    }

    [Fact]
    public void Unknown_user_folders_protect_everything_in_profiles()
    {
        var unknown = Rules with { UserFoldersUnknown = true };
        Assert.True(Protected(@"C:\Users\ana\AppData\Local\Temp", systemAreas: false, rules: unknown));
        Assert.False(Protected(@"C:\Users\ana\AppData\Local\Temp", systemAreas: false));
    }

    [Theory]
    [InlineData(@"C:\Users\ana\AppData\Local\Temp", true)]
    [InlineData(@"C:\Users\ana\AppData\Local\temp\", true)]
    [InlineData(@"C:\Users\ana\AppData\Local\Tmp", true)]
    [InlineData(@"C:\Users\ana\AppData\Local\Temp\2", true)]                 // Remote Desktop session
    [InlineData(@"C:\Users\ana\AppData\Local\Programs\2", false)]
    [InlineData(@"C:\", false)]
    [InlineData(@"C:\Temp", false)]                                  // outside the profile
    [InlineData(@"C:\Windows\Temp", false)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData(@"C:\Users\ana", false)]
    [InlineData(@"C:\Users\ana\Documents", false)]
    [InlineData(@"C:\Users\ana\Documents\Temp", false)]              // a Temp folder of the user's own
    [InlineData(@"C:\Users\ana\AppData\Local", false)]
    [InlineData(@"C:\Users\ana\AppData\Local\Temp\..\..", false)]
    [InlineData(@"C:\Users\bob\AppData\Local\Temp", false)]          // another user
    [InlineData(@"Temp", false)]                                     // relative: would resolve against System32
    [InlineData(@"C:Temp", false)]
    [InlineData(@"%USERPROFILE%\AppData\Local\Temp", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_real_temp_folder_inside_LocalAppData_is_emptied(string? candidate, bool expected)
        => Assert.Equal(expected, SafePath.IsTempFolderInside(candidate, @"C:\Users\ana\AppData\Local"));

    [Fact]
    public void Temp_check_refuses_a_drive_root_as_LocalAppData()
        => Assert.False(SafePath.IsTempFolderInside(@"C:\Temp", @"C:\"));

    [Theory]
    [InlineData(@"C:\Users\ana\Desktop", true)]
    [InlineData(@"C:\Users\ana\OneDrive\Documents", true)]
    [InlineData(@"C:\Users\ana", false)]
    [InlineData(@"C:\Users\anabel\Desktop", false)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData(@"D:\", false)]
    [InlineData(@"Desktop", false)]
    [InlineData(@"C:\Users\ana\..\bob\Desktop", false)]
    public void Registry_folders_must_stay_inside_the_profile(string candidate, bool expected)
        => Assert.Equal(expected, SafePath.IsStrictlyInside(candidate, @"C:\Users\ana"));

    [Fact]
    public void Real_machine_folders_are_protected()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var drive = Path.GetPathRoot(win)!;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.True(SafePath.IsProtectedFolder(Path.Combine(drive, "Recovery")));
        Assert.True(SafePath.IsProtectedFolder(Path.Combine(drive, "$Recycle.Bin", "S-1-5-21-1")));
        Assert.True(SafePath.IsProtectedFolder(Path.Combine(drive, "Users")));
        Assert.True(SafePath.IsProtectedFolder(Path.Combine(profile, "Downloads")));
        Assert.True(SafePath.IsProtectedFolder(Path.Combine(profile, "Saved Games")));
        Assert.True(SafePath.IsProtectedFolder(Path.Combine(profile, "AppData", "LocalLow")));
        Assert.True(SafePath.IsProtectedFolder("relative"));
        Assert.True(SafePath.IsCriticalFolder(win));
        Assert.True(SafePath.IsCriticalFolder(profile));
        Assert.True(SafePath.IsCriticalFolder(drive));
        Assert.False(SafePath.IsCriticalFolder(Path.Combine(win, "Temp")));
        Assert.True(SafePath.IsInsideSystemOrProgramFolder(Path.Combine(win, "Temp")));
        Assert.True(SafePath.IsProtectedFromUserDelete(profile));
        Assert.False(SafePath.IsProtectedFromUserDelete(Path.Combine(profile, "Downloads", "old-installers")));
    }

    [Theory]
    [InlineData(@"C:\Users\ana\Downloads\setup.exe", true)]
    [InlineData(@"D:\Games\data.pak", true)]
    [InlineData(@"C:\Tools\lib.DLL", true)]
    [InlineData(@"C:\Users\ana\Pictures\photo.jpg", false)]
    public void Program_files_are_never_duplicates(string path, bool excluded)
        => Assert.Equal(excluded, DuplicateFinder.IsExcludedFileType(path));

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Game\movie.mp4", true)]
    [InlineData(@"C:\Users\ana\Documents\site\node_modules\x\big.bin", true)]
    [InlineData(@"C:\Users\ana\Documents\repo\.git\objects\pack\p.pack", true)]
    [InlineData(@"C:\Users\ana\Pictures\2020\photo.jpg", false)]
    public void Program_and_project_folders_are_excluded(string path, bool excluded)
        => Assert.Equal(excluded, DuplicateFinder.IsInExcludedFolder(path));

    [Fact]
    public void Copies_are_only_auto_selected_in_personal_folders()
    {
        string[] personal = [@"C:\Users\ana\Downloads", @"C:\Users\ana\Pictures"];
        Assert.True(DuplicateFinder.IsSafeToAutoSelect([@"C:\Users\ana\Downloads\a.jpg", @"C:\Users\ana\Pictures\a.jpg"], personal));
        // Copies in different program folders: each may be needed.
        Assert.False(DuplicateFinder.IsSafeToAutoSelect([@"D:\Tools\AppA\model.bin", @"D:\Tools\AppB\model.bin"], personal));
        Assert.False(DuplicateFinder.IsSafeToAutoSelect([@"C:\Users\ana\Downloads\a.jpg", @"D:\Backup\a.jpg"], personal));
        Assert.False(DuplicateFinder.IsSafeToAutoSelect([@"C:\Users\ana\Downloads\a.exe", @"C:\Users\ana\Pictures\a.exe"], personal));
        Assert.False(DuplicateFinder.IsSafeToAutoSelect([@"C:\Users\ana\Downloads\a.jpg", @"C:\Users\ana\Pictures\a.jpg"], []));
    }
}

/// <summary>The 24-hour rule for temporary folders.</summary>
public sealed class TempAgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinSolveTests", Guid.NewGuid().ToString("N"));

    public TempAgeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void MakeOld(string path, bool directory)
    {
        var old = DateTime.Now.AddDays(-3);
        if (directory)
        {
            Directory.SetCreationTime(path, old);
            Directory.SetLastWriteTime(path, old);
        }
        else
        {
            File.SetCreationTime(path, old);
            File.SetLastWriteTime(path, old);
        }
    }

    [Fact]
    public void Only_items_older_than_the_cutoff_are_deleted()
    {
        var oldFile = Path.Combine(_root, "old.tmp");
        var newFile = Path.Combine(_root, "new.tmp");
        File.WriteAllText(oldFile, "old");
        File.WriteAllText(newFile, "new");
        MakeOld(oldFile, directory: false);

        var oldEmpty = Directory.CreateDirectory(Path.Combine(_root, "old-empty")).FullName;
        var newEmpty = Directory.CreateDirectory(Path.Combine(_root, "new-empty")).FullName;
        var oldWithNew = Directory.CreateDirectory(Path.Combine(_root, "old-with-new")).FullName;
        File.WriteAllText(Path.Combine(oldWithNew, "fresh.tmp"), "fresh");
        MakeOld(oldEmpty, directory: true);
        MakeOld(oldWithNew, directory: true);

        SafeDelete.DeleteContents(_root, "*", recursive: true, default, DateTime.UtcNow.AddHours(-24));

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
        Assert.False(Directory.Exists(oldEmpty));
        Assert.True(Directory.Exists(newEmpty));
        Assert.True(File.Exists(Path.Combine(oldWithNew, "fresh.tmp")));
    }
}
