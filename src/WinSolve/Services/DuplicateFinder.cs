using System.Security.Cryptography;
using WinSolve.Core;

namespace WinSolve.Services;

public sealed record DuplicateFile(string Path, long Size, DateTime Modified);

public sealed record DuplicateGroup(string Hash, long Size, List<DuplicateFile> Files)
{
    /// <summary>Space recovered by keeping a single copy.</summary>
    public long Wasted => Size * (Files.Count - 1);
}

/// <summary>
/// Finds identical files among the files of a disk scan: same size, then a quick hash of the
/// first and last 64 KB, then a full SHA-256. System and program folders are skipped because
/// duplicates there are normal and deleting them breaks things.
/// </summary>
public static class DuplicateFinder
{
    private const int Chunk = 64 * 1024;

    public static async Task<List<DuplicateGroup>> FindAsync(SpaceNode root, long minSize, Action<string> status, CancellationToken ct)
        => await Task.Run(() => Find(root, minSize, status, ct), ct);

    private static string[] ExcludedRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Path.Combine(InteractiveUser.LocalAppData, "Packages"),
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "$Recycle.Bin"),
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "System Volume Information"),
    ];

    private static List<DuplicateGroup> Find(SpaceNode root, long minSize, Action<string> status, CancellationToken ct)
    {
        var excluded = ExcludedRoots().Where(e => e.Length > 0).ToArray();
        var files = new List<(string Path, long Size)>();

        void Walk(SpaceNode n, string path)
        {
            foreach (var c in n.Children)
            {
                var childPath = Path.Combine(path, c.Name);
                if (c.IsFile)
                {
                    if (!c.IsGroup && c.Size >= minSize) files.Add((childPath, c.Size));
                }
                else if (!excluded.Any(e => SafePath.IsSameOrInside(childPath, e)))
                {
                    Walk(c, childPath);
                }
            }
        }
        Walk(root, root.FullPath);

        var bySize = files.GroupBy(f => f.Size).Where(g => g.Count() > 1).ToList();
        status($"{files.Count:N0} files checked, {bySize.Sum(g => g.Count()):N0} share a size with another file. Comparing contents...");

        var groups = new List<DuplicateGroup>();
        var done = 0;
        foreach (var sizeGroup in bySize)
        {
            ct.ThrowIfCancellationRequested();
            // Hard links are one file with several names: deleting one frees nothing.
            var distinct = sizeGroup.DistinctBy(f => FileId(f.Path) ?? f.Path).ToList();
            if (distinct.Count < 2) continue;
            var quick = distinct.Select(f => (f.Path, f.Size, Hash: TryHash(f.Path, quickOnly: true, ct)))
                .Where(f => f.Hash is not null)
                .GroupBy(f => f.Hash!)
                .Where(g => g.Count() > 1);

            foreach (var q in quick)
            {
                var full = q.Select(f => (f.Path, f.Size, Hash: f.Size <= Chunk * 2 ? f.Hash : TryHash(f.Path, quickOnly: false, ct)))
                    .Where(f => f.Hash is not null)
                    .GroupBy(f => f.Hash!)
                    .Where(g => g.Count() > 1);
                foreach (var g in full)
                {
                    groups.Add(new DuplicateGroup(g.Key, sizeGroup.Key,
                        g.Select(f => new DuplicateFile(f.Path, f.Size, SafeModified(f.Path))).OrderBy(f => f.Modified).ToList()));
                }
            }
            done++;
            if (done % 20 == 0) status($"Comparing contents... {done:N0}/{bySize.Count:N0} size groups, {groups.Count:N0} duplicate sets so far");
        }

        return groups.OrderByDescending(g => g.Wasted).ToList();
    }

    private static DateTime SafeModified(string path)
    {
        try { return File.GetLastWriteTime(path); } catch { return DateTime.MinValue; }
    }

    /// <summary>Volume serial + file index: the same for every hard link of a file.</summary>
    private static string? FileId(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return GetFileInformationByHandle(fs.SafeFileHandle, out var info)
                ? $"{info.VolumeSerialNumber:X8}-{info.FileIndexHigh:X8}{info.FileIndexLow:X8}"
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryHash(string path, bool quickOnly, CancellationToken ct)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Chunk, FileOptions.SequentialScan);
            using var sha = SHA256.Create();
            if (!quickOnly)
            {
                // Chunked so closing the dialog stops hashing a 60 GB file right away.
                var chunk = new byte[1 << 20];
                int n;
                while ((n = fs.Read(chunk, 0, chunk.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.TransformBlock(chunk, 0, n, null, 0);
                }
                sha.TransformFinalBlock([], 0, 0);
                return Convert.ToHexString(sha.Hash!);
            }

            var buffer = new byte[Chunk];
            var read = fs.Read(buffer, 0, Chunk);
            sha.TransformBlock(buffer, 0, read, null, 0);
            if (fs.Length > Chunk * 2)
            {
                fs.Seek(-Chunk, SeekOrigin.End);
                read = fs.Read(buffer, 0, Chunk);
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // locked or inaccessible
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);
}
