using System.Runtime.InteropServices;
using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

/// <summary>WizTree-style disk space analyzer: folder tree, treemap and file types.</summary>
public sealed class SpacePage : Page
{
    private readonly ComboBox _drives;
    private readonly FlatBtn _scan, _cancel, _up;
    private readonly Label _status = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly Label _hoverInfo = Theme.Label("", Theme.Small, Theme.Text);
    private readonly TreeView _tree = new();
    private readonly TreemapView _map = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _types = new();
    private readonly ContextMenuStrip _menu = Menus.Create();
    private CancellationTokenSource? _cts;
    private SpaceScanResult? _result;

    public override string Key => "space";

    public SpacePage() : base("Disk space",
        "Find out what takes up space. Double-click the map to zoom into a folder; right-click to open or delete.")
    {
        _drives = Theme.Combo();
        _drives.Width = 240;
        _scan = Theme.Button("Scan", async (_, _) => await ScanAsync(), primary: true);
        _cancel = Theme.Button("Cancel", (_, _) => _cts?.Cancel());
        _cancel.Enabled = false;
        _up = Theme.Button("Up one level", (_, _) => ZoomUp());
        _up.Enabled = false;

        AddRow(Theme.Row(_drives,
            Theme.Button("Choose folder", (_, _) => PickFolder()),
            _scan, _cancel, _up,
            Theme.Button("Clean junk files", (_, _) => Main.Navigate("tools"))));
        AddRow(_status);

        // ── Folder tree ──
        _tree.BackColor = Theme.Card;
        _tree.LineColor = Theme.Border;
        _tree.ForeColor = Theme.Text;
        _tree.BorderStyle = BorderStyle.None;
        _tree.Font = Theme.Body;
        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;
        _tree.FullRowSelect = true;
        _tree.ShowLines = false;
        _tree.ItemHeight = 24;
        _tree.BeforeExpand += (_, e) => Expand(e.Node!);
        _tree.AfterSelect += (_, e) => { if (e.Node?.Tag is SpaceNode n) _map.Selected = n; };
        _tree.NodeMouseDoubleClick += (_, e) => { if (e.Node.Tag is SpaceNode { IsFile: false } n) ZoomTo(n); };
        _tree.NodeMouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.Node.Tag is not SpaceNode n) return;
            _tree.SelectedNode = e.Node;
            ShowMenu(n, _tree, e.Location);
        };

        // ── File types ──
        Theme.StyleGrid(_types);
        _types.Dock = DockStyle.Fill;
        _types.ReadOnly = true;
        _types.Columns.Add(new DataGridViewTextBoxColumn { Name = "color", HeaderText = "", Width = 18, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _types.Columns.Add("ext", "Type");
        _types.Columns.Add("size", "Size");
        _types.Columns.Add("count", "Files");
        _types.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _types.Columns["color"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        _types.Columns["color"]!.Width = 18;
        _types.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0 || e.Graphics is null) return;
            e.PaintBackground(e.CellBounds, true);
            var ext = _types.Rows[e.RowIndex].Cells["ext"].Value as string ?? "";
            using var b = new SolidBrush(FileColors.For(ext));
            var r = e.CellBounds;
            e.Graphics.FillRectangle(b, r.X + 4, r.Y + r.Height / 2 - 5, 10, 10);
            e.Handled = true;
        };

        var leftSplit = new SplitContainer { Orientation = Orientation.Horizontal, SplitterWidth = 6, BackColor = Theme.Background, Dock = DockStyle.Fill };
        leftSplit.Panel1.Controls.Add(_tree);
        leftSplit.Panel2.Controls.Add(_types);
        leftSplit.HandleCreated += (_, _) => { try { leftSplit.SplitterDistance = (int)(leftSplit.Height * 0.6); } catch { } };

        var mapPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
        mapPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mapPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mapPanel.Controls.Add(_map, 0, 0);
        mapPanel.Controls.Add(_hoverInfo, 0, 1);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 8, BackColor = Theme.Background, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(leftSplit);
        split.Panel2.Controls.Add(mapPanel);
        split.HandleCreated += (_, _) => { try { split.SplitterDistance = Math.Min(360, split.Width / 3); } catch { } };
        AddRow(split, fill: true);

        _map.HoverChanged += n => _hoverInfo.Text = n is null ? "" : $"{n.FullPath}   ·   {Format.Bytes(n.Size)}" +
            (n.FileCount > 1 ? $"   ·   {n.FileCount:N0} files" : "") + (_result is null ? "" : $"   ·   {Percent(n):0.0}% of total");
        _map.NodeSelected += SelectInTree;
        _map.NodeActivated += ZoomTo;
        _map.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && _map.Selected is { } n) ShowMenu(n, _map, e.Location);
        };

        LoadDrives();
    }

    private double Percent(SpaceNode n) => _result is { Root.Size: > 0 } r ? n.Size * 100.0 / r.Root.Size : 0;

    private void LoadDrives()
    {
        _drives.Items.Clear();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var label = string.IsNullOrEmpty(d.VolumeLabel) ? "Local Disk" : d.VolumeLabel;
                _drives.Items.Add(new DriveChoice(d.RootDirectory.FullName,
                    $"{d.Name.TrimEnd('\\')} {label} ({Format.Bytes(d.TotalSize - d.AvailableFreeSpace)} used of {Format.Bytes(d.TotalSize)})"));
            }
            catch { }
        }
        if (_drives.Items.Count > 0) _drives.SelectedIndex = 0;
    }

    private sealed record DriveChoice(string Path, string Text)
    {
        public override string ToString() => Text;
    }

    private void PickFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = "Folder to scan", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var choice = new DriveChoice(dlg.SelectedPath, dlg.SelectedPath);
        _drives.Items.Add(choice);
        _drives.SelectedItem = choice;
    }

    private async Task ScanAsync()
    {
        if (_cts is not null || _drives.SelectedItem is not DriveChoice choice) return;
        _cts = new CancellationTokenSource();
        _scan.Enabled = false;
        _cancel.Enabled = true;
        _tree.Nodes.Clear();
        _types.Rows.Clear();
        _map.Root = null;
        _result = null;
        GC.Collect(); // release the previous scan before starting another

        var progress = new DiskScanner.Progress();
        using var timer = new System.Windows.Forms.Timer { Interval = 200 };
        timer.Tick += (_, _) => _status.Text =
            $"Scanning... {progress.Files:N0} files  ·  {Format.Bytes(progress.Bytes)}  ·  {progress.Current}";
        timer.Start();

        try
        {
            _result = await DiskScanner.ScanAsync(choice.Path, progress, _cts.Token);
            _status.Text = $"{Format.Bytes(_result.Root.Size)} in {_result.Root.FileCount:N0} files  ·  scanned in {_result.Elapsed.TotalSeconds:0.0} s" +
                           (_result.Inaccessible > 0 ? $"  ·  {_result.Inaccessible:N0} folders not accessible" : "");
            ShowResult();
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Scan canceled.";
        }
        catch (Exception ex)
        {
            _status.Text = "Error: " + ex.Message;
            Logger.Write($"Error scanning {choice.Path}: {ex}");
        }
        finally
        {
            timer.Stop();
            _cts.Dispose();
            _cts = null;
            _scan.Enabled = true;
            _cancel.Enabled = false;
        }
    }

    private void ShowResult()
    {
        if (_result is null) return;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var rootNode = MakeTreeNode(_result.Root);
        _tree.Nodes.Add(rootNode);
        rootNode.Expand();
        _tree.EndUpdate();

        _types.Rows.Clear();
        foreach (var t in _result.Extensions.Take(40))
            _types.Rows.Add("", t.Extension, Format.Bytes(t.Size), t.Count.ToString("N0"));

        ZoomTo(_result.Root);
    }

    private TreeNode MakeTreeNode(SpaceNode n)
    {
        var pct = Percent(n);
        var node = new TreeNode($"{n.DisplayName}   {Format.Bytes(n.Size)}   ({pct:0.0}%)") { Tag = n };
        node.ForeColor = n.IsGroup ? Theme.Muted : pct >= 10 ? Theme.Warn : Theme.Text;
        if (!n.IsFile && n.Children.Count > 0) node.Nodes.Add(new TreeNode("…"));
        return node;
    }

    private void Expand(TreeNode node)
    {
        if (node.Tag is not SpaceNode n || node.Nodes.Count != 1 || node.Nodes[0].Tag is not null) return;
        _tree.BeginUpdate();
        node.Nodes.Clear();
        foreach (var c in n.Children.Take(300)) node.Nodes.Add(MakeTreeNode(c));
        _tree.EndUpdate();
    }

    private void ZoomTo(SpaceNode n)
    {
        _map.Root = n;
        _up.Enabled = n.Parent is not null;
        SelectInTree(n);
    }

    private void ZoomUp()
    {
        if (_map.Root?.Parent is { } p) ZoomTo(p);
    }

    /// <summary>Expands the tree down to the node and selects it.</summary>
    private void SelectInTree(SpaceNode target)
    {
        if (_tree.Nodes.Count == 0) return;
        var chain = new Stack<SpaceNode>();
        for (var x = target; x is not null; x = x.Parent) chain.Push(x);

        TreeNodeCollection level = _tree.Nodes;
        TreeNode? current = null;
        _tree.BeginUpdate();
        while (chain.Count > 0)
        {
            var wanted = chain.Pop();
            current = level.Cast<TreeNode>().FirstOrDefault(t => t.Tag == wanted);
            if (current is null) break;
            if (chain.Count > 0)
            {
                Expand(current);
                current.Expand();
                level = current.Nodes;
            }
        }
        _tree.EndUpdate();
        if (current is not null)
        {
            _tree.SelectedNode = current;
            current.EnsureVisible();
        }
    }

    // ───────────── Context menu ─────────────

    private void ShowMenu(SpaceNode n, Control owner, Point location)
    {
        _menu.Items.Clear();

        var path = n.FullPath;
        _menu.Items.Add("Show in File Explorer", null, (_, _) =>
        {
            System.Diagnostics.Process.Start("explorer.exe", n.IsFile && !n.IsGroup ? $"/select,\"{path}\"" : $"\"{path}\"");
        });
        if (!n.IsFile) _menu.Items.Add("Zoom into this folder", null, (_, _) => ZoomTo(n));
        _menu.Items.Add("Copy path", null, (_, _) => Clipboard.SetText(path));
        if (!n.IsGroup && n.Parent is not null)
        {
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Delete (move to Recycle Bin)", null, (_, _) => Delete(n));
        }
        _menu.Show(owner, location);
    }

    private static readonly string[] ProtectedRoots =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    ];

    private void Delete(SpaceNode n)
    {
        var path = n.FullPath;
        var name = n.Name.ToLowerInvariant();

        if (name is "pagefile.sys" or "swapfile.sys")
        {
            Info("This is the Windows page file (virtual memory). It cannot be deleted; its size is set under System > Advanced system settings > Performance.");
            return;
        }
        if (name == "hiberfil.sys")
        {
            Info("This is the hibernation file. To free it, turn on the 'Disable hibernation' tweak.");
            Main.Navigate("tweaks");
            return;
        }
        var isProtected = ProtectedRoots.Any(r => r.Length > 0 &&
            (path.Equals(r, StringComparison.OrdinalIgnoreCase) || path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)));
        var warning = isProtected
            ? "\n\nWarning: this is inside a system or program folder. Deleting it can break Windows or applications. To remove programs use the Apps page instead."
            : "";

        if (MessageBox.Show(this, $"Move to the Recycle Bin?\n\n{path}\n({Format.Bytes(n.Size)}){warning}", "Delete",
                MessageBoxButtons.YesNo, isProtected ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (RecycleBin.Send(path, Handle))
        {
            Logger.Write($"Moved to Recycle Bin: {path} ({Format.Bytes(n.Size)})");
            var zoomRoot = _map.Root;
            n.Detach();
            ShowResult();
            if (zoomRoot is not null && zoomRoot != n) ZoomTo(zoomRoot);
        }
        else
        {
            Info("Could not delete it (it may be in use or access was denied).");
        }
    }

    public override void OnShown()
    {
        if (_drives.Items.Count == 0) LoadDrives();
    }

    private static class RecycleBin
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string? pTo;
            public ushort fFlags;
            public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string? lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        private const uint FO_DELETE = 3;
        private const ushort FOF_ALLOWUNDO = 0x40, FOF_NOCONFIRMATION = 0x10, FOF_WANTNUKEWARNING = 0x4000;

        public static bool Send(string path, IntPtr owner)
        {
            var op = new SHFILEOPSTRUCT
            {
                hwnd = owner,
                wFunc = FO_DELETE,
                pFrom = path + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING,
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }
    }
}
