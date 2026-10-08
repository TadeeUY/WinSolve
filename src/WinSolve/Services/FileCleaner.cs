using System.Runtime.InteropServices;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>Error-tolerant deletion: skips files in use and counts the freed space.</summary>
public static class FileCleaner
{
    public static long DeleteContents(string directory, TaskContext ctx, string pattern = "*", bool recursive = true)
    {
        if (!Directory.Exists(directory)) return 0;
        long freed = 0;
        int skipped = 0;

        try
        {
            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint, // never follow junctions
            };

            foreach (var file in Directory.EnumerateFiles(directory, pattern, enumOptions))
            {
                ctx.Token.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(file);
                    var len = info.Length;
                    if (info.IsReadOnly) info.IsReadOnly = false;
                    info.Delete();
                    freed += len;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                }
            }

            if (recursive && pattern == "*")
            {
                // Remove now-empty folders, deepest first.
                foreach (var dir in Directory.EnumerateDirectories(directory, "*", enumOptions)
                             .OrderByDescending(d => d.Length))
                {
                    try { Directory.Delete(dir, recursive: false); } catch { /* not empty or in use */ }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ctx.Log($"  No access to {directory}: {ex.Message}");
        }

        ctx.Log($"  {directory}: {Format.Bytes(freed)} freed" + (skipped > 0 ? $" ({skipped} files in use skipped)" : ""));
        ctx.FreedBytes += freed;
        return freed;
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
