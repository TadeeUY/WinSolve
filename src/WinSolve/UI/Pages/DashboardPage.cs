using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class DashboardPage : Page
{
    private readonly Stack _body = new(scroll: true);
    private readonly ScoreRing _ring = new();
    private readonly Label _summary = Theme.Label("Not scanned yet", Theme.H2);
    private readonly Label _scanStatus = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly TableLayoutPanel _tiles = new() { ColumnCount = 4, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8) };
    private readonly StackCard _issues = new();
    private readonly FlatBtn _scanButton;
    private bool _scanned;
    private bool _scanning;

    public override string Key => "home";

    public DashboardPage() : base("Home", "Overall health of this PC and the problems that need attention.")
    {
        _scanButton = Theme.Button("Scan again", async (_, _) => await ScanAsync(), glyph: "\uE72C");

        // Hero: score ring + summary + main actions.
        var hero = new Card { Height = 156, Padding = new Padding(20, 16, 20, 16) };
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, BackColor = Color.Transparent };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _ring.Anchor = AnchorStyles.Left;
        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, BackColor = Color.Transparent, WrapContents = false, Padding = new Padding(0, 14, 0, 0) };
        right.Controls.AddRange([
            _summary, _scanStatus,
            Theme.Row(Theme.Button("Optimize now", (_, _) => Main.Navigate("optimize"), primary: true, glyph: "\uE945"), _scanButton),
        ]);
        grid.Controls.Add(_ring, 0, 0);
        grid.Controls.Add(right, 1, 0);
        hero.Controls.Add(grid);

        // Quick actions.
        var actions = new TableLayoutPanel { ColumnCount = 4, Height = 92, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8) };
        (string Glyph, string Title, string Subtitle, string Target)[] quick =
        [
            ("\uE74D", "Clean junk files", "Temp files, caches and logs", "tools"),
            ("\uEDA2", "Disk space", "See what takes up space", "space"),
            ("\uE772", "Drivers", "Update or clean install", "drivers"),
            ("\uE9D9", "Monitor", "Live usage and temperatures", "monitor"),
        ];
        for (int i = 0; i < quick.Length; i++)
        {
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            var q = quick[i];
            var tile = new ActionTile(q.Glyph, q.Title, q.Subtitle) { Dock = DockStyle.Fill, Margin = new Padding(0, 0, i == quick.Length - 1 ? 0 : 8, 0) };
            tile.Click += (_, _) => Main.Navigate(q.Target);
            actions.Controls.Add(tile, i, 0);
        }

        for (int i = 0; i < 4; i++) _tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));

        _body.Add(hero, actions, Theme.Label("This PC", Theme.H2), _tiles, _issues);
        AddRow(_body, fill: true);
    }

    public override async void OnShown()
    {
        if (!_scanned && !_scanning && AppSettings.Current.ScanOnStartup)
            await ScanAsync();
        else if (!_scanned && !_scanning)
            _scanStatus.Text = "Select Scan to check this PC.";
        _scanButton.Text = _scanned ? "Scan again" : "Scan";
    }

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        _scanButton.Enabled = false;
        _ring.Score = null;
        _summary.Text = "Scanning...";

        try
        {
            var result = await HealthScanner.ScanAsync(s => { if (InvokeRequired) BeginInvoke(() => _scanStatus.Text = s); else _scanStatus.Text = s; });
            _scanned = true;
            Render(result);
        }
        catch (Exception ex)
        {
            _scanStatus.Text = "Scan failed: " + ex.Message;
            Logger.Write($"Scan error: {ex}");
        }
        finally
        {
            _scanning = false;
            _scanButton.Enabled = true;
            _scanButton.Text = "Scan again";
        }
    }

    private static Control Tile(string caption, string value, string detail, Color? valueColor = null, bool last = false)
    {
        var card = new StackCard { Margin = new Padding(0, 0, 8, 0), Padding = new Padding(14, 12, 14, 12) };
        var v = Theme.Label(value, Theme.H2, valueColor);
        v.AutoEllipsis = true;
        card.Add(Theme.Label(caption, Theme.Small, Theme.Muted), v, Theme.Paragraph(detail, font: Theme.Small));
        card.Dock = DockStyle.Fill;
        if (last) card.Margin = new Padding(0);
        return card;
    }

    private void Render(ScanResult r)
    {
        SuspendLayout();
        var score = r.Score;
        _ring.Score = score;
        _summary.Text = score >= 85 ? "Your PC is in good shape"
            : score >= 60 ? "A few things could be improved"
            : "Your PC needs attention";
        _scanStatus.Text = $"Last scan {DateTime.Now:t}  ·  {r.Issues.Count} item(s)  ·  {Format.Bytes(r.JunkBytes)} of junk files";

        var s = r.System;
        _tiles.Controls.Clear();
        var sysDrive = s.Drives.FirstOrDefault(d => Environment.SystemDirectory.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase)) ?? s.Drives.FirstOrDefault();
        _tiles.Controls.Add(Tile("Windows", s.OsName.Replace("Windows ", "Windows "), s.OsVersion), 0, 0);
        _tiles.Controls.Add(Tile("Processor", s.Threads > 0 ? $"{s.Cores} cores / {s.Threads} threads" : "—", s.Cpu), 1, 0);
        _tiles.Controls.Add(Tile("Memory", Format.Bytes(s.RamTotal), $"{Format.Bytes(s.RamFree)} available"), 2, 0);
        _tiles.Controls.Add(sysDrive is null
            ? Tile("System drive", "—", "", last: true)
            : Tile($"System drive ({sysDrive.Name})", $"{Format.Bytes(sysDrive.Free)} free", $"of {Format.Bytes(sysDrive.Total)}",
                sysDrive.FreePercent < 10 ? Theme.Warn : null, last: true), 3, 0);

        _issues.Body.Controls.Clear();
        _issues.Add(Theme.Label(r.Issues.Count == 0 ? "No problems found" : $"Recommendations ({r.Issues.Count})", Theme.H2));

        var details = new List<(string, string, Color)>();
        foreach (var d in r.Disks) details.Add(("Drive", $"{d.Model}  ·  {d.MediaType}  ·  {d.HealthText}" + (d.TemperatureC is { } t ? $"  ·  {t} °C" : ""), Theme.For(d.Health)));
        details.Add(("Activation", r.Activation.StatusText, r.Activation.IsActivated ? Theme.Good : Theme.Warn));
        details.Add(("Graphics", s.Gpus.Count > 0 ? string.Join(", ", s.Gpus) : "—", Theme.Muted));

        bool first = true;
        foreach (var issue in r.Issues.OrderByDescending(i => i.Severity))
        {
            if (!first) _issues.Add(new Divider());
            first = false;
            _issues.Add(BuildIssueRow(issue));
        }
        if (r.Issues.Count == 0)
            _issues.Add(Theme.Paragraph("Everything checked out. Run a scan again any time."));

        _issues.Add(new Divider());
        _issues.Add(Theme.Label("Components", Theme.BodyBold));
        foreach (var (k, v, c) in details)
        {
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(Theme.Label(k, Theme.Body, Theme.Muted), 0, 0);
            row.Controls.Add(Theme.Status(v, c), 1, 0);
            _issues.Add(row);
        }

        ResumeLayout(true);
    }

    private Control BuildIssueRow(Issue issue)
    {
        var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var dot = new StatusLabel("", Theme.For(issue.Severity)) { Width = 14, Margin = new Padding(0, 4, 0, 0) };
        var text = new Stack().Add(Theme.Label(issue.Title, Theme.BodyBold), Theme.Paragraph(issue.Detail));
        row.Controls.Add(dot, 0, 0);
        row.Controls.Add(text, 1, 0);
        text.Dock = DockStyle.Fill;

        if (issue.FixTaskIds.Length > 0 || issue.NavigateTo is not null)
        {
            var b = Theme.Button(issue.FixLabel, async (_, _) =>
            {
                if (issue.FixTaskIds.Length > 0)
                {
                    var tasks = issue.FixTaskIds.Select(TaskCatalog.Find).OfType<SystemTask>().ToList();
                    if (!Confirm($"The following will run:\n\n- {string.Join("\n- ", tasks.Select(t => Localization.Loc.T(t.Title)))}\n\nContinue?")) return;
                    RunDialog.RunTasks(this, issue.Title, tasks);
                    await ScanAsync();
                }
                else if (issue.NavigateTo is { } target)
                {
                    Main.Navigate(target);
                }
            });
            b.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            row.Controls.Add(b, 2, 0);
        }
        return row;
    }
}
