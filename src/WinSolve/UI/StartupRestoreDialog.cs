using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>Lists startup entries removed by WinSolve (backed up first) and puts them back.</summary>
internal sealed class StartupRestoreDialog : ToolDialog
{
    private readonly DataGridView _grid = Grid(("name", "Name", 160), ("loc", "Location", 140), ("cmd", "Command", 240), ("when", "Removed on", 90));

    /// <summary>True when at least one entry was restored (the caller refreshes its list).</summary>
    public bool Restored { get; private set; }

    public StartupRestoreDialog() : base("", "Restore removed startup entries",
        "WinSolve saves every startup entry before removing it. Choose one to put it back.", new Size(900, 520))
    {
        _grid.MultiSelect = true;
        Theme.EmptyState(_grid, "No removed entries to restore.");
        AddRow(Theme.InCard(_grid), fill: true);
        Buttons.Controls.Add(Theme.Button("Restore", async (_, _) => await RestoreSelected(), primary: true));
        Shown += (_, _) => Fill();
    }

    private void Fill()
    {
        _grid.Rows.Clear();
        foreach (var b in StartupService.ListBackups())
        {
            var i = _grid.Rows.Add(b.Name, Loc.T(b.Location), b.Command, b.RemovedAt.ToString("g"));
            _grid.Rows[i].Tag = b;
        }
    }

    private async Task RestoreSelected()
    {
        var chosen = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<StartupBackup>().ToList();
        if (chosen.Count == 0)
        {
            Tell("Select one or more entries first.");
            return;
        }
        var errors = new List<string>();
        foreach (var b in chosen)
        {
            try
            {
                await StartupService.RestoreAsync(b);
                Restored = true;
            }
            catch (Exception ex) { errors.Add($"{b.Name}: {Loc.T(ex.Message)}"); }
        }
        Fill();
        if (errors.Count > 0) Tell("Some entries could not be restored:\n\n" + string.Join("\n", errors), warning: true);
        else Status.Text = Loc.T("Restored.");
    }
}
