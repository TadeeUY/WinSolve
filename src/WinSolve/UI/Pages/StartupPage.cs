using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class StartupPage : Page
{
    private readonly DataGridView _grid = new();
    private readonly Label _status = Theme.Label("", Theme.Small, Theme.Muted);
    private bool _filling;

    public override string Key => "startup";

    public StartupPage() : base("Startup apps",
        "Disable is instant and reversible (same as Task Manager). Remove deletes the startup entry (WinSolve keeps a backup you can restore); the program stays installed.")
    {
        AddRow(Theme.Row(
            Theme.Button("Remove selected", async (_, _) => await RemoveSelected(), primary: true, glyph: ""),
            Theme.Button("Restore removed...", async (_, _) => await RestoreRemoved(), glyph: ""),
            Theme.Button("Refresh", async (_, _) => await FillAsync()),
            Theme.Button("Open Startup folder", (_, _) => ProcessRunner.ShellOpen(InteractiveUser.Startup)),
            Theme.Button("Task Scheduler", (_, _) => ProcessRunner.ShellOpen("taskschd.msc"))));
        AddRow(_status);

        Theme.StyleGrid(_grid);
        _grid.MultiSelect = true;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "Enabled", Width = 84, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Name", ReadOnly = true, Width = 240, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "loc", HeaderText = "Location", ReadOnly = true, Width = 220, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "note", HeaderText = "Notes", ReadOnly = true, Width = 150, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "cmd", HeaderText = "Command", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns["loc"]!.DefaultCellStyle.ForeColor = Theme.Muted;
        _grid.Columns["note"]!.DefaultCellStyle.ForeColor = Theme.Muted;
        _grid.Columns["cmd"]!.DefaultCellStyle.ForeColor = Theme.Muted;
        _grid.CellContentClick += (_, e) =>
        {
            if (e.ColumnIndex != 0 || e.RowIndex < 0) return;
            // Windows Security can't be turned off; turning it back on is always allowed.
            if (_grid.Rows[e.RowIndex].Tag is StartupItem { BlocksDisable: true, Enabled: true } p)
            {
                Info($"WinSolve won't turn off or remove '{p.Name}'.\n\n{p.ProtectedReason}");
                return;
            }
            _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += async (_, e) =>
        {
            if (_filling || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            var row = _grid.Rows[e.RowIndex];
            var item = (StartupItem)row.Tag!;
            var enabled = row.Cells[0].Value is true;
            try
            {
                await StartupService.SetEnabledAsync(item, enabled);
                row.Cells[0].ReadOnly = item.BlocksDisable && item.Enabled;
                Logger.Write($"Startup: '{item.Name}' {(enabled ? "enabled" : "disabled")}");
            }
            catch (Exception ex)
            {
                Loc.Show(this, $"Could not change '{item.Name}': {Loc.T(ex.Message)}", "WinSolve", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                // Show the real state again.
                _filling = true;
                row.Cells[0].Value = item.Enabled;
                _filling = false;
            }
            UpdateStatus();
        };
        Theme.EmptyState(_grid, "No startup apps found.");
        AddRow(Theme.InCard(_grid), fill: true);
    }

    public override async void OnShown() => await FillAsync();

    private async Task FillAsync()
    {
        _status.Text = "Loading...";
        List<StartupItem> items;
        try { items = await StartupService.GetItemsAsync(); }
        catch (Exception ex)
        {
            Logger.Write($"Startup items: {ex}");
            if (!IsDisposed) _status.Text = ex.Message;
            return;
        }
        if (IsDisposed) return;
        _filling = true;
        _grid.Rows.Clear();
        foreach (var item in items)
        {
            var note = item.BlocksDisable ? "Windows Security"
                : item.IsProtected ? "Windows component"
                : item.RunsAsSystem ? "Runs as SYSTEM"
                : item.IsMachineWide ? "All users"
                : "";
            var i = _grid.Rows.Add(item.Enabled, item.Name, Loc.T(item.Location), Loc.T(note), item.Command);
            var row = _grid.Rows[i];
            row.Tag = item;
            if (item.IsProtected)
            {
                // Can't be removed (Windows Security can't be turned off either): say why on hover and on click.
                row.Cells[0].ReadOnly = item.BlocksDisable && item.Enabled;
                row.DefaultCellStyle.ForeColor = Theme.DisabledText;
                foreach (DataGridViewCell c in row.Cells) c.ToolTipText = Loc.T(item.ProtectedReason);
            }
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

        // Windows Security and Windows' own entries: never removed.
        var protectedItems = items.Where(i => i.IsProtected).ToList();
        if (protectedItems.Count > 0)
        {
            Info("These entries are protected and can't be removed:\n\n" +
                 string.Join("\n", protectedItems.Select(i => $"- {i.Name}\n  {i.ProtectedReason}")));
            items = items.Except(protectedItems).ToList();
            if (items.Count == 0) return;
        }

        // Entries for every user or run as SYSTEM: steer to Disable, which can be undone at once.
        var errors = new List<string>();
        var machineWide = items.Where(i => i.IsMachineWide).ToList();
        if (machineWide.Count > 0)
        {
            var answer = Loc.Show(this,
                "These entries start for every user of this PC or run as SYSTEM:\n\n- " + string.Join("\n- ", machineWide.Select(i => i.Name)) +
                "\n\nDisabling them is safer: it takes effect at once and you can turn them back on here. Removing deletes them from Windows (WinSolve keeps a backup you can restore).\n\n" +
                "Yes: disable them instead\nNo: remove them anyway\nCancel: do nothing",
                "WinSolve", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.Yes)
            {
                foreach (var item in machineWide)
                {
                    try { if (item.Enabled) await StartupService.SetEnabledAsync(item, false); }
                    catch (Exception ex) { errors.Add($"{item.Name}: {Loc.T(ex.Message)}"); }
                }
                items = items.Except(machineWide).ToList();
            }
        }

        if (items.Count > 0)
        {
            if (!ConfirmDanger($"Remove {items.Count} startup entries?\n\n- " + string.Join("\n- ", items.Select(i => i.Name)) +
                               "\n\nThe programs stay installed; they just won't start automatically. WinSolve saves a backup first, so you can bring them back with 'Restore removed...'."))
                items = [];
            foreach (var item in items)
            {
                try { await StartupService.RemoveAsync(item); }
                catch (Exception ex) { errors.Add($"{item.Name}: {Loc.T(ex.Message)}"); }
            }
        }
        await FillAsync();
        if (errors.Count > 0) Info("Some entries could not be changed:\n\n" + string.Join("\n", errors));
    }

    private async Task RestoreRemoved()
    {
        bool restored;
        using (var dlg = new StartupRestoreDialog())
        {
            dlg.ShowDialog(this);
            restored = dlg.Restored;
        }
        if (restored) await FillAsync();
    }
}
