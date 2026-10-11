using System.Runtime.InteropServices;

namespace WinSolve.Core;

/// <summary>Moves files and folders to the Recycle Bin (undoable delete).</summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_WANTNUKEWARNING = 0x4000;

    public static bool Send(string path, IntPtr owner)
    {
        // Under another administrator's account, the shell would put it in the administrator's
        // bin (where the signed-in user can't restore it) and could delete what the user may not.
        // Recycle it as the signed-in user instead, with their own rights.
        if (InteractiveUser.IsDifferent) return SendAsUser(path);
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    private static bool SendAsUser(string path)
    {
        var quoted = "'" + path.Replace("'", "''") + "'";
        var call = Directory.Exists(path) ? "DeleteDirectory" : "DeleteFile";
        var script = "Add-Type -AssemblyName Microsoft.VisualBasic; " +
                     $"[Microsoft.VisualBasic.FileIO.FileSystem]::{call}({quoted}, 'OnlyErrorDialogs', 'SendToRecycleBin')";
        InteractiveUser.RunPowerShellAsUser(script, 10 * 60 * 1000);
        for (int i = 0; i < 20 && (File.Exists(path) || Directory.Exists(path)); i++) Thread.Sleep(100);
        return !File.Exists(path) && !Directory.Exists(path);
    }

    /// <summary>Size of the signed-in user's own Recycle Bin folders on all fixed drives.</summary>
    public static long UserBinSize()
    {
        long total = 0;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                var bin = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin", InteractiveUser.Sid.Value);
                if (!Directory.Exists(bin) || SafePath.HasReparsePoint(bin)) continue;
                foreach (var f in new DirectoryInfo(bin).EnumerateFiles("*", SafePath.NoLinks(recursive: true))) total += f.Length;
            }
            catch { }
        }
        return total;
    }
}
