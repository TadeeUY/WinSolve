using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class ToolsPage : Page
{
    private readonly TaskRunnerView _runner = new();
    private readonly FlatBtn _run;
    private readonly Label _selection = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly Dictionary<string, CheckBox> _boxes = [];

    private static readonly (TaskCategory Category, string Glyph, string Subtitle)[] Sections =
    [
        (TaskCategory.Cleanup, "", "Free up space by removing files Windows doesn't need."),
        (TaskCategory.Repair, "", "Fix Windows components, the system drive and common problems."),
        (TaskCategory.Performance, "", "Drive optimization and power plans."),
        (TaskCategory.Network, "", "Fix connection and DNS problems."),
        (TaskCategory.Security, "", "Microsoft Defender updates and scans."),
    ];

    public override string Key => "tools";

    public ToolsPage() : base("Cleanup & repair",
        "Select the tasks to run. Tasks marked Slow can take several minutes.")
    {
        _run = Theme.Button("Run selected", async (_, _) => await RunSelected(), primary: true, glyph: "");
        _runner.BusyChanged += busy => _run.Enabled = !busy;

        AddRow(Theme.Row(_run,
            Theme.Button("Select recommended", (_, _) => SetChecks(t => t.Recommended), glyph: ""),
            Theme.Button("Clear selection", (_, _) => SetChecks(_ => false)),
            Theme.Button("Windows tools", (s, _) => ShowToolsMenu((Control)s!), glyph: "")));
        AddRow(_selection);

        var columns = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var left = new Stack { Margin = new Padding(0, 0, 8, 0) };
        var right = new Stack();
        foreach (var (category, glyph, subtitle) in Sections)
        {
            var card = Section(category, glyph, subtitle);
            (category is TaskCategory.Cleanup or TaskCategory.Network ? left : right).Add(card);
        }
        left.Dock = right.Dock = DockStyle.Fill;
        columns.Controls.Add(left, 0, 0);
        columns.Controls.Add(right, 1, 0);

        AddRow(new Stack(scroll: true).Add(columns), fill: true);
        AddRow(_runner, height: 220);
        SetChecks(t => t.Recommended);
    }

    private Control Section(TaskCategory category, string glyph, string subtitle)
    {
        var card = new StackCard();
        card.Add(Theme.SectionHeader(glyph, category == TaskCategory.Repair ? "Repair" : category.ToString(), subtitle));

        foreach (var t in TaskCatalog.All.Where(t => t.Category == category))
        {
            card.Add(new Divider { Margin = new Padding(0, 2, 0, 2) });
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var text = new Stack();
            var box = Theme.Check(t.Title, false);
            box.CheckedChanged += (_, _) => UpdateSelection();
            var desc = Theme.Paragraph(t.Description, font: Theme.Small);
            desc.Margin = new Padding(28, 0, 0, 2);
            text.Add(box, desc);
            text.Dock = DockStyle.Fill;

            var badges = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(6, 4, 0, 0), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            if (t.Slow) badges.Controls.Add(Badge("Slow", Theme.Warn));
            if (t.NeedsReboot) badges.Controls.Add(Badge("Restart", Theme.Info));
            if (t.Recommended) badges.Controls.Add(Badge("Recommended", Theme.Good));

            row.Controls.Add(text, 0, 0);
            row.Controls.Add(badges, 1, 0);
            card.Add(row);
            _boxes[t.Id] = box;
        }
        return card;
    }

    private static Control Badge(string text, Color color) => new Pill(text, color) { Margin = new Padding(4, 0, 0, 0) };

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

    private void SetChecks(Func<SystemTask, bool> predicate)
    {
        foreach (var (id, box) in _boxes) box.Checked = predicate(TaskCatalog.Find(id)!);
        UpdateSelection();
    }

    private List<SystemTask> Selected() => TaskCatalog.All.Where(t => _boxes.TryGetValue(t.Id, out var b) && b.Checked).ToList();

    private void UpdateSelection()
    {
        var sel = Selected();
        _selection.Text = sel.Any(t => t.Slow)
            ? $"{sel.Count} task(s) selected  ·  {sel.Count(t => t.Slow)} slow"
            : $"{sel.Count} task(s) selected";
    }

    private async Task RunSelected()
    {
        var tasks = Selected();
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
