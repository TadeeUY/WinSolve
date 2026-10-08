using System.Collections.Concurrent;
using System.IO.Enumeration;

namespace WinSolve.Services;

/// <summary>Disk space tree node (folder, file or a group of small files).</summary>
public sealed class SpaceNode
{
    public string Name { get; }
    public SpaceNode? Parent { get; }
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

    private readonly record struct Entry(string Name, long Length, bool IsDirectory);

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

            void ScanDir(SpaceNode node, string dirPath, int depth)
            {
                ct.ThrowIfCancellationRequested();
                var subdirs = new List<(SpaceNode Node, string Path)>();
                long smallSize = 0, smallCount = 0;

                try
                {
                    var options = new EnumerationOptions
                    {
                        IgnoreInaccessible = true,
                        RecurseSubdirectories = false,
                        AttributesToSkip = FileAttributes.ReparsePoint, // never follow junctions/symlinks
                        ReturnSpecialDirectories = false,
                    };
                    var enumerable = new FileSystemEnumerable<Entry>(dirPath,
                        (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.Length, e.IsDirectory), options);

                    foreach (var entry in enumerable)
                    {
                        if (entry.IsDirectory)
                        {
                            var child = new SpaceNode(entry.Name, node, isFile: false);
                            lock (node.Children) node.Children.Add(child);
                            subdirs.Add((child, Path.Combine(dirPath, entry.Name)));
                            continue;
                        }

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
}
