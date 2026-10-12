using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>
/// Lets the person choose which leftover folders a force uninstall deletes. Program folders are
/// ticked; AppData/ProgramData folders that may hold their data (settings, saves, documents) are not.
/// </summary>
internal sealed class LeftoversDialog : ToolDialog
{
    private readonly DataGridView _grid = new();

    public IReadOnlyList<Leftover> Chosen { get; private set; } = [];

    public LeftoversDialog(IReadOnlyList<Leftover> leftovers) : base("", "Leftover folders",
        "Choose the folders to delete after the uninstaller. Ticked folders go to the Recycle Bin when possible.", new Size(940, 560))
    {
        AddRow(Theme.Paragraph("Folders marked \"May contain your data\" are not ticked: they can hold settings, saved games, projects or documents. Tick them only if you are sure you don't need what is inside."));

        Theme.StyleGrid(_grid);
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "on", HeaderText = "Delete", FillWeight = 34 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "app", HeaderText = "Program", ReadOnly = true, FillWeight = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "path", HeaderText = "Folder", ReadOnly = true, FillWeight = 260 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "note", HeaderText = "Notes", ReadOnly = true, FillWeight = 150 });
        _grid.Columns["note"]!.DefaultCellStyle.ForeColor = Theme.Muted;
        _grid.CellContentClick += (_, e) => { if (e.ColumnIndex == 0) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        foreach (var l in leftovers)
        {
            var i = _grid.Rows.Add(l.CheckedByDefault, l.Program.DisplayName, l.Path, Loc.T(l.Note));
            _grid.Rows[i].Tag = l;
            if (l.Kind == LeftoverKind.UserData) _grid.Rows[i].Cells["note"].Style.ForeColor = Theme.Warn;
        }
        AddRow(Theme.InCard(_grid), fill: true);

        Buttons.Controls.Add(Theme.Button("Continue", (_, _) =>
        {
            _grid.EndEdit();
            Chosen = _grid.Rows.Cast<DataGridViewRow>().Where(r => r.Cells[0].Value is true).Select(r => (Leftover)r.Tag!).ToList();
            DialogResult = DialogResult.OK;
            Close();
        }, primary: true));
    }
}
