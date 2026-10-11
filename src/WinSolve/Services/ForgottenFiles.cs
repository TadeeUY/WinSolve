using WinSolve.Core;

namespace WinSolve.Services;

public sealed record ForgottenFile(string Path, long Size, DateTime LastUsed);

/// <summary>
/// Big files nobody has opened in a long time, and empty folders, in the signed-in user's own
/// folders only (not AppData, not other users, not online-only cloud files).
/// </summary>
public static class ForgottenFiles
{
    // Cloud-file (OneDrive Files On-Demand) attribute bits: online-only files take no space here.
    private const int RecallOnOpen = 0x40000, RecallOnDataAccess = 0x400000;

    private static IEnumerable<string> Roots()
    {
        var profile = InteractiveUser.Profile;
        if (!Directory.Exists(profile)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(profile, "*", SafePath.NoLinks(recursive: false)))
        {
            var name = System.IO.Path.GetFileName(dir);
            if (name.Equals("AppData", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.')) continue;
            var attr = File.GetAttributes(dir);
            if ((attr & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
            yield return dir;
        }
    }

    public static List<ForgottenFile> FindLarge(long minSize, TimeSpan notUsedFor, Action<string> status, CancellationToken ct)
    {
        var cutoff = DateTime.Now - notUsedFor;
        var list = new List<ForgottenFile>();
        foreach (var root in Roots())
        {
            status(root);
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", SafePath.NoLinks(recursive: true)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (f.Length < minSize || ((int)f.Attributes & (RecallOnOpen | RecallOnDataAccess)) != 0) continue;
                    var used = f.LastWriteTime > f.LastAccessTime ? f.LastWriteTime : f.LastAccessTime;
                    if (used < cutoff) list.Add(new ForgottenFile(f.FullName, f.Length, used));
                }
                catch (IOException) { }
            }
        }
        return list.OrderByDescending(f => f.Size).ToList();
    }

    /// <summary>Folders with no files anywhere inside (deepest first, so they can be deleted in order).</summary>
    public static List<string> FindEmptyFolders(CancellationToken ct)
    {
        var empty = new List<string>();
        bool Scan(string dir)
        {
            ct.ThrowIfCancellationRequested();
            var isEmpty = true;
            try
            {
                if (Directory.EnumerateFiles(dir, "*", SafePath.NoLinks(recursive: false)).Any()) isEmpty = false;
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", SafePath.NoLinks(recursive: false)))
                    if (!Scan(sub)) isEmpty = false;
                // Links (junctions) are never followed and never counted as empty.
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            if (isEmpty) empty.Add(dir);
            return isEmpty;
        }
        foreach (var root in Roots())
            foreach (var sub in Directory.EnumerateDirectories(root, "*", SafePath.NoLinks(recursive: false)))
                Scan(sub); // the known folders themselves (Documents, Downloads...) are never removed
        return empty.OrderByDescending(d => d.Length).ToList();
    }

    /// <summary>Removes the folders that are still empty; returns how many were removed.</summary>
    public static int DeleteEmptyFolders(IEnumerable<string> folders)
    {
        var removed = 0;
        foreach (var dir in folders.OrderByDescending(d => d.Length))
        {
            try
            {
                if (InteractiveUser.IsOtherUsersData(dir) || !SafePath.IsSameOrInside(dir, InteractiveUser.Profile)) continue;
                if (SafePath.HasReparsePoint(dir) || Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                Directory.Delete(dir, recursive: false);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return removed;
    }
}
