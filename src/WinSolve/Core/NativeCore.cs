using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WinSolve.Services;

namespace WinSolve.Core;

/// <summary>A set of identical files found by the native core (indexes into the list that was sent).</summary>
internal sealed record NativeDuplicateGroup(string Hash, long Size, int[] Members);

/// <summary>
/// The native core (<c>native/winsolve-native</c>, written in Rust): the disk scan and the duplicate
/// hashing, the two jobs that are mostly raw file-system speed. Everything it does is read-only.
/// </summary>
/// <remarks>
/// It is optional: when it is missing, can't be verified or fails, the callers use the managed
/// code they always had, so the app behaves the same either way.
/// <para>
/// WinSolve runs elevated and only loads DLLs from System32, so the DLL is not loaded from the
/// app's folder. It is embedded in WinSolve.exe, written (once per version) to the Administrators-only
/// data folder, checked against the embedded copy's SHA-256 while a read-only handle blocks writers,
/// and loaded by its full path. The portable edition doesn't use it: it must leave nothing behind.
/// </para>
/// </remarks>
internal static unsafe class NativeCore
{
    private const uint ExpectedAbi = 1;
    private const uint ScanMagic = 0x314E5357;   // "WSN1"
    private const uint DupesMagic = 0x31445357;  // "WSD1"
    private const int Cancelled = 1;
    private const string ResourceName = "winsolve_native.dll";

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBuffer
    {
        public byte* Ptr;
        public ulong Len;
    }

    private sealed class Api
    {
        public delegate* unmanaged[Cdecl]<byte*, ulong, void> Free;
        public delegate* unmanaged[Cdecl]<char*, uint, char*, uint, char*, uint, char*, uint, uint, int*,
            delegate* unmanaged[Cdecl]<nint, ulong, ulong, char*, uint, void>, nint, NativeBuffer*, int> Scan;
        public delegate* unmanaged[Cdecl]<char*, ulong, ulong*, uint, uint, int*,
            delegate* unmanaged[Cdecl]<nint, uint, uint, uint, void>, nint, NativeBuffer*, int> FindDuplicates;
    }

    private static readonly Lazy<Api?> Library = new(Load);

    /// <summary>True when the native core is loaded (and will be used).</summary>
    internal static bool IsAvailable => Library.Value is not null;

    private static Api? Load()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;
            if (Environment.GetEnvironmentVariable("WINSOLVE_NO_NATIVE") == "1") return null;
            if (Edition.IsPortable || !Admin.IsElevated) return null;

            using var resource = typeof(NativeCore).Assembly.GetManifestResourceStream(ResourceName);
            if (resource is null) return null; // built without the native core

            var bytes = new byte[resource.Length];
            resource.ReadExactly(bytes);
            var hash = SHA256.HashData(bytes);
            var name = $"winsolve_native-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}.dll";

            var dir = Path.Combine(SafePath.DataFolder, "Native");
            SafePath.MakeTrusted(dir);
            var file = Path.Combine(dir, name);

            if (!File.Exists(file))
            {
                var temp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, file, overwrite: true);
            }

            // Nobody can write to the file while it is checked and loaded.
            using var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!SHA256.HashData(held).AsSpan().SequenceEqual(hash))
            {
                Logger.Write("Native core: the file on disk is not the one embedded in WinSolve; using the built-in code instead.");
                return null;
            }
            var handle = NativeLibrary.Load(file);
            var api = new Api
            {
                Free = (delegate* unmanaged[Cdecl]<byte*, ulong, void>)NativeLibrary.GetExport(handle, "ws_free"),
                Scan = (delegate* unmanaged[Cdecl]<char*, uint, char*, uint, char*, uint, char*, uint, uint, int*,
                    delegate* unmanaged[Cdecl]<nint, ulong, ulong, char*, uint, void>, nint, NativeBuffer*, int>)
                    NativeLibrary.GetExport(handle, "ws_scan"),
                FindDuplicates = (delegate* unmanaged[Cdecl]<char*, ulong, ulong*, uint, uint, int*,
                    delegate* unmanaged[Cdecl]<nint, uint, uint, uint, void>, nint, NativeBuffer*, int>)
                    NativeLibrary.GetExport(handle, "ws_find_duplicates"),
            };
            var abi = ((delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(handle, "ws_abi_version"))();
            if (abi != ExpectedAbi)
            {
                Logger.Write($"Native core: unexpected version {abi}; using the built-in code instead.");
                return null;
            }

            RemoveOldVersions(dir, name);
            Logger.Write("Native core loaded.");
            return api;
        }
        catch (Exception ex)
        {
            Logger.Write($"Native core not used: {ex.Message}");
            return null;
        }
    }

    private static void RemoveOldVersions(string dir, string keep)
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(dir, "winsolve_native-*"))
            {
                if (Path.GetFileName(old).StartsWith(keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { } // in use by another WinSolve
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ───────────── Disk scan ─────────────

    /// <summary>
    /// Scans <paramref name="path"/> into <paramref name="root"/>. Returns false (with <paramref name="root"/>
    /// and <paramref name="progress"/> left as they were) when the native core can't be used, so the
    /// caller scans with managed code. Throws <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    internal static bool TryScan(string path, SpaceNode root, DiskScanner.Progress progress, string? sharedLinksRoot,
        int workers, CancellationToken ct, out List<ExtensionStat> extensions, out long inaccessible)
    {
        extensions = [];
        inaccessible = 0;
        var api = Library.Value;
        if (api is null) return false;

        var excludes = string.Join('\n', InteractiveUser.OtherProfileFoldersForScan);
        var sid = InteractiveUser.Sid.Value;
        var shared = sharedLinksRoot ?? "";
        var cancel = (int*)NativeMemory.AllocZeroed(sizeof(int));
        var state = GCHandle.Alloc(progress);
        NativeBuffer buffer = default;
        try
        {
            int rc;
            using (ct.Register(static p => Interlocked.Exchange(ref *(int*)(nint)p!, 1), (nint)cancel))
            {
                fixed (char* pRoot = path)
                fixed (char* pExcludes = excludes)
                fixed (char* pSid = sid)
                fixed (char* pShared = shared)
                {
                    rc = api.Scan(pRoot, (uint)path.Length, pExcludes, (uint)excludes.Length, pSid, (uint)sid.Length,
                        pShared, (uint)shared.Length, (uint)workers, cancel, &OnScanProgress, GCHandle.ToIntPtr(state), &buffer);
                }
            }
            if (rc == Cancelled) throw new OperationCanceledException(ct);
            if (rc != 0 || buffer.Ptr is null)
            {
                Logger.Write($"Native disk scan failed (code {rc}); using the built-in scan.");
                Interlocked.Exchange(ref progress.Files, 0);
                Interlocked.Exchange(ref progress.Bytes, 0);
                return false;
            }
            try
            {
                ParseScan(new ReadOnlySpan<byte>(buffer.Ptr, checked((int)buffer.Len)), root, out extensions, out inaccessible);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
            {
                Logger.Write($"Native disk scan returned unreadable data ({ex.Message}); using the built-in scan.");
                root.Children.Clear();
                root.Size = 0;
                root.FileCount = 0;
                extensions = [];
                inaccessible = 0;
                Interlocked.Exchange(ref progress.Files, 0);
                Interlocked.Exchange(ref progress.Bytes, 0);
                return false;
            }
            // The totals the callbacks were heading for.
            Raise(ref progress.Files, root.FileCount);
            Raise(ref progress.Bytes, root.Size);
            return true;
        }
        finally
        {
            if (buffer.Ptr is not null) api.Free(buffer.Ptr, buffer.Len);
            state.Free();
            NativeMemory.Free(cancel);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnScanProgress(nint user, ulong files, ulong bytes, char* dir, uint dirLength)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not DiskScanner.Progress p) return;
            Raise(ref p.Files, (long)files);
            Raise(ref p.Bytes, (long)bytes);
            if (dir is not null && dirLength > 0) p.Current = new string(dir, 0, (int)dirLength);
        }
        catch
        {
            // Nothing may escape into native code.
        }
    }

    /// <summary>Progress arrives from several threads, possibly out of order: only ever move forward.</summary>
    private static void Raise(ref long target, long value)
    {
        long current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
    }

    /// <summary>
    /// Reads the scan buffer (see <c>format.rs</c>): fills <paramref name="root"/> and its descendants.
    /// Nodes come parents-first, children largest-first.
    /// </summary>
    internal static void ParseScan(ReadOnlySpan<byte> data, SpaceNode root, out List<ExtensionStat> extensions, out long inaccessible)
    {
        var r = new Reader(data);
        if (data.Length < 20 || r.U32() != ScanMagic) throw new InvalidDataException("bad header");
        var count = r.U32();
        inaccessible = (long)r.U64();
        var extCount = r.U32();
        if (count == 0 || count > data.Length) throw new InvalidDataException("bad node count");

        var nodes = new SpaceNode[count];
        for (var i = 0; i < count; i++)
        {
            var parent = r.U32();
            var flags = r.Byte();
            var size = (long)r.U64();
            var files = (long)r.U64();
            var name = r.Text();
            if (i == 0)
            {
                if (parent != uint.MaxValue) throw new InvalidDataException("the first node must be the root");
                root.Size = size;
                root.FileCount = files;
                nodes[0] = root;
                continue;
            }
            if (parent >= i) throw new InvalidDataException("a child came before its parent");
            var isGroup = (flags & 2) != 0;
            var node = new SpaceNode(isGroup ? $"[{files:N0} small files]" : name, nodes[parent], isFile: (flags & 1) != 0)
            {
                IsGroup = isGroup,
                Size = size,
                FileCount = files,
            };
            nodes[parent].Children.Add(node);
            nodes[i] = node;
        }

        extensions = new List<ExtensionStat>((int)Math.Min(extCount, 4096));
        for (var i = 0; i < extCount; i++)
        {
            var ext = r.Text();
            var size = (long)r.U64();
            var n = (long)r.U64();
            extensions.Add(new ExtensionStat(ext, size, n));
        }
    }

    /// <summary>Reads little-endian values and UTF-16 text from a native buffer, refusing to go past its end.</summary>
    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _d;
        private int _pos;

        public Reader(ReadOnlySpan<byte> data)
        {
            _d = data;
            _pos = 0;
        }

        private void Need(int n)
        {
            if (n < 0 || _d.Length - _pos < n) throw new InvalidDataException("truncated");
        }

        public uint U32()
        {
            Need(4);
            var v = BinaryPrimitives.ReadUInt32LittleEndian(_d[_pos..]);
            _pos += 4;
            return v;
        }

        public ulong U64()
        {
            Need(8);
            var v = BinaryPrimitives.ReadUInt64LittleEndian(_d[_pos..]);
            _pos += 8;
            return v;
        }

        public byte Byte()
        {
            Need(1);
            return _d[_pos++];
        }

        public string Text()
        {
            var length = U32();
            if (length > int.MaxValue / 2) throw new InvalidDataException("name too long");
            var bytes = (int)length * 2;
            Need(bytes);
            var text = new string(MemoryMarshal.Cast<byte, char>(_d.Slice(_pos, bytes)));
            _pos += bytes;
            return text;
        }
    }

    // ───────────── Duplicates ─────────────

    private sealed class DupState(Action<string> status)
    {
        public readonly Action<string> Status = status;
    }

    /// <summary>
    /// Compares the contents of same-size files: returns the sets of identical files, or null when
    /// the native core can't be used (the caller then hashes with managed code).
    /// Throws <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    internal static List<NativeDuplicateGroup>? TryFindDuplicates(IReadOnlyList<(string Path, long Size)> files, Action<string> status, CancellationToken ct)
    {
        var api = Library.Value;
        if (api is null) return null;
        if (files.Count == 0) return new List<NativeDuplicateGroup>();

        var total = 0L;
        foreach (var f in files) total += f.Path.Length + 1;
        if (total >= int.MaxValue) return null;
        var chars = new char[total];
        var sizes = new ulong[files.Count];
        var at = 0;
        for (var i = 0; i < files.Count; i++)
        {
            files[i].Path.CopyTo(0, chars, at, files[i].Path.Length);
            at += files[i].Path.Length;
            chars[at++] = '\0';
            sizes[i] = (ulong)Math.Max(0, files[i].Size);
        }

        var cancel = (int*)NativeMemory.AllocZeroed(sizeof(int));
        var state = GCHandle.Alloc(new DupState(status));
        NativeBuffer buffer = default;
        try
        {
            int rc;
            using (ct.Register(static p => Interlocked.Exchange(ref *(int*)(nint)p!, 1), (nint)cancel))
            {
                fixed (char* pPaths = chars)
                fixed (ulong* pSizes = sizes)
                {
                    rc = api.FindDuplicates(pPaths, (ulong)chars.Length, pSizes, (uint)files.Count,
                        (uint)Math.Clamp(Environment.ProcessorCount, 1, 4), cancel, &OnDuplicatesProgress, GCHandle.ToIntPtr(state), &buffer);
                }
            }
            if (rc == Cancelled) throw new OperationCanceledException(ct);
            if (rc != 0 || buffer.Ptr is null)
            {
                Logger.Write($"Native duplicate search failed (code {rc}); using the built-in search.");
                return null;
            }
            try
            {
                return ParseDuplicates(new ReadOnlySpan<byte>(buffer.Ptr, checked((int)buffer.Len)), files.Count);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
            {
                Logger.Write($"Native duplicate search returned unreadable data ({ex.Message}); using the built-in search.");
                return null;
            }
        }
        finally
        {
            if (buffer.Ptr is not null) api.Free(buffer.Ptr, buffer.Len);
            state.Free();
            NativeMemory.Free(cancel);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDuplicatesProgress(nint user, uint done, uint total, uint sets)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is DupState s)
                s.Status($"Comparing contents... {done:N0}/{total:N0} size groups, {sets:N0} duplicate sets so far");
        }
        catch
        {
            // The dialog may be closing; nothing may escape into native code.
        }
    }

    /// <summary>Reads the duplicates buffer (see <c>format.rs</c>); every member index must be below <paramref name="fileCount"/>.</summary>
    internal static List<NativeDuplicateGroup> ParseDuplicates(ReadOnlySpan<byte> d, int fileCount)
    {
        if (d.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(d) != DupesMagic) throw new InvalidDataException("bad header");
        var groups = BinaryPrimitives.ReadUInt32LittleEndian(d[4..]);
        var pos = 8;
        var result = new List<NativeDuplicateGroup>((int)Math.Min(groups, 4096));
        for (var g = 0; g < groups; g++)
        {
            if (d.Length - pos < 8 + 32 + 4) throw new InvalidDataException("truncated");
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(d[pos..]);
            var hash = Convert.ToHexString(d.Slice(pos + 8, 32));
            var n = BinaryPrimitives.ReadUInt32LittleEndian(d[(pos + 40)..]);
            pos += 44;
            if (n < 2 || (long)n * 4 > d.Length - pos) throw new InvalidDataException("bad member count");
            var members = new int[n];
            for (var i = 0; i < n; i++)
            {
                var index = BinaryPrimitives.ReadUInt32LittleEndian(d[pos..]);
                pos += 4;
                if (index >= fileCount) throw new InvalidDataException("member out of range");
                members[i] = (int)index;
            }
            result.Add(new NativeDuplicateGroup(hash, size, members));
        }
        return result;
    }
}
