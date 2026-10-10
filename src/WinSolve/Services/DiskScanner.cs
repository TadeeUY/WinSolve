using System.Collections.Concurrent;
using System.IO.Enumeration;
using WinSolve.Core;

namespace WinSolve.Services;

/// <summary>Disk space tree node (folder, file or a group of small files).</summary>
public sealed class SpaceNode
{
    public string Name { get; }
    public SpaceNode? Parent { get; private set; }
    public bool IsFile { get; }

    /// <summary>Groups many small files to keep memory usage low.</summary>
    public bool IsGroup { get; init; }
    public long Size { get; set; }
    public long FileCount { get; set; }
    public List<SpaceNode> Children { get; } = [];

    public SpaceNode(string name, SpaceNode? parent, bool isFile)
    {
        Name = name;
        Parent = parent;
        IsFile = isFile;
    }

    public string Extension => IsFile && !IsGroup ? System.IO.Path.GetExtension(Name).ToLowerInvariant() : "";

    public string FullPath
    {
        get
        {
            if (Parent is null) return Name;
            var parent = Parent.FullPath;
            return IsGroup ? parent : System.IO.Path.Combine(parent, Name);
        }
    }

    public string DisplayName => IsFile ? Name : (Parent is null ? Name : Name + "\\");

    public void SortRecursive()
    {
        Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        foreach (var c in Children) if (!c.IsFile) c.SortRecursive();
    }

    /// <summary>Removes the node and subtracts its size from every parent.</summary>
    public void Detach()
    {
        if (Parent is null) return;
        Parent.Children.Remove(this);
        for (var p = Parent; p is not null; p = p.Parent)
        {
            p.Size -= Size;
            p.FileCount -= IsFile ? 1 : FileCount;
        }
        // Detached: a second Detach (or a delete from inside this subtree) must not subtract again.
        Parent = null;
    }
}

public sealed record ExtensionStat(string Extension, long Size, long Count);

public sealed class SpaceScanResult
{
    public required SpaceNode Root { get; init; }
    public required List<ExtensionStat> Extensions { get; init; }
    public TimeSpan Elapsed { get; init; }
    public long Inaccessible { get; init; }
}

/// <summary>
/// Scans a drive or folder in parallel, WizTree/WinDirStat style.
/// Files under 1 MB are grouped per folder to keep memory usage low.
/// </summary>
public static class DiskScanner
{
    private const long SmallFileThreshold = 1L << 20;

    private readonly record struct Entry(string Name, long Length, bool IsDirectory, FileAttributes Attributes);

    // Cloud-file (OneDrive Files On-Demand) attribute bits.
    private const int RecallOnOpen = 0x40000, Pinned = 0x80000, Unpinned = 0x100000, RecallOnDataAccess = 0x400000;

    public sealed class Progress
    {
        public long Files;
        public long Bytes;
        public string Current = "";
    }

    public static Task<SpaceScanResult> ScanAsync(string path, Progress progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var root = new SpaceNode(path.TrimEnd('\\') + (path.Length <= 3 ? "\\" : ""), null, isFile: false);
            var ext = new ConcurrentDictionary<string, (long Size, long Count)>(StringComparer.OrdinalIgnoreCase);
            long inaccessible = 0;
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string? winSxS = windows.Length > 0 ? Path.Combine(windows, "WinSxS") : null;

            void ScanDir(SpaceNode node, string dirPath, int depth)
            {
                ct.ThrowIfCancellationRequested();
                // Most of WinSxS is the same files as System32 & co. (hard links): count them once,
                // where Windows uses them, like "Analyze Component Store" does.
                var sharedLinks = winSxS is not null && SafePath.IsSameOrInside(dirPath, winSxS);
                var subdirs = new List<(SpaceNode Node, string Path)>();
                long smallSize = 0, smallCount = 0;

                try
                {
                    var options = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = false,
                        // Reparse points are filtered below: junctions/symlinks are never followed,
                        // but OneDrive (Files On-Demand) folders and files are reparse points too.
                        AttributesToSkip = 0,
                        ReturnSpecialDirectories = false,
                    };
                    var enumerable = new FileSystemEnumerable<Entry>(dirPath,
                        (ref FileSystemEntry e) => new Entry(e.FileName.ToString(),
                            // Online-only cloud files take no space on this disk.
                            ((int)e.Attributes & RecallOnDataAccess) != 0 && !e.IsDirectory ? 0 : e.Length,
                            e.IsDirectory, e.Attributes), options);

                    foreach (var item in enumerable)
                    {
                        var entry = item;
                        var reparse = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                        var cloud = ((int)entry.Attributes & (RecallOnOpen | RecallOnDataAccess | Pinned | Unpinned)) != 0;
                        if (reparse && !cloud) continue; // junction, symlink, mount point
                        if (entry.IsDirectory)
                        {
                            var child = new SpaceNode(entry.Name, node, isFile: false);
                            lock (node.Children) node.Children.Add(child);
                            subdirs.Add((child, Path.Combine(dirPath, entry.Name)));
                            continue;
                        }

                        if (sharedLinks && entry.Length > 0 && LinkCount(Path.Combine(dirPath, entry.Name)) > 1)
                            entry = entry with { Length = 0 };

                        node.Size += entry.Length;
                        node.FileCount++;
                        Interlocked.Increment(ref progress.Files);
                        Interlocked.Add(ref progress.Bytes, entry.Length);

                        var e = Path.GetExtension(entry.Name);
                        ext.AddOrUpdate(e.Length == 0 ? "(no extension)" : e.ToLowerInvariant(),
                            (entry.Length, 1), (_, v) => (v.Size + entry.Length, v.Count + 1));

                        if (entry.Length >= SmallFileThreshold)
                        {
                            lock (node.Children) node.Children.Add(new SpaceNode(entry.Name, node, isFile: true) { Size = entry.Length, FileCount = 1 });
                        }
                        else
                        {
                            smallSize += entry.Length;
                            smallCount++;
                        }
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    Interlocked.Increment(ref inaccessible);
                }

                if (smallCount > 0)
                {
                    lock (node.Children)
                        node.Children.Add(new SpaceNode($"[{smallCount:N0} small files]", node, isFile: true)
                        {
                            IsGroup = true, Size = smallSize, FileCount = smallCount,
                        });
                }

                if (depth <= 2) progress.Current = dirPath;

                if (depth < 3 && subdirs.Count > 1)
                {
                    Parallel.ForEach(subdirs, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
                        s => ScanDir(s.Node, s.Path, depth + 1));
                }
                else
                {
                    foreach (var s in subdirs) ScanDir(s.Node, s.Path, depth + 1);
                }

                foreach (var s in subdirs)
                {
                    node.Size += s.Node.Size;
                    node.FileCount += s.Node.FileCount;
                }
            }

            ScanDir(root, path, 0);
            root.SortRecursive();

            var stats = ext.Select(kv => new ExtensionStat(kv.Key, kv.Value.Size, kv.Value.Count))
                .OrderByDescending(s => s.Size).ToList();
            return new SpaceScanResult { Root = root, Extensions = stats, Elapsed = sw.Elapsed, Inaccessible = inaccessible };
        }, ct);

    /// <summary>How many names (hard links) the file has; 1 when it can't be read.</summary>
    private static uint LinkCount(string path)
    {
        using var h = CreateFile(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) return 1;
        return GetFileInformationByHandle(h, out var info) ? info.NumberOfLinks : 1;
    }

    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, FileShare share, IntPtr security,
        FileMode mode, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);
}
