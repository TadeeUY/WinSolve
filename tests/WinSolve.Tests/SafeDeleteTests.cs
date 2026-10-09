using System.Diagnostics;
using WinSolve.Core;
using Xunit;

namespace WinSolve.Tests;

public sealed class SafeDeleteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinSolveTests", Guid.NewGuid().ToString("N"));
    private readonly string _victim;

    public SafeDeleteTests()
    {
        Directory.CreateDirectory(_root);
        _victim = Path.Combine(_root, "victim");
        Directory.CreateDirectory(_victim);
        File.WriteAllText(Path.Combine(_victim, "keep.txt"), "must survive");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Area(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A directory junction (no admin rights needed, unlike symbolic links).</summary>
    private static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true })!;
        p.WaitForExit();
        Assert.True(Directory.Exists(link), "could not create the test junction");
    }

    [Fact]
    public void Deletes_files_and_empty_subfolders_but_keeps_the_root()
    {
        var area = Area("clean");
        Directory.CreateDirectory(Path.Combine(area, "a", "b"));
        File.WriteAllText(Path.Combine(area, "x.tmp"), new string('x', 1000));
        File.WriteAllText(Path.Combine(area, "a", "b", "z.tmp"), "z");
        var ro = Path.Combine(area, "a", "ro.txt");
        File.WriteAllText(ro, "ro");
        File.SetAttributes(ro, FileAttributes.ReadOnly);

        var r = SafeDelete.DeleteContents(area, "*", recursive: true, default);

        Assert.True(Directory.Exists(area));
        Assert.Empty(Directory.EnumerateFileSystemEntries(area));
        Assert.True(r.Freed >= 1000);
    }

    [Fact]
    public void Never_follows_a_junction_inside_the_folder()
    {
        var area = Area("junction");
        File.WriteAllText(Path.Combine(area, "junk.tmp"), "junk");
        Junction(Path.Combine(area, "link"), _victim);

        SafeDelete.DeleteContents(area, "*", recursive: true, default);

        Assert.True(File.Exists(Path.Combine(_victim, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(area, "junk.tmp")));
    }

    [Fact]
    public void Refuses_a_root_that_is_a_junction()
    {
        var link = Path.Combine(_root, "rootlink");
        Junction(link, _victim);

        Assert.ThrowsAny<Exception>(() => SafeDelete.DeleteContents(link, "*", recursive: true, default));
        Assert.True(File.Exists(Path.Combine(_victim, "keep.txt")));
    }

    [Fact]
    public void Refuses_a_root_reached_through_a_junction()
    {
        var link = Path.Combine(_root, "parentlink");
        Junction(link, _root);
        // C:\...\parentlink\victim is really C:\...\victim, reached through a link.
        Assert.ThrowsAny<Exception>(() => SafeDelete.DeleteContents(Path.Combine(link, "victim"), "*", recursive: true, default));
        Assert.True(File.Exists(Path.Combine(_victim, "keep.txt")));
    }

    [Fact]
    public void Pattern_and_non_recursive_are_respected()
    {
        var area = Area("pattern");
        Directory.CreateDirectory(Path.Combine(area, "sub"));
        File.WriteAllText(Path.Combine(area, "thumbcache_1.db"), "1");
        File.WriteAllText(Path.Combine(area, "other.db"), "2");
        File.WriteAllText(Path.Combine(area, "sub", "thumbcache_2.db"), "3");

        SafeDelete.DeleteContents(area, "thumbcache_*.db", recursive: false, default);

        Assert.False(File.Exists(Path.Combine(area, "thumbcache_1.db")));
        Assert.True(File.Exists(Path.Combine(area, "other.db")));
        Assert.True(File.Exists(Path.Combine(area, "sub", "thumbcache_2.db")));
    }

    [Fact]
    public void Files_in_use_are_skipped()
    {
        var area = Area("locked");
        var locked = Path.Combine(area, "locked.tmp");
        using var fs = new FileStream(locked, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        File.WriteAllText(Path.Combine(area, "free.tmp"), "f");

        var r = SafeDelete.DeleteContents(area, "*", recursive: true, default);

        Assert.Equal(1, r.Skipped);
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(Path.Combine(area, "free.tmp")));
    }

    [Fact]
    public void Delete_tree_removes_the_folder_but_not_junction_targets()
    {
        var area = Area("tree");
        Directory.CreateDirectory(Path.Combine(area, "q", "w"));
        File.WriteAllText(Path.Combine(area, "q", "w", "e.txt"), "e");
        Junction(Path.Combine(area, "q", "link"), _victim);

        SafeDelete.DeleteTree(Path.Combine(area, "q", "w"), default);

        Assert.False(Directory.Exists(Path.Combine(area, "q", "w")));
        Assert.True(File.Exists(Path.Combine(_victim, "keep.txt")));
    }

    [Fact]
    public void Signed_in_user_is_this_account_in_a_normal_session()
    {
        // Tests don't run under over-the-shoulder elevation: everything maps to this account.
        Assert.Null(InteractiveUser.Other);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), InteractiveUser.LocalAppData);
        Assert.Equal(Path.GetTempPath(), InteractiveUser.TempPath);
    }
}

public class InstallerProcessTests
{
    [Fact]
    public async Task Waits_for_child_processes_of_a_self_extractor()
    {
        // The started process exits at once and leaves a child running ~3 s, like a driver
        // package that unpacks itself and launches the real setup.
        var script = Path.Combine(Path.GetTempPath(), $"winsolve-job-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script, "@start \"\" /b cmd /c ping -n 4 127.0.0.1 >nul\r\n@exit 0\r\n");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await InstallerProcess.RunAndWaitAsync(script, null, shellExecute: false, _ => { }, default);
            Assert.True(sw.Elapsed.TotalSeconds >= 2.5, $"returned after {sw.Elapsed.TotalSeconds:0.0} s");
        }
        finally
        {
            File.Delete(script);
        }
    }
}
