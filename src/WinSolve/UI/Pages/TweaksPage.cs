using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class TweaksPage : Page
{
    private readonly DataGridView _grid = new();
    private readonly ComboBox _category;
    private readonly TextBox _search = Theme.TextBox("Search tweaks");
    private readonly Label _status = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly Dictionary<string, bool> _desired = [];
    private Dictionary<string, bool> _actual = [];

    public override string Key => "tweaks";

    public TweaksPage() : base("Tweaks",
        "Turn settings on or off, then select Apply. Every tweak can be undone by unchecking it.")
    {
        var categories = new[] { "All categories" }.Concat(TweakCatalog.All.Select(t => t.Category).Distinct()).ToArray();
        _category = Theme.Combo(categories);
        _category.SelectedIndexChanged += (_, _) => Fill();
        _search.TextChanged += (_, _) => Fill();

        AddRow(Theme.Row(
            _category, _search,
            Theme.Button("Apply changes", (_, _) => ApplyChanges(), primary: true),
            Theme.Button("Select recommended", (_, _) => { foreach (var t in TweakCatalog.All.Where(t => t.Recommended)) _desired[t.Id] = true; Fill(); }),
            Theme.Button("Discard changes", (_, _) => Reload()),
            Theme.Button("Restart Explorer", async (_, _) => await TaskCatalog.ExplorerRestart(new TaskContext(Logger.Write, default)))));
        AddRow(_status);

        Theme.StyleGrid(_grid);
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "On", Width = 50, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Tweak", ReadOnly = true, Width = 320, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "cat", HeaderText = "Category", ReadOnly = true, Width = 110, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "desc", HeaderText = "Description", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "rec", HeaderText = "Notes", ReadOnly = true, Width = 130, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns["cat"]!.DefaultCellStyle.ForeColor = Theme.Muted;
        _grid.Columns["desc"]!.DefaultCellStyle.ForeColor = Theme.Muted;
        _grid.Columns["rec"]!.DefaultCellStyle.ForeColor = Theme.Muted;
        _grid.CellContentClick += (_, e) => { if (e.ColumnIndex == 0) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellToolTipTextNeeded += (_, e) => { if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is Tweak t) e.ToolTipText = t.Description; };
        _grid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 0) return;
            var row = _grid.Rows[e.RowIndex];
            var t = (Tweak)row.Tag!;
            _desired[t.Id] = row.Cells[0].Value is true;
            MarkRow(row, t);
            UpdateStatus();
        };
        AddRow(_grid, fill: true);
    }

    public override void OnShown() => Reload();

    private void Reload()
    {
        _actual = TweakCatalog.All.ToDictionary(t => t.Id, t => t.SafeIsApplied());
        _desired.Clear();
        foreach (var kv in _actual) _desired[kv.Key] = kv.Value;
        Fill();
    }

    private void Fill()
    {
        var cat = _category.SelectedIndex <= 0 ? null : _category.SelectedItem as string;
        var q = _search.Text.Trim();
        _grid.Rows.Clear();
        foreach (var t in TweakCatalog.All)
        {
            if (cat is not null && t.Category != cat) continue;
            if (q.Length > 0 && !t.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                             && !t.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase)) continue;

            var note = string.Join(", ", new[] { t.Recommended ? "Recommended" : null, t.NeedsReboot ? "Restart" : null }.OfType<string>());
            var i = _grid.Rows.Add(_desired.GetValueOrDefault(t.Id), t.Title, t.Category, t.Description, note);
            _grid.Rows[i].Tag = t;
            MarkRow(_grid.Rows[i], t);
        }
        UpdateStatus();
    }

    private void MarkRow(DataGridViewRow row, Tweak t)
    {
        var changed = _desired.GetValueOrDefault(t.Id) != _actual.GetValueOrDefault(t.Id);
        row.Cells["title"].Style.ForeColor = changed ? Theme.Info : Theme.Text;
    }

    private List<Tweak> Pending() => TweakCatalog.All
        .Where(t => _desired.GetValueOrDefault(t.Id) != _actual.GetValueOrDefault(t.Id))
        .ToList();

    private void UpdateStatus()
    {
        var pending = Pending().Count;
        _status.Text = $"{_actual.Count(kv => kv.Value)} of {_actual.Count} tweaks on" + (pending > 0 ? $"  ·  {pending} unapplied change(s), highlighted in blue" : "");
    }

    private void ApplyChanges()
    {
        _grid.EndEdit();
        var pending = Pending();
        if (pending.Count == 0)
        {
            Info("There are no changes to apply.");
            return;
        }
        if (!Confirm($"Apply {pending.Count} change(s)?\n\n" +
                     string.Join("\n", pending.Select(t => (_desired[t.Id] ? "Turn on:  " : "Turn off:  ") + t.Title))))
            return;

        var errors = new List<string>();
        bool explorer = false, reboot = false;
        foreach (var t in pending)
        {
            try
            {
                if (_desired[t.Id]) t.Apply(); else t.Revert();
                Logger.Write($"Tweak '{t.Title}' -> {(_desired[t.Id] ? "on" : "off")}");
                explorer |= t.NeedsExplorerRestart;
                reboot |= t.NeedsReboot;
            }
            catch (Exception ex)
            {
                errors.Add($"{t.Title}: {ex.Message}");
            }
        }

        Reload();

        var msg = errors.Count == 0 ? "Changes applied." : "Some tweaks failed:\n\n" + string.Join("\n", errors);
        if (reboot) msg += "\n\nSome changes take effect after a restart.";
        if (explorer)
        {
            if (MessageBox.Show(this, msg + "\n\nRestart Explorer now to see the changes?", "WinSolve",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                _ = TaskCatalog.ExplorerRestart(new TaskContext(Logger.Write, default));
        }
        else
        {
            Info(msg);
        }
    }
}
