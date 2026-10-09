using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>Shows duplicate file sets and moves the selected copies to the Recycle Bin.</summary>
public sealed class DuplicatesDialog : Form
{
    private readonly DataGridView _grid = new();
    private readonly Label _status = Theme.Label("", Theme.Body, Theme.Muted);
    private readonly FlatBtn _delete;
    private readonly SpaceNode _root;
    private CancellationTokenSource? _cts;
    private List<DuplicateGroup> _groups = [];

    public DuplicatesDialog(SpaceNode root)
    {
        _root = root;
        Text = Localization.Loc.T("Duplicate files");
        HandleCreated += (_, _) => Localization.Loc.Apply(this);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1000, 640);
        MinimizeBox = false;
        ShowInTaskbar = false;
        Padding = new Padding(18);

        Theme.StyleGrid(_grid);
        _grid.Dock = DockStyle.Fill;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "Delete", Width = 60, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "set", HeaderText = "Set", ReadOnly = true, Width = 50, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "path", HeaderText = "File", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "size", HeaderText = "Size", ReadOnly = true, Width = 90, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "date", HeaderText = "Modified", ReadOnly = true, Width = 140, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.CellContentClick += (_, e) => { if (e.ColumnIndex == 0) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += (_, _) => UpdateDeleteButton();
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is DuplicateFile f)
                ProcessRunner.ShellOpen("explorer.exe", $"/select,\"{f.Path}\"");
        };

        _delete = Theme.Button("Move selected to Recycle Bin", (_, _) => DeleteSelected(), primary: true);
        _delete.Enabled = false;
        var top = Theme.Row(
            Theme.Button("Keep newest", (_, _) => AutoSelect(keepNewest: true)),
            Theme.Button("Keep oldest", (_, _) => AutoSelect(keepNewest: false)),
            Theme.Button("Clear selection", (_, _) => { foreach (DataGridViewRow r in _grid.Rows) r.Cells[0].Value = false; }),
            _delete);
        top.Dock = DockStyle.Top;
        _status.Dock = DockStyle.Bottom;
        _status.Padding = new Padding(0, 8, 0, 0);

        Controls.Add(_grid);
        Controls.Add(top);
        Controls.Add(_status);

        HandleCreated += (_, _) => Theme.StyleWindow(this);
        Shown += async (_, _) => await SearchAsync();
        FormClosing += (_, _) => _cts?.Cancel();
    }

    private async Task SearchAsync()
    {
        _cts = new CancellationTokenSource();
        _status.Text = "Looking for duplicates (files of 1 MB or more)...";
        try
        {
            _groups = await DuplicateFinder.FindAsync(_root, 1L << 20, s => BeginInvoke(() => _status.Text = s), _cts.Token);
            Fill();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "Error: " + ex.Message; }
    }

    private void Fill()
    {
        _grid.Rows.Clear();
        var set = 0;
        foreach (var g in _groups)
        {
            set++;
            foreach (var f in g.Files)
            {
                var i = _grid.Rows.Add(false, set, f.Path, Format.Bytes(f.Size), f.Modified == DateTime.MinValue ? "" : f.Modified.ToString("g"));
                _grid.Rows[i].Tag = f;
                if (set % 2 == 0) _grid.Rows[i].DefaultCellStyle.BackColor = Color.FromArgb(48, 48, 48);
            }
        }
        _status.Text = _groups.Count == 0
            ? "No duplicates found (Windows and program folders are not checked)."
            : $"{_groups.Count:N0} sets of duplicates  ·  {Format.Bytes(_groups.Sum(g => g.Wasted))} can be recovered by keeping one copy of each.  Double-click a file to show it.";
    }

    private void AutoSelect(bool keepNewest)
    {
        var keep = _groups.Select(g => keepNewest ? g.Files[^1] : g.Files[0]).ToHashSet();
        foreach (DataGridViewRow r in _grid.Rows)
            r.Cells[0].Value = r.Tag is DuplicateFile f && !keep.Contains(f);
        UpdateDeleteButton();
    }

    private List<DuplicateFile> Selected()
        => _grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells[0].Value is true).Select(r => (DuplicateFile)r.Tag!).ToList();

    private void UpdateDeleteButton()
    {
        var sel = Selected();
        _delete.Enabled = sel.Count > 0;
        _delete.Text = sel.Count > 0 ? $"Move {sel.Count} to Recycle Bin ({Format.Bytes(sel.Sum(f => f.Size))})" : "Move selected to Recycle Bin";
    }

    private void DeleteSelected()
    {
        var sel = Selected();
        // Never delete every copy of a set.
        foreach (var g in _groups)
        {
            if (g.Files.All(sel.Contains))
            {
                Localization.Loc.Show(this, $"Every copy of '{Path.GetFileName(g.Files[0].Path)}' is selected. Leave at least one copy unchecked.",
                    "Duplicate files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        if (Localization.Loc.Show(this, $"Move {sel.Count} file(s) ({Format.Bytes(sel.Sum(f => f.Size))}) to the Recycle Bin?",
                "Duplicate files", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        var failed = 0;
        var moved = new HashSet<DuplicateFile>();
        foreach (var f in sel)
        {
            if (RecycleBin.Send(f.Path, Handle))
            {
                moved.Add(f);
                Logger.Write($"Duplicate moved to Recycle Bin: {f.Path}");
            }
            else failed++;
        }
        // Only drop the files that were really moved; the others are still on disk.
        foreach (var g in _groups) g.Files.RemoveAll(moved.Contains);
        _groups.RemoveAll(g => g.Files.Count < 2);
        Fill();
        if (failed > 0) Localization.Loc.Show(this, $"{failed} file(s) could not be moved (in use or access denied).", "Duplicate files");
    }
}
