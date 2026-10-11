using System.Diagnostics;
using WinSolve.Core;
using Xunit;

namespace WinSolve.Tests;

public class UserIsolationTests
{
    [Fact]
    public void Data_folder_link_is_moved_aside_and_recreated()
    {
        if (!Admin.IsElevated) return;
        var root = Path.Combine(Path.GetTempPath(), "ws-trust-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "attacker");
        var data = Path.Combine(root, "WinSolve");
        Directory.CreateDirectory(target);
        try
        {
            using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{data}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true })!)
                p.WaitForExit();
            Assert.True((File.GetAttributes(data) & FileAttributes.ReparsePoint) != 0);

            SafePath.MakeTrusted(data);

            Assert.True(Directory.Exists(data));
            Assert.Equal((FileAttributes)0, File.GetAttributes(data) & FileAttributes.ReparsePoint);
            Assert.True(Directory.Exists(target)); // the link's target was left alone
            Assert.Contains(Directory.GetDirectories(root), d => Path.GetFileName(d).StartsWith("WinSolve.untrusted-", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var d in Directory.GetDirectories(root))
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) Directory.Delete(d);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Own_profile_is_not_other_users_data()
    {
        Assert.False(InteractiveUser.IsOtherUsersData(InteractiveUser.Profile));
        Assert.False(InteractiveUser.IsOtherUsersData(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "$Recycle.Bin", InteractiveUser.Sid.Value)));
        Assert.True(InteractiveUser.IsOtherUsersData(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "$Recycle.Bin", "S-1-5-21-1-2-3-1001", "x")));
    }

    [Fact]
    public void Other_users_paths_are_redacted()
        => Assert.Equal("no paths here", InteractiveUser.RedactOtherUsers("no paths here"));
}
