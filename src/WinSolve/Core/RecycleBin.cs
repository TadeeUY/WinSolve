using System.Runtime.InteropServices;
using Microsoft.Win32;

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

    /// <summary>
    /// Moves a file or folder to the Recycle Bin. Something that can't go there (network or removable
    /// drive, bin turned off, larger than the bin) is only deleted permanently if the person agrees.
    /// </summary>
    public static bool Send(string path, IntPtr owner)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return false;
        var permanent = false;
        if (WhyNotRecyclable(path) is { } reason)
        {
            Logger.Write($"{path} can't go to the Recycle Bin: {reason}");
            var window = owner == IntPtr.Zero ? null : new Owner(owner);
            if (Localization.Loc.Show(window, $"'{path}' can't be moved to the Recycle Bin: {reason}.\n\nDelete it permanently? It can't be restored afterwards.",
                    "Delete permanently", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return false;
            permanent = true;
        }

        // Even when the checks above find nothing, the shell itself asks before it destroys something
        // it can't recycle (FOF_WANTNUKEWARNING) instead of deleting it silently.
        var flags = permanent ? FOF_NOCONFIRMATION : (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING);

        // Under another administrator's account, the shell would put it in the administrator's
        // bin (where the signed-in user can't restore it) and could delete what the user may not.
        // Recycle it as the signed-in user instead, with their own rights.
        if (InteractiveUser.IsDifferent) return SendAsUser(path, flags);
        var op = new SHFILEOPSTRUCT
        {
            hwnd = owner,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = flags,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    private sealed class Owner(IntPtr handle) : IWin32Window
    {
        public IntPtr Handle => handle;
    }

    private static bool SendAsUser(string path, ushort flags)
    {
        // The same shell call as for our own account, so it warns the same way before destroying
        // anything it can't recycle. The path travels base64-encoded: no quoting to get wrong.
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(path));
        var script = $$"""
            $src = @'
            using System;
            using System.Runtime.InteropServices;
            public static class WinSolveRecycle {
                [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
                public struct Op { public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags; public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle; }
                [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref Op op);
                public static int Delete(string path, ushort flags) { var op = new Op { wFunc = 3, pFrom = path + "\0\0", fFlags = flags }; return SHFileOperation(ref op); }
            }
            '@
            Add-Type -TypeDefinition $src
            $path = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{encoded}}'))
            [void][WinSolveRecycle]::Delete($path, {{flags}})
            """;
        InteractiveUser.RunPowerShellAsUser(script, 10 * 60 * 1000);
        for (int i = 0; i < 20 && (File.Exists(path) || Directory.Exists(path)); i++) Thread.Sleep(100);
        return !File.Exists(path) && !Directory.Exists(path);
    }

    /// <summary>
    /// Why Windows would delete this permanently instead of recycling it (in English, translated
    /// when shown), or null when it can go to the Recycle Bin as far as can be told.
    /// </summary>
    internal static string? WhyNotRecyclable(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return "it is on a network location";
            var root = Path.GetPathRoot(full)!;
            switch (new DriveInfo(root).DriveType)
            {
                case DriveType.Fixed: break;
                case DriveType.Network: return "it is on a network drive";
                case DriveType.Removable: return "it is on a removable drive (such as a USB stick or memory card)";
                default: return "this drive has no Recycle Bin";
            }

            if (PolicyDisablesBin()) return "the Recycle Bin is turned off on this PC";
            var (nuke, maxBytes) = BinSettings(root);
            if (nuke) return "the Recycle Bin is turned off for this drive";
            if (maxBytes > 0 && SizeExceeds(full, maxBytes)) return "it is larger than the Recycle Bin of this drive";
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Write($"Could not check whether {path} can be recycled: {ex.Message}");
            return null; // the shell's own warning (FOF_WANTNUKEWARNING) still applies
        }
    }

    private static bool PolicyDisablesBin()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var root = InteractiveUser.OpenBase(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");
                if (key?.GetValue("NoRecycleFiles") is int v && v != 0) return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return false;
    }

    /// <summary>The signed-in user's Recycle Bin settings for a drive: turned off, and its maximum size (0 = unknown).</summary>
    private static (bool Nuke, long MaxBytes) BinSettings(string root)
    {
        var name = new System.Text.StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(root, name, name.Capacity)) return (false, 0);
        var volume = name.ToString(); // \\?\Volume{GUID}\
        var open = volume.IndexOf('{');
        var close = volume.IndexOf('}');
        if (open < 0 || close < open) return (false, 0);
        try
        {
            using var hkcu = InteractiveUser.OpenBase(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = hkcu.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + volume[open..(close + 1)]);
            if (key is null) return (false, 0);
            var nuke = key.GetValue("NukeOnDelete") is int n && n != 0;
            var max = key.GetValue("MaxCapacity") is int mb && mb > 0 ? mb * (1L << 20) : 0;
            return (nuke, max);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, 0);
        }
    }

    /// <summary>True when the file or folder is larger than <paramref name="limit"/> (stops counting once it is).</summary>
    private static bool SizeExceeds(string full, long limit)
    {
        if (File.Exists(full)) return new FileInfo(full).Length > limit;
        if (!Directory.Exists(full)) return false;
        long total = 0;
        foreach (var f in new DirectoryInfo(full).EnumerateFiles("*", SafePath.NoLinks(recursive: true)))
        {
            try { total += f.Length; } catch (IOException) { }
            if (total > limit) return true;
        }
        return false;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, System.Text.StringBuilder volumeName, int size);

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
