using System.Runtime.InteropServices;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>Error-tolerant deletion: skips files in use and counts the freed space.</summary>
public static class FileCleaner
{
    public static long DeleteContents(string directory, TaskContext ctx, string pattern = "*", bool recursive = true)
    {
        if (!Directory.Exists(directory)) return 0;
        if (SafePath.HasReparsePoint(directory))
        {
            // Never follow a junction/symlink planted in a user-writable folder.
            ctx.Log($"  Skipped {directory}: it is (or is inside) a junction or symbolic link.");
            return 0;
        }

        SafeDelete.Result result;
        try
        {
            // Handle-based: links inside are never followed, even if swapped in mid-cleanup.
            result = SafeDelete.DeleteContents(directory, pattern, recursive, ctx.Token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            ctx.Log($"  Skipped {directory}: {ex.Message}");
            return 0;
        }

        ctx.Log($"  {directory}: {Format.Bytes(result.Freed)} freed" + (result.Skipped > 0 ? $" ({result.Skipped} files in use skipped)" : ""));
        ctx.FreedBytes += result.Freed;
        return result.Freed;
    }

    public static long DirectorySize(string directory)
    {
        if (!Directory.Exists(directory)) return 0;
        long total = 0;
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in Directory.EnumerateFiles(directory, "*", opts))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return total;
    }

    // ---- Recycle Bin ----

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    public static long RecycleBinSize()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
    }

    public static void EmptyRecycleBin(TaskContext ctx)
    {
        var size = RecycleBinSize();
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        // S_OK, or an error because the bin was already empty
        if (hr == 0 || size == 0)
        {
            ctx.FreedBytes += size;
            ctx.Log($"  Recycle Bin emptied: {Format.Bytes(size)}");
        }
        else
        {
            ctx.Log($"  Could not empty the Recycle Bin (HRESULT 0x{hr:X8})");
        }
    }
}
