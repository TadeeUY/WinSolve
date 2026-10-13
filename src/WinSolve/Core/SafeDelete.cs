using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinSolve.Core;

/// <summary>
/// Deletes files and folders without ever following junctions or symbolic links, even if someone
/// swaps a folder for a link while the cleanup is running.
/// </summary>
/// <remarks>
/// Path-based deletion (enumerate, then delete by path) can be redirected: a standard user can
/// replace a folder inside %TEMP% with a junction to System32 after it was enumerated, and an
/// elevated delete would then follow it. Here every child is opened relative to its parent's
/// handle (NtCreateFile with RootDirectory) with FILE_OPEN_REPARSE_POINT, and deleted through
/// that same handle, so a swapped-in link is opened (and skipped) as the link itself.
/// </remarks>
public static class SafeDelete
{
    public readonly record struct Result(long Freed, int Skipped);

    private const int MaxDepth = 128;

    /// <summary>
    /// Deletes the files in <paramref name="root"/> that match <paramref name="pattern"/> (in
    /// subfolders too when <paramref name="recursive"/>). With the "*" pattern, emptied subfolders
    /// are removed as well. The root itself is kept. With <paramref name="olderThanUtc"/>, files and
    /// folders created or modified after that moment are kept (a program may still be using them).
    /// </summary>
    public static Result DeleteContents(string root, string pattern, bool recursive, CancellationToken ct, DateTime? olderThanUtc = null)
    {
        using var dir = OpenRoot(root, delete: false);
        var stats = new Stats { Cutoff = olderThanUtc?.ToFileTimeUtc() ?? 0 };
        Walk(dir, pattern, recursive, removeDirs: pattern == "*", stats, depth: 0, ct);
        return new Result(stats.Freed, stats.Skipped);
    }

    /// <summary>Deletes a folder and everything in it (links inside are removed, never followed).</summary>
    public static Result DeleteTree(string root, CancellationToken ct)
    {
        var stats = new Stats();
        using (var dir = OpenRoot(root, delete: true))
        {
            Walk(dir, "*", recursive: true, removeDirs: true, stats, depth: 0, ct);
            if (!MarkForDeletion(dir, FileAttributes.Directory)) stats.Skipped++;
        }
        return new Result(stats.Freed, stats.Skipped);
    }

    private sealed class Stats
    {
        public long Freed;
        public int Skipped;
        public long Cutoff; // FILETIME (UTC); 0 = no age limit

        /// <summary>Created or written at or after the cutoff: kept.</summary>
        public bool IsRecent(long newest) => Cutoff != 0 && newest >= Cutoff;
    }

    // ───────────── Walking ─────────────

    private static void Walk(SafeFileHandle dir, string pattern, bool recursive, bool removeDirs, Stats stats, int depth, CancellationToken ct)
    {
        foreach (var entry in List(dir))
        {
            ct.ThrowIfCancellationRequested();
            var isDir = (entry.Attributes & FileAttributes.Directory) != 0;
            if (isDir && !recursive) continue;
            if (!isDir && !FileSystemName.MatchesSimpleExpression(pattern, entry.Name)) continue;
            // Too new: kept, and a recent folder is not even entered (an installer that is running
            // extracts files that keep their old dates). Checked again on the handle below.
            if (stats.IsRecent(entry.Newest)) continue;

            // Only the access that is needed: DELETE on a folder fails while it is another
            // process's working directory, which must not hide its whole contents.
            using var child = isDir
                ? (removeDirs ? OpenRelative(dir, entry.Name, true, DELETE) : null) ?? OpenRelative(dir, entry.Name, true, 0)
                : OpenRelative(dir, entry.Name, false, DELETE);
            if (child is null)
            {
                if (!isDir) stats.Skipped++; // in use or access denied
                continue;
            }

            // Trust what the handle says, not the listing: the entry may have been swapped since.
            // (A folder's times are read here, before deleting its contents updates them.)
            if (!GetFileInformationByHandle(child, out var info)) continue;
            var a = (FileAttributes)info.dwFileAttributes;
            if ((a & FileAttributes.ReparsePoint) != 0) continue; // a link: never follow or touch
            var reallyDir = (a & FileAttributes.Directory) != 0;
            if (reallyDir != isDir) continue;
            if (stats.Cutoff != 0 && stats.IsRecent(Newest(child, info))) continue; // changed since the listing

            if (reallyDir)
            {
                if (depth >= MaxDepth) continue;
                Walk(child, pattern, recursive, removeDirs, stats, depth + 1, ct);
                if (removeDirs) MarkForDeletion(child, a); // fails harmlessly if not empty
            }
            else if (MarkForDeletion(child, a) || DeleteReadOnly(dir, entry.Name, a))
            {
                stats.Freed += entry.Size;
            }
            else
            {
                stats.Skipped++;
            }
        }
    }

    /// <param name="Newest">The latest of creation, last-write and change time (FILETIME, UTC).</param>
    private readonly record struct Entry(string Name, FileAttributes Attributes, long Size, long Newest);

    /// <summary>Lists a directory through its handle (no path lookup that could be redirected).</summary>
    private static List<Entry> List(SafeFileHandle dir)
    {
        var entries = new List<Entry>();
        var buffer = Marshal.AllocHGlobal(64 * 1024);
        try
        {
            var cls = FileFullDirectoryRestartInfo;
            while (GetFileInformationByHandleEx(dir, cls, buffer, 64 * 1024))
            {
                cls = FileFullDirectoryInfo;
                var offset = 0;
                while (true)
                {
                    var p = buffer + offset;
                    var next = Marshal.ReadInt32(p, 0);
                    var newest = Math.Max(Marshal.ReadInt64(p, 8), Math.Max(Marshal.ReadInt64(p, 24), Marshal.ReadInt64(p, 32))); // creation, last-write, change
                    var size = Marshal.ReadInt64(p, 40);
                    var attributes = (FileAttributes)Marshal.ReadInt32(p, 56);
                    var nameBytes = Marshal.ReadInt32(p, 60);
                    var name = Marshal.PtrToStringUni(p + 68, nameBytes / 2);
                    if (name is not ("." or "..")) entries.Add(new Entry(name, attributes, size, newest));
                    if (next == 0) break;
                    offset += next;
                }
            }
            // ERROR_NO_MORE_FILES ends the listing; anything else just means we stop early.
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return entries;
    }

    // ───────────── Opening ─────────────

    private static SafeFileHandle OpenRoot(string root, bool delete)
    {
        var full = Path.GetFullPath(root).TrimEnd('\\');
        var access = FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE | (delete ? DELETE | FILE_WRITE_ATTRIBUTES : 0);
        var h = CreateFileW(full, access, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot open {full}");

        if (GetAttributes(h) is not { } a || (a & FileAttributes.Directory) == 0 || (a & FileAttributes.ReparsePoint) != 0)
        {
            h.Dispose();
            throw new IOException($"{full} is not a regular folder (it is a link or a file).");
        }

        // The handle must be the folder at the expected place, not one reached through a link
        // somewhere along the path.
        var actual = FinalPath(h);
        var expected = ResolveDrive(LongPath(full));
        if (actual is null || !string.Equals(actual.TrimEnd('\\'), expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            h.Dispose();
            throw new IOException($"{full} resolves to {actual ?? "an unknown location"}; skipped because it goes through a link.");
        }
        return h;
    }

    /// <summary>Older Windows / FAT: clearing read-only needs write-attributes access, asked only then.</summary>
    private static bool DeleteReadOnly(SafeFileHandle dir, string name, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReadOnly) == 0) return false;
        using var h = OpenRelative(dir, name, false, DELETE | FILE_WRITE_ATTRIBUTES);
        if (h is null) return false;
        var a = GetAttributes(h);
        return a is { } attrs && (attrs & FileAttributes.ReparsePoint) == 0 && MarkForDeletion(h, attrs);
    }

    private static SafeFileHandle? OpenRelative(SafeFileHandle parent, string name, bool directory, uint extraAccess)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UNICODE_STRING>());
        try
        {
            Marshal.StructureToPtr(new UNICODE_STRING
            {
                Length = (ushort)(name.Length * 2),
                MaximumLength = (ushort)(name.Length * 2),
                Buffer = nameBuffer,
            }, unicode, false);

            var success = false;
            parent.DangerousAddRef(ref success);
            try
            {
                var oa = new OBJECT_ATTRIBUTES
                {
                    Length = (uint)Marshal.SizeOf<OBJECT_ATTRIBUTES>(),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = unicode,
                    Attributes = OBJ_CASE_INSENSITIVE,
                };
                var access = FILE_READ_ATTRIBUTES | SYNCHRONIZE | extraAccess | (directory ? FILE_LIST_DIRECTORY : 0);
                var options = FILE_OPEN_REPARSE_POINT | FILE_SYNCHRONOUS_IO_NONALERT | (directory ? FILE_DIRECTORY_FILE : FILE_NON_DIRECTORY_FILE);
                var status = NtCreateFile(out var handle, access, ref oa, out _, IntPtr.Zero, 0, FILE_SHARE_ALL, FILE_OPEN, options, IntPtr.Zero, 0);
                if (status != 0)
                {
                    handle.Dispose();
                    return null;
                }
                return handle;
            }
            finally
            {
                if (success) parent.DangerousRelease();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(unicode);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    // ───────────── Deleting ─────────────

    /// <summary>Deletes through the open handle (takes effect when it closes).</summary>
    private static bool MarkForDeletion(SafeFileHandle h, FileAttributes attributes)
    {
        // Windows 10 1809+: delete now (POSIX semantics) and ignore the read-only attribute.
        var ex = new FILE_DISPOSITION_INFO_EX { Flags = FILE_DISPOSITION_FLAG_DELETE | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE };
        if (SetFileInformationByHandle(h, FileDispositionInfoEx, ref ex, (uint)Marshal.SizeOf<FILE_DISPOSITION_INFO_EX>()))
            return true;

        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            var remaining = attributes & ~FileAttributes.ReadOnly;
            var basic = new FILE_BASIC_INFO { FileAttributes = (uint)(remaining == 0 ? FileAttributes.Normal : remaining) };
            SetFileInformationByHandle(h, FileBasicInfo, ref basic, (uint)Marshal.SizeOf<FILE_BASIC_INFO>());
        }
        var info = new FILE_DISPOSITION_INFO { DeleteFile = 1 };
        return SetFileInformationByHandle(h, FileDispositionInfo, ref info, (uint)Marshal.SizeOf<FILE_DISPOSITION_INFO>());
    }

    /// <summary>
    /// Deletes one file through a handle opened on the entry itself: a link is deleted as the link
    /// (never what it points to), and a path that reaches the file through a junction or symbolic
    /// link along the way is refused. Nothing happens if the file doesn't exist.
    /// </summary>
    public static void DeleteFile(string path)
    {
        var full = Path.GetFullPath(path);
        using var h = CreateFileW(full, DELETE | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return; // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND
            throw new Win32Exception(error, $"Cannot open {full}");
        }
        if (GetAttributes(h) is not { } a || (a & FileAttributes.Directory) != 0)
            throw new IOException($"{full} is not a file.");
        if (!IsAtPath(h, full))
            throw new IOException($"{full} is reached through a link; not deleted.");
        if (!MarkForDeletion(h, a))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Cannot delete {full}");
    }

    /// <summary>Deletes the file or folder an open handle (with DELETE access) refers to, when the handle closes.</summary>
    internal static bool DeleteThroughHandle(SafeFileHandle h)
        => GetAttributes(h) is { } a && MarkForDeletion(h, a);

    /// <summary>
    /// Renames the file an open handle (with DELETE access) refers to, inside its own folder: no
    /// path is looked up, so a folder swapped for a link can't redirect it.
    /// </summary>
    internal static bool RenameThroughHandle(SafeFileHandle h, string newName)
    {
        if (newName.Length == 0 || newName.IndexOfAny(['\\', '/', ':']) >= 0) return false;
        var nameOffset = (int)Marshal.OffsetOf<FILE_RENAME_INFO>(nameof(FILE_RENAME_INFO.FileNameLength)) + sizeof(uint);
        var size = Math.Max(nameOffset + (newName.Length + 1) * 2, Marshal.SizeOf<FILE_RENAME_INFO>());
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.StructureToPtr(new FILE_RENAME_INFO { Flags = 0, RootDirectory = IntPtr.Zero, FileNameLength = (uint)(newName.Length * 2) }, buffer, false);
            Marshal.Copy(newName.ToCharArray(), 0, buffer + nameOffset, newName.Length);
            return SetFileInformationByHandle(h, FileRenameInfo, buffer, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ───────────── Helpers ─────────────

    internal static FileAttributes? GetAttributes(SafeFileHandle h)
        => GetFileInformationByHandle(h, out var info) ? (FileAttributes)info.dwFileAttributes : null;

    /// <summary>The latest of creation, last-write and change time (FILETIME, UTC).</summary>
    private static long Newest(SafeFileHandle h, BY_HANDLE_FILE_INFORMATION info)
    {
        var newest = Math.Max(((long)info.ftCreationHigh << 32) | info.ftCreationLow, ((long)info.ftLastWriteHigh << 32) | info.ftLastWriteLow);
        // The change time (metadata, e.g. a file moved in) is only in FILE_BASIC_INFO.
        if (GetFileInformationByHandleEx(h, FileBasicInfo, out var basic,(uint)Marshal.SizeOf<FILE_BASIC_INFO>()))
            newest = Math.Max(newest, Math.Max(basic.ChangeTime, Math.Max(basic.CreationTime, basic.LastWriteTime)));
        return newest;
    }

    /// <summary>
    /// True when an open handle is the file or folder at <paramref name="expectedPath"/>, not one
    /// reached through a junction or symbolic link somewhere along the path.
    /// </summary>
    internal static bool IsAtPath(SafeFileHandle h, string expectedPath)
    {
        var actual = FinalPath(h);
        var expected = ResolveDrive(LongPath(Path.GetFullPath(expectedPath).TrimEnd('\\')));
        return actual is not null && string.Equals(actual.TrimEnd('\\'), expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Number of names (hard links) of an open file; 0 if unknown.</summary>
    internal static uint LinkCount(SafeFileHandle h) => GetFileInformationByHandle(h, out var info) ? info.nNumberOfLinks : 0;

    private static string? FinalPath(SafeFileHandle h)
    {
        var buffer = new char[32768];
        var len = GetFinalPathNameByHandleW(h, buffer, (uint)buffer.Length, 0);
        if (len == 0 || len >= buffer.Length) return null;
        var path = new string(buffer, 0, (int)len);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    /// <summary>
    /// Replaces the drive root with what it really points to (subst'd and mapped drives resolve to
    /// another volume or a UNC path), so only links *after* the root are treated as redirection.
    /// </summary>
    private static string ResolveDrive(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return path;
        using var h = CreateFileW(root, FILE_READ_ATTRIBUTES | SYNCHRONIZE, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid || FinalPath(h) is not { } real) return path;
        return Path.Combine(real.TrimEnd('\\') + "\\", path[root.Length..]);
    }

    /// <summary>Expands 8.3 short names (Path.GetTempPath can return C:\Users\ADMINI~1\...).</summary>
    private static string LongPath(string path)
    {
        var buffer = new char[32768];
        var len = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        return len > 0 && len < buffer.Length ? new string(buffer, 0, (int)len) : path;
    }

    // ───────────── Native ─────────────

    private const uint DELETE = 0x00010000, SYNCHRONIZE = 0x00100000;
    private const uint FILE_LIST_DIRECTORY = 0x0001, FILE_READ_ATTRIBUTES = 0x0080, FILE_WRITE_ATTRIBUTES = 0x0100;
    private const uint FILE_SHARE_ALL = 0x7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint OBJ_CASE_INSENSITIVE = 0x40;
    private const uint FILE_OPEN = 1;
    private const uint FILE_DIRECTORY_FILE = 0x1, FILE_NON_DIRECTORY_FILE = 0x40, FILE_SYNCHRONOUS_IO_NONALERT = 0x20, FILE_OPEN_REPARSE_POINT = 0x00200000;
    private const int FileBasicInfo = 0, FileDispositionInfo = 4, FileFullDirectoryInfo = 14, FileFullDirectoryRestartInfo = 15, FileDispositionInfoEx = 21, FileRenameInfo = 3;
    private const uint FILE_DISPOSITION_FLAG_DELETE = 0x1, FILE_DISPOSITION_FLAG_POSIX_SEMANTICS = 0x2, FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBJECT_ATTRIBUTES
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_STATUS_BLOCK
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_RENAME_INFO
    {
        public uint Flags; // ReplaceIfExists
        public IntPtr RootDirectory;
        public uint FileNameLength; // followed by the name (UTF-16)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_DISPOSITION_INFO_EX
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_BASIC_INFO
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; // 0 = unchanged
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        // FILETIMEs are two DWORDs (4-byte aligned), not 8-byte longs.
        public uint dwFileAttributes;
        public uint ftCreationLow, ftCreationHigh, ftLastAccessLow, ftLastAccessHigh, ftLastWriteLow, ftLastWriteHigh;
        public uint dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref OBJECT_ATTRIBUTES attributes, out IO_STATUS_BLOCK status,
        IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, IntPtr buffer, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FILE_BASIC_INFO info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref FILE_DISPOSITION_INFO info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref FILE_DISPOSITION_INFO_EX info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, ref FILE_BASIC_INFO info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle h, [Out] char[] buffer, uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, [Out] char[] buffer, uint size);
}
