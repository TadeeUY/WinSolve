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
        // App data, per-user programs (AppData\Local\Programs), Store apps and browser profiles.
        Path.Combine(InteractiveUser.Profile, "AppData"),
        InteractiveUser.LocalAppData,
        InteractiveUser.RoamingAppData,
        InteractiveUser.LocalLow,
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "$Recycle.Bin"),
        Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "System Volume Information"),
    ];

    /// <summary>
    /// Folders whose files belong to a program, game library or code project wherever they are:
    /// identical files there are normal and each copy is needed.
    /// </summary>
    private static readonly HashSet<string> ExcludedFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "steamapps", "SteamLibrary", "Steam", "Epic Games", "GOG Games", "GOG Galaxy", "XboxGames", "WindowsApps", "ModifiableWindowsApps",
        "Origin Games", "EA Games", "Ubisoft Game Launcher", "Battle.net", "Riot Games", "Rockstar Games",
        "User Data", "node_modules", ".git", ".svn", ".hg", ".venv", "venv", "site-packages", "__pycache__", ".gradle", ".m2", ".nuget", ".cargo", ".rustup",
        "$Recycle.Bin", "System Volume Information",
    };

    /// <summary>Programs, libraries and packed app data: never offered as duplicates to delete.</summary>
    private static readonly HashSet<string> ExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".drv", ".ocx", ".cpl", ".scr", ".efi", ".mui", ".winmd", ".cat", ".inf",
        ".msi", ".msp", ".msix", ".msixbundle", ".appx", ".appxbundle", ".cab",
        ".pak", ".asar", ".so", ".dylib", ".jar", ".node", ".pyd", ".lib", ".a", ".vpk", ".ucas", ".utoc", ".bundle", ".assets", ".resS",
    };

    /// <summary>True for a program/library-type file (by extension).</summary>
    internal static bool IsExcludedFileType(string path) => ExcludedExtensions.Contains(Path.GetExtension(path));

    /// <summary>True when any folder on the path is a program, game library or code project folder.</summary>
    internal static bool IsInExcludedFolder(string path)
    {
        var folder = Path.GetDirectoryName(path);
        return folder is not null && folder.Split('\\', StringSplitOptions.RemoveEmptyEntries).Any(ExcludedFolderNames.Contains);
    }

    /// <summary>
    /// A copy of the set may be selected automatically only when that is clearly harmless: every copy
    /// is a personal file (not a program file, not in a program or project folder) inside the person's
    /// own Desktop, Documents, Downloads, Pictures, Music, Videos or OneDrive. Copies spread over other
    /// folders may each be needed by a different program or game.
    /// </summary>
    internal static bool IsSafeToAutoSelect(IReadOnlyCollection<string> paths, IReadOnlyCollection<string> personalFolders)
        => paths.Count > 1 && paths.All(p =>
            !IsExcludedFileType(p) && !IsInExcludedFolder(p) &&
            personalFolders.Any(f => f.Length > 3 && SafePath.IsSameOrInside(p, f) && !p.TrimEnd('\\').Equals(f.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)));

    /// <summary>The signed-in user's personal folders; empty (nothing is auto-selected) if they can't be read.</summary>
    internal static List<string> PersonalFolders()
    {
        try
        {
            var list = new List<string>
            {
                InteractiveUser.Desktop, InteractiveUser.Documents, InteractiveUser.Downloads,
                InteractiveUser.Pictures, InteractiveUser.Music, InteractiveUser.Videos,
            };
            list.AddRange(InteractiveUser.OneDriveRoots());
            return list.Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p)).Select(p => p.TrimEnd('\\')).ToList();
        }
        catch (Exception ex)
        {
            Logger.Write($"Could not read the personal folders for duplicate selection: {ex.Message}");
            return [];
        }
    }

    private static List<DuplicateGroup> Find(SpaceNode root, long minSize, Action<string> status, CancellationToken ct)
    {
        var excluded = ExcludedRoots().Where(e => e.Length > 0).ToArray();
        // (Other users' folders never get into the scan tree; see DiskScanner.)
        var files = new List<(string Path, long Size)>();

        void Walk(SpaceNode n, string path)
        {
            foreach (var c in n.Children)
            {
                var childPath = Path.Combine(path, c.Name);
                if (c.IsFile)
                {
                    if (!c.IsGroup && c.Size >= minSize && !IsExcludedFileType(childPath)) files.Add((childPath, c.Size));
                }
                else if (!ExcludedFolderNames.Contains(c.Name) && !excluded.Any(e => SafePath.IsSameOrInside(childPath, e)))
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
