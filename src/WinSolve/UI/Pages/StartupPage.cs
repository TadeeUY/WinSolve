using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class StartupPage : Page
{
    private readonly DataGridView _grid = new();
    private readonly Label _status = Theme.Label("", Theme.Small, Theme.Muted);
    private bool _filling;

    public override string Key => "startup";

    public StartupPage() : base("Startup apps",
        "Disable is instant and reversible (same as Task Manager). Remove deletes the startup entry for good; the program stays installed.")
    {
        AddRow(Theme.Row(
            Theme.Button("Remove selected", async (_, _) => await RemoveSelected(), primary: true, glyph: "\uE74D"),
            Theme.Button("Refresh", async (_, _) => await FillAsync()),
            Theme.Button("Open Startup folder", (_, _) => ProcessRunner.ShellOpen(Environment.GetFolderPath(Environment.SpecialFolder.Startup))),
            Theme.Button("Task Scheduler", (_, _) => ProcessRunner.ShellOpen("taskschd.msc"))));
        AddRow(_status);

        Theme.StyleGrid(_grid);
        _grid.MultiSelect = true;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "Enabled", Width = 84, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Name", ReadOnly = true, Width = 240, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "loc", HeaderText = "Location", ReadOnly = true, Width = 220, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "cmd", HeaderText = "Command", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns["loc"]!.DefaultCellStyle.ForeColor = Theme.Muted;
        _grid.Columns["cmd"]!.DefaultCellStyle.ForeColor = Theme.Muted;
        _grid.CellContentClick += (_, e) => { if (e.ColumnIndex == 0) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += async (_, e) =>
        {
            if (_filling || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            var row = _grid.Rows[e.RowIndex];
            var item = (StartupItem)row.Tag!;
            var enabled = row.Cells[0].Value is true;
            try
            {
                await StartupService.SetEnabledAsync(item, enabled);
                Logger.Write($"Startup: '{item.Name}' {(enabled ? "enabled" : "disabled")}");
            }
            catch (Exception ex)
            {
                Localization.Loc.Show(this, $"Could not change '{item.Name}': {ex.Message}", "WinSolve", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            UpdateStatus();
        };
        AddRow(_grid, fill: true);
    }

    public override async void OnShown() => await FillAsync();

    private async Task FillAsync()
    {
        _status.Text = "Loading...";
        var items = await StartupService.GetItemsAsync();
        _filling = true;
        _grid.Rows.Clear();
        foreach (var item in items)
        {
            var i = _grid.Rows.Add(item.Enabled, item.Name, item.Location, item.Command);
            _grid.Rows[i].Tag = item;
        }
        _filling = false;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var items = _grid.Rows.Cast<DataGridViewRow>().Select(r => (StartupItem)r.Tag!).ToList();
        var on = items.Count(i => i.Enabled);
        _status.Text = $"{on} of {items.Count} entries start with Windows." + (on > 8 ? " Disabling the ones you don't need speeds up sign-in." : "");
    }

    private async Task RemoveSelected()
    {
        var items = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => (StartupItem)r.Tag!).ToList();
        if (items.Count == 0)
        {
            Info("Select one or more entries first (click a row; Ctrl+click to select several).");
            return;
        }
        if (!ConfirmDanger($"Permanently remove {items.Count} startup entr{(items.Count == 1 ? "y" : "ies")}?\n\n- " +
                           string.Join("\n- ", items.Select(i => i.Name)) + "\n\nThe programs stay installed; they just won't start automatically."))
            return;

        var errors = new List<string>();
        foreach (var item in items)
        {
            try { await StartupService.RemoveAsync(item); }
            catch (Exception ex) { errors.Add($"{item.Name}: {ex.Message}"); }
        }
        await FillAsync();
        if (errors.Count > 0) Info("Some entries could not be removed:\n\n" + string.Join("\n", errors));
    }
}
