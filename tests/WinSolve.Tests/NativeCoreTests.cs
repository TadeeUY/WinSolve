using System.Text;
using WinSolve.Core;
using WinSolve.Services;
using Xunit;

namespace WinSolve.Tests;

/// <summary>
/// The binary formats the Rust core returns (see native/winsolve-native/src/format.rs). The same
/// layout is pinned on the Rust side, so a change on either side fails a test.
/// </summary>
public class NativeCoreTests
{
    private static void Text(BinaryWriter w, string s)
    {
        w.Write((uint)s.Length);
        foreach (var c in s) w.Write((ushort)c);
    }

    private static void Node(BinaryWriter w, uint parent, byte flags, ulong size, ulong files, string name)
    {
        w.Write(parent);
        w.Write(flags);
        w.Write(size);
        w.Write(files);
        Text(w, name);
    }

    private static byte[] SampleScan()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
        w.Write(0x314E5357u);   // "WSN1"
        w.Write(4u);            // nodes
        w.Write(2UL);           // inaccessible folders
        w.Write(2u);            // extensions
        Node(w, uint.MaxValue, 0, 30, 3, "");
        Node(w, 0, 0, 20, 2, "dir");
        Node(w, 1, 1, 20, 1, "a.iso");
        Node(w, 0, 3, 10, 1, "");   // a group of small files
        Text(w, ".iso"); w.Write(20UL); w.Write(1UL);
        Text(w, "(no extension)"); w.Write(10UL); w.Write(1UL);
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void The_scan_buffer_becomes_the_same_tree_the_managed_scanner_builds()
    {
        var root = new SpaceNode(@"C:\", null, isFile: false);
        NativeCore.ParseScan(SampleScan(), root, out var extensions, out var inaccessible);

        Assert.Equal(30L, root.Size);
        Assert.Equal(3L, root.FileCount);
        Assert.Equal(2L, inaccessible);
        Assert.Equal(2, root.Children.Count);

        var dir = root.Children[0];
        Assert.Equal("dir", dir.Name);
        Assert.False(dir.IsFile);
        Assert.Equal(20L, dir.Size);
        var iso = Assert.Single(dir.Children);
        Assert.Equal("a.iso", iso.Name);
        Assert.True(iso.IsFile);
        Assert.False(iso.IsGroup);
        Assert.Equal(@"C:\dir\a.iso", iso.FullPath);
        Assert.Equal(".iso", iso.Extension);

        var group = root.Children[1];
        Assert.True(group.IsFile);
        Assert.True(group.IsGroup);
        Assert.Equal("[1 small files]", group.Name);
        Assert.Equal(10L, group.Size);

        Assert.Equal(new[] { new ExtensionStat(".iso", 20, 1), new ExtensionStat("(no extension)", 10, 1) }, extensions.ToArray());
    }

    [Fact]
    public void A_damaged_scan_buffer_is_refused_instead_of_read_past_its_end()
    {
        var good = SampleScan();
        for (var cut = 0; cut < good.Length; cut += 7)
        {
            var truncated = good[..cut];
            Assert.ThrowsAny<InvalidDataException>(() =>
                NativeCore.ParseScan(truncated, new SpaceNode("C:\\", null, false), out _, out _));
        }
        var wrongMagic = (byte[])good.Clone();
        wrongMagic[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => NativeCore.ParseScan(wrongMagic, new SpaceNode("C:\\", null, false), out _, out _));
    }

    private static byte[] SampleDuplicates(params uint[] members)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true);
        w.Write(0x31445357u);   // "WSD1"
        w.Write(1u);
        w.Write(7UL);
        w.Write(Enumerable.Repeat((byte)0xAB, 32).ToArray());
        w.Write((uint)members.Length);
        foreach (var m in members) w.Write(m);
        w.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void The_duplicates_buffer_lists_the_identical_files_by_index()
    {
        var groups = NativeCore.ParseDuplicates(SampleDuplicates(0, 2), fileCount: 3);
        var g = Assert.Single(groups);
        Assert.Equal(7L, g.Size);
        Assert.Equal(string.Concat(Enumerable.Repeat("AB", 32)), g.Hash);
        Assert.Equal(new[] { 0, 2 }, g.Members);
    }

    [Fact]
    public void A_duplicates_buffer_that_points_outside_the_list_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => NativeCore.ParseDuplicates(SampleDuplicates(0, 2), fileCount: 2));
        Assert.Throws<InvalidDataException>(() => NativeCore.ParseDuplicates(SampleDuplicates(1), fileCount: 3)); // a set needs two files
        Assert.Throws<InvalidDataException>(() => NativeCore.ParseDuplicates(SampleDuplicates(0, 1)[..20], fileCount: 3));
    }
}
