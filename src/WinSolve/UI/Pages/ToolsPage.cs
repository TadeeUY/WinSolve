using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class ToolsPage : Page
{
    private readonly DataGridView _grid = new();
    private readonly ComboBox _filter;
    private readonly TaskRunnerView _runner = new();
    private readonly FlatBtn _run;
    private readonly HashSet<string> _checked = [.. TaskCatalog.All.Where(t => t.Recommended).Select(t => t.Id)];

    private static readonly (string Label, TaskCategory? Cat)[] Filters =
    [
        ("All categories", null),
        ("Cleanup", TaskCategory.Cleanup),
        ("Performance", TaskCategory.Performance),
        ("Repair", TaskCategory.Repair),
        ("Network", TaskCategory.Network),
        ("Security", TaskCategory.Security),
    ];

    public override string Key => "tools";

    public ToolsPage() : base("Cleanup & repair",
        "Select the tasks to run. Tasks marked Slow can take several minutes.")
    {
        _filter = Theme.Combo(Filters.Select(f => f.Label).ToArray());
        _filter.SelectedIndexChanged += (_, _) => Fill();

        _run = Theme.Button("Run selected", async (_, _) => await RunSelected(), primary: true);
        _runner.BusyChanged += busy => _run.Enabled = !busy;

        var toolbar = Theme.Row(_filter, _run,
            Theme.Button("Select recommended", (_, _) => SetChecks(t => t.Recommended)),
            Theme.Button("Clear selection", (_, _) => SetChecks(_ => false)),
            Theme.Button("Windows tools", (s, _) => ShowToolsMenu((Control)s!)));

        BuildGrid();
        var split = new SplitContainer { Orientation = Orientation.Horizontal, BackColor = Theme.Background, SplitterWidth = 10 };
        split.Panel1.Controls.Add(_grid);
        split.Panel2.Controls.Add(_runner);
        split.HandleCreated += (_, _) => { try { split.SplitterDistance = (int)(split.Height * 0.58); } catch { } };

        AddRow(toolbar);
        AddRow(split, fill: true);
        Fill();
    }

    private static void ShowToolsMenu(Control anchor)
    {
        var menu = Menus.Create();
        (string Text, string Target)[] tools =
        [
            ("Task Manager", "taskmgr.exe"),
            ("Device Manager", "devmgmt.msc"),
            ("System Restore", "rstrui.exe"),
            ("Disk Cleanup", "cleanmgr.exe"),
            ("Resource Monitor", "resmon.exe"),
            ("Services", "services.msc"),
            ("Event Viewer", "eventvwr.msc"),
            ("System Information", "msinfo32.exe"),
        ];
        foreach (var (text, target) in tools) menu.Items.Add(text, null, (_, _) => ProcessRunner.ShellOpen(target));
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void BuildGrid()
    {
        Theme.StyleGrid(_grid);
        _grid.Dock = DockStyle.Fill;
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "", Width = 40, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "title", HeaderText = "Task", ReadOnly = true, Width = 290, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "cat", HeaderText = "Category", ReadOnly = true, Width = 110, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "desc", HeaderText = "Description", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "notes", HeaderText = "Notes", ReadOnly = true, Width = 120, AutoSizeMode = DataGridViewAutoSizeColumnMode.None });
        _grid.Columns["cat"]!.DefaultCellStyle.ForeColor = Theme.Muted;
        _grid.Columns["desc"]!.DefaultCellStyle.ForeColor = Theme.Muted;
        _grid.Columns["notes"]!.DefaultCellStyle.ForeColor = Theme.Muted;
        _grid.CellContentClick += (_, e) => { if (e.ColumnIndex == 0) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (e.RowIndex >= 0 && _grid.Rows[e.RowIndex].Tag is SystemTask t) e.ToolTipText = t.Description;
        };
    }

    private void SaveChecks()
    {
        foreach (DataGridViewRow row in _grid.Rows)
        {
            var t = (SystemTask)row.Tag!;
            if (row.Cells["sel"].Value is true) _checked.Add(t.Id); else _checked.Remove(t.Id);
        }
    }

    private void Fill()
    {
        SaveChecks();
        var cat = Filters[Math.Max(0, _filter.SelectedIndex)].Cat;
        _grid.Rows.Clear();
        foreach (var t in TaskCatalog.All.Where(t => cat is null || t.Category == cat))
        {
            var notes = string.Join(", ", new[] { t.Slow ? "Slow" : null, t.NeedsReboot ? "Restart" : null }.OfType<string>());
            var i = _grid.Rows.Add(_checked.Contains(t.Id), t.Title, t.Category.ToString(), t.Description, notes);
            _grid.Rows[i].Tag = t;
        }
    }

    private void SetChecks(Func<SystemTask, bool> predicate)
    {
        foreach (DataGridViewRow row in _grid.Rows)
            row.Cells["sel"].Value = predicate((SystemTask)row.Tag!);
        SaveChecks();
    }

    private async Task RunSelected()
    {
        _grid.EndEdit();
        SaveChecks();
        var tasks = TaskCatalog.All.Where(t => _checked.Contains(t.Id)).ToList();
        if (tasks.Count == 0)
        {
            Info("No tasks selected.");
            return;
        }

        var slow = tasks.Count(t => t.Slow);
        if (!Confirm($"Run {tasks.Count} task(s)?" + (slow > 0 ? $" {slow} of them can take several minutes." : "")))
            return;

        TaskContext? final = null;
        await _runner.RunTasksAsync(tasks, ctx => final = ctx);
        if (final?.RebootRecommended == true) AskReboot(this);
    }
}
