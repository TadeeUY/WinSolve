using System.Runtime.InteropServices;
using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

/// <summary>WizTree-style disk space analyzer: folder tree, treemap and file types.</summary>
public sealed class SpacePage : Page
{
    private readonly FlowLayoutPanel _driveRow = new() { AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 4) };
    private readonly DriveCard _folderCard;
    private readonly FlatBtn _scan, _cancel, _up, _dupes;
    private readonly Label _crumb = Theme.Label("", Theme.BodyBold, Theme.Text);
    private readonly Label _status = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly Label _hoverInfo = Theme.Label("Hover over the map to see what each block is. Double-click a folder to zoom in.", Theme.Small, Theme.Muted);
    private readonly SpaceTree _tree = new();
    private readonly TreemapView _map = new() { Dock = DockStyle.Fill };
    private readonly DataGridView _types = new();
    private readonly ContextMenuStrip _menu = Menus.Create();
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    private readonly ScanPlaceholder _placeholder;
    private readonly Control _resultView;
    private CancellationTokenSource? _cts;
    private SpaceScanResult? _result;
    private string? _selectedPath;

    public override string Key => "space";

    public SpacePage() : base("Disk space",
        "Find out what takes up space. Double-click the map to zoom into a folder; right-click to open or delete.")
    {
        _scan = Theme.Button("Scan", async (_, _) => await ScanAsync(), primary: true, glyph: "\uE721");
        _cancel = Theme.Button("Cancel", (_, _) => _cts?.Cancel(), glyph: "\uE711");
        _cancel.Visible = false;
        _up = Theme.Button("Up", (_, _) => ZoomUp(), glyph: "\uE74A");
        _up.Enabled = false;
        _dupes = Theme.Button("Find duplicates", (_, _) =>
        {
            if (_result is null) return;
            using var dlg = new DuplicatesDialog(_result.Root);
            dlg.ShowDialog(this);
        }, glyph: "\uE8C8");
        _dupes.Enabled = false;

        // ── Drives ──
        _folderCard = new DriveCard("", "Scan a folder", "Pick any folder", -1, "\uE8B7");
        _folderCard.Click += (_, _) => PickFolder();
        AddRow(_driveRow);

        // ── Toolbar: actions left, where you are in the middle, extras right ──
        var bar = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 2) };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var left = Theme.Row(_scan, _cancel, _up);
        left.WrapContents = false;
        var right = Theme.Row(_dupes, Theme.Button("Clean junk files", (_, _) => Main.Navigate("tools"), glyph: "\uE74D"));
        right.WrapContents = false;
        _crumb.AutoSize = false;
        _crumb.Dock = DockStyle.Fill;
        _crumb.TextAlign = ContentAlignment.MiddleLeft;
        _crumb.AutoEllipsis = true;
        _crumb.Margin = new Padding(8, 0, 8, 0);
        bar.Controls.Add(left, 0, 0);
        bar.Controls.Add(_crumb, 1, 0);
        bar.Controls.Add(right, 2, 0);
        AddRow(bar);
        AddRow(_status);

        // ── Folder tree ──
        _tree.Dock = DockStyle.Fill;
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
        _types.Columns.Add(new DataGridViewTextBoxColumn { Name = "color", HeaderText = "", Width = 22, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _types.Columns.Add("ext", "Type");
        _types.Columns.Add("size", "Size");
        _types.Columns.Add("count", "Files");
        _types.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _types.Columns["color"]!.AutoSizeMode = DataGridViewAutoSizeColumnMode.None;
        _types.Columns["color"]!.Width = 22;
        foreach (var col in new[] { "size", "count" })
            _types.Columns[col]!.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        // Set per cell: the grid's alternating-row style would otherwise override column alignment.
        _types.CellFormatting += (_, e) =>
        {
            if (e.CellStyle is null) return;
            if (e.ColumnIndex is 2 or 3) e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            if (e.ColumnIndex == 3) e.CellStyle.ForeColor = Theme.Muted;
        };
        _types.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0 || e.Graphics is null) return;
            e.PaintBackground(e.CellBounds, true);
            var ext = _types.Rows[e.RowIndex].Cells["ext"].Value as string ?? "";
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(FileColors.For(ext));
            using var dot = Theme.RoundedRect(new Rectangle(e.CellBounds.X + 6, e.CellBounds.Y + e.CellBounds.Height / 2 - 5, 10, 10), 3);
            e.Graphics.FillPath(b, dot);
            e.Handled = true;
        };

        var leftSplit = new SplitContainer { Orientation = Orientation.Horizontal, SplitterWidth = 10, BackColor = Theme.Background, Dock = DockStyle.Fill };
        leftSplit.Panel1.Controls.Add(Titled("Folders", _tree));
        leftSplit.Panel2.Controls.Add(Titled("File types", _types));
        var leftSized = false;
        leftSplit.SizeChanged += (_, _) =>
        {
            if (leftSized || leftSplit.Height < 200) return;
            leftSized = true;
            try { leftSplit.SplitterDistance = (int)(leftSplit.Height * 0.62); } catch { }
        };

        _hoverInfo.AutoSize = false;
        _hoverInfo.Dock = DockStyle.Fill;
        _hoverInfo.AutoEllipsis = true;
        _hoverInfo.TextAlign = ContentAlignment.MiddleLeft;
        _hoverInfo.Height = 26;
        var mapPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(0) };
        mapPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mapPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        mapPanel.Controls.Add(_map, 0, 0);
        mapPanel.Controls.Add(_hoverInfo, 0, 1);
        _map.Margin = new Padding(0);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 10, BackColor = Theme.Background, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(leftSplit);
        split.Panel2.Controls.Add(Titled("Map", mapPanel));
        var sized = false;
        split.SizeChanged += (_, _) =>
        {
            if (sized || split.Width < 600) return;
            sized = true;
            try { split.SplitterDistance = Math.Min(420, split.Width * 2 / 5); } catch { }
        };
        _resultView = split;

        // Before (and while) scanning: a friendly empty state instead of empty panels.
        _placeholder = new ScanPlaceholder(Theme.Button("Scan", async (_, _) => await ScanAsync(), primary: true, glyph: "\uE721")) { Dock = DockStyle.Fill };
        _content.Controls.Add(Theme.InCard(_placeholder, 8));
        AddRow(_content, fill: true);

        _map.HoverChanged += n => _hoverInfo.Text = n is null
            ? Localization.Loc.T("Hover over the map to see what each block is. Double-click a folder to zoom in.")
            : $"{n.FullPath}   ·   {Format.Bytes(n.Size)}" + (n.FileCount > 1 ? $"   ·   {n.FileCount:N0} files" : "") +
              (_result is null ? "" : $"   ·   {Percent(n):0.0}% of total");
        _map.NodeSelected += SelectInTree;
        _map.NodeActivated += ZoomTo;
        _map.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && _map.Selected is { } n) ShowMenu(n, _map, e.Location);
        };

        LoadDrives();
    }

    /// <summary>A card with a small title above its content.</summary>
    private static Control Titled(string title, Control content)
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent, Margin = new Padding(0) };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = Theme.Label(title, Theme.BodyBold, Theme.Text);
        label.Margin = new Padding(4, 2, 0, 6);
        panel.Controls.Add(label, 0, 0);
        content.Dock = DockStyle.Fill;
        content.Margin = new Padding(0);
        panel.Controls.Add(content, 0, 1);
        return Theme.InCard(panel, 10);
    }

    private void ShowView(bool results)
    {
        var view = results ? _resultView : _placeholder.Parent!;
        if (_content.Controls.Count == 1 && _content.Controls[0] == view) return;
        _content.SuspendLayout();
        _content.Controls.Clear();
        _content.Controls.Add(view);
        view.Dock = DockStyle.Fill;
        _content.ResumeLayout();
    }

    private void SelectDrive(DriveCard card)
    {
        foreach (var c in _driveRow.Controls.OfType<DriveCard>()) c.Selected = c == card;
        _selectedPath = card.Path;
    }

    private double Percent(SpaceNode n) => _result is { Root.Size: > 0 } r ? n.Size * 100.0 / r.Root.Size : 0;

    private void LoadDrives()
    {
        Theme.ClearAndDispose(_driveRow, _folderCard);
        DriveCard? first = null;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                var label = string.IsNullOrEmpty(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "USB drive" : "Local Disk") : d.VolumeLabel;
                var used = d.TotalSize - d.AvailableFreeSpace;
                var card = new DriveCard(d.RootDirectory.FullName, $"{d.Name.TrimEnd('\\')}  {label}",
                    $"{Format.Bytes(d.AvailableFreeSpace)} free of {Format.Bytes(d.TotalSize)}",
                    d.TotalSize > 0 ? (double)used / d.TotalSize : 0,
                    d.DriveType == DriveType.Removable ? "\uE88E" : "\uEDA2");
                card.Click += (_, _) => SelectDrive(card);
                card.DoubleClick += async (_, _) => { SelectDrive(card); await ScanAsync(); };
                _driveRow.Controls.Add(card);
                first ??= card;
            }
            catch { }
        }
        _driveRow.Controls.Add(_folderCard);
        // Keep the current choice after a refresh; otherwise pick the first drive.
        var current = _driveRow.Controls.OfType<DriveCard>().FirstOrDefault(c => c.Path.Length > 0 && c.Path == _selectedPath);
        if (current is not null) SelectDrive(current);
        else if (first is not null) SelectDrive(first);
    }

    private void PickFolder()
    {
        using var dlg = new FolderBrowserDialog { Description = Localization.Loc.T("Folder to scan"), UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _folderCard.Path = dlg.SelectedPath;
        _folderCard.Text = Path.GetFileName(dlg.SelectedPath.TrimEnd('\\')) is { Length: > 0 } name ? name : dlg.SelectedPath;
        _folderCard.Detail = dlg.SelectedPath;
        SelectDrive(_folderCard);
    }

    private async Task ScanAsync()
    {
        if (_cts is not null || string.IsNullOrEmpty(_selectedPath)) return;
        var path = _selectedPath;
        _cts = new CancellationTokenSource();
        _scan.Enabled = false;
        _cancel.Visible = true;
        _up.Enabled = _dupes.Enabled = false;
        _tree.Nodes.Clear();
        _types.Rows.Clear();
        _map.Root = null;
        _result = null;
        _crumb.Text = "";
        _status.Text = "";
        GC.Collect(); // release the previous scan before starting another

        _placeholder.Title = $"Scanning {path}";
        _placeholder.Counters = "";
        _placeholder.Current = "";
        _placeholder.Scanning = true;
        ShowView(results: false);

        var progress = new DiskScanner.Progress();
        using var timer = new System.Windows.Forms.Timer { Interval = 150 };
        timer.Tick += (_, _) =>
        {
            _placeholder.Counters = $"{progress.Files:N0} files  ·  {Format.Bytes(progress.Bytes)}";
            _placeholder.Current = progress.Current ?? "";
            _placeholder.Invalidate();
        };
        timer.Start();

        try
        {
            _result = await DiskScanner.ScanAsync(path, progress, _cts.Token);
            _status.Text = $"{Format.Bytes(_result.Root.Size)}  ·  {_result.Root.FileCount:N0} files  ·  scanned in {_result.Elapsed.TotalSeconds:0.0} s" +
                           (_result.Inaccessible > 0 ? $"  ·  {_result.Inaccessible:N0} folders not accessible" : "");
            ShowView(results: true);
            ShowResult();
            _dupes.Enabled = true;
        }
        catch (OperationCanceledException)
        {
            _placeholder.Title = "Scan canceled";
            _placeholder.Subtitle = "Select Scan to start again.";
        }
        catch (Exception ex)
        {
            _placeholder.Title = "The scan didn't finish";
            _placeholder.Subtitle = ex.Message;
            Logger.Write($"Error scanning {path}: {ex}");
        }
        finally
        {
            timer.Stop();
            _cts.Dispose();
            _cts = null;
            _scan.Enabled = true;
            _cancel.Visible = false;
            _placeholder.Scanning = false;
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
        var node = new TreeNode(n.DisplayName) { Tag = n };
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
        _crumb.Text = $"{n.FullPath}   ({Format.Bytes(n.Size)})";
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
            ProcessRunner.ShellOpen("explorer.exe", n.IsFile && !n.IsGroup ? $"/select,\"{path}\"" : $"\"{path}\"");
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

        if (Localization.Loc.Show(this, $"Move to the Recycle Bin?\n\n{path}\n({Format.Bytes(n.Size)}){warning}", "Delete",
                MessageBoxButtons.YesNo, isProtected ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        if (RecycleBin.Send(path, Handle))
        {
            Logger.Write($"Moved to Recycle Bin: {path} ({Format.Bytes(n.Size)})");
            var zoomRoot = _map.Root;
            var parent = n.Parent;
            n.Detach();
            ShowResult();
            // If the zoomed folder was inside what was deleted, go to the deleted item's parent.
            if (zoomRoot is not null && IsAttached(zoomRoot)) ZoomTo(zoomRoot);
            else if (parent is not null && IsAttached(parent)) ZoomTo(parent);
        }
        else
        {
            Info("Could not delete it (it may be in use or access was denied).");
        }
    }

    private bool IsAttached(SpaceNode node)
    {
        for (var p = node; p is not null; p = p.Parent)
            if (p == _result?.Root) return true;
        return false;
    }

    public override void OnShown()
    {
        // Free space changes; refresh the cards unless a scan is running.
        if (_cts is null) LoadDrives();
    }
}
