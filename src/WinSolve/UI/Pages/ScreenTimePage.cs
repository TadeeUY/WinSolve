using System.Drawing.Drawing2D;
using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

/// <summary>How long the PC is used each day, and lock hours to take a break from it.</summary>
public sealed class ScreenTimePage : Page
{
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 30_000 };
    private readonly Label _today = Theme.Label("—", Theme.Big), _todayDetail = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly Label _week = Theme.Label("—", Theme.Big), _weekDetail = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly Label _lockHours = Theme.Label("—", Theme.Big), _lockDetail = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly UsageBars _bars = new() { Height = 220, Margin = new Padding(0, 6, 0, 0) };
    private readonly CheckBox _enabled = Theme.Toggle("", false);
    private readonly ComboBox _from = Theme.Combo(Times()), _to = Theme.Combo(Times());
    private readonly ComboBox _relock = Theme.Combo(LockSchedule.RelockChoices.Select(m => $"{m} minutes").ToArray());
    private readonly Dictionary<DayOfWeek, CheckBox> _days = [];
    private readonly Label _status = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly Label _autoStartNote = Theme.Label("", Theme.Small, Theme.Warn);

    public override string Key => "screentime";

    public ScreenTimePage() : base("Screen time", "How long you use the PC each day, and lock hours to take a break from it.")
    {
        var tiles = new TableLayoutPanel { ColumnCount = 3, Height = 150, BackColor = Color.Transparent, Margin = new Padding(0) };
        for (var i = 0; i < 3; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        tiles.Controls.Add(Tile("Today", _today, _todayDetail, 8), 0, 0);
        tiles.Controls.Add(Tile("Last 7 days", _week, _weekDetail, 8), 1, 0);
        tiles.Controls.Add(Tile("During lock hours (7 days)", _lockHours, _lockDetail, 0), 2, 0);

        var chart = new StackCard().Add(
            Theme.SectionHeader("", "Each day", "Time with the screen on and the PC unlocked. The part used during lock hours is shown in another color."),
            _bars);

        _from.Width = _to.Width = 100;
        _relock.Width = 130;
        var dayRow = Theme.Row();
        foreach (var day in WeekFromMonday())
        {
            var check = Theme.Check(Loc.Culture.DateTimeFormat.GetAbbreviatedDayName(day), false);
            check.Margin = new Padding(0, 3, 14, 3);
            _days[day] = check;
            dayRow.Controls.Add(check);
        }

        var schedule = new StackCard().Add(
            Theme.SectionHeader("", "Lock hours", "The PC locks itself during these hours. You can unlock it, but it locks again every few minutes."),
            Theme.SettingRow("Lock the PC during these hours", "You get a warning one minute before. Unlocking during lock hours counts above.", _enabled),
            new Divider(),
            Theme.SettingRow("From", null, _from),
            Theme.SettingRow("To", "If it is earlier than the start, the lock hours end the next morning.", _to),
            new Divider(),
            Theme.Label("Days", Theme.Body),
            dayRow,
            new Divider(),
            Theme.SettingRow("Lock again after unlocking", null, _relock),
            Theme.Row(
                Theme.Button("Save", (_, _) => SaveSchedule(), primary: true, glyph: ""),
                Theme.Button("Lock now", (_, _) => ScreenTimeWatcher.LockNow(), glyph: "")),
            _status,
            _autoStartNote);

        AddRow(new Stack(scroll: true).Add(tiles, chart, schedule), fill: true);
        _refresh.Tick += (_, _) => Fill();
        LoadSchedule();
    }

    private static Card Tile(string caption, Label value, Label detail, int right)
    {
        var card = new Card { Dock = DockStyle.Fill, Margin = new Padding(0, 0, right, 8), Padding = new Padding(14, 12, 14, 12) };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
        layout.Controls.Add(Theme.Label(caption, Theme.Body, Theme.Muted));
        layout.Controls.Add(value);
        layout.Controls.Add(detail);
        card.Controls.Add(layout);
        return card;
    }

    private static string[] Times() => Enumerable.Range(0, 48).Select(i => LockSchedule.Clock(i * 30)).ToArray();

    private static IEnumerable<DayOfWeek> WeekFromMonday() => Enumerable.Range(1, 7).Select(i => (DayOfWeek)(i % 7));

    private static ScreenTimeData? Data => ScreenTimeWatcher.Instance?.Data;

    private void LoadSchedule()
    {
        var s = Data?.Schedule ?? new LockSchedule();
        _enabled.Checked = s.Enabled;
        _from.SelectedIndex = Math.Clamp(s.StartMinute / 30, 0, 47);
        _to.SelectedIndex = Math.Clamp(s.EndMinute / 30, 0, 47);
        var relock = Array.IndexOf(LockSchedule.RelockChoices, s.RelockMinutes);
        _relock.SelectedIndex = relock >= 0 ? relock : 1;
        foreach (var (day, check) in _days) check.Checked = s.Days.Contains(day);
    }

    private void SaveSchedule()
    {
        if (ScreenTimeWatcher.Instance is not { } watcher)
        {
            _status.Text = Loc.T("Screen time isn't available in this session.");
            return;
        }
        var schedule = new LockSchedule
        {
            Enabled = _enabled.Checked,
            StartMinute = _from.SelectedIndex * 30,
            EndMinute = _to.SelectedIndex * 30,
            Days = _days.Where(d => d.Value.Checked).Select(d => d.Key).ToList(),
            RelockMinutes = LockSchedule.RelockChoices[Math.Max(0, _relock.SelectedIndex)],
        };
        if (schedule.Enabled && schedule.StartMinute == schedule.EndMinute)
        {
            _status.Text = Loc.T("The start and the end can't be the same time.");
            return;
        }
        if (schedule.Enabled && schedule.Days.Count == 0)
        {
            _status.Text = Loc.T("Pick at least one day.");
            return;
        }
        watcher.UpdateSchedule(schedule);
        _status.Text = Loc.T("Saved.");
        Fill();
    }

    public override async void OnShown()
    {
        Fill();
        _refresh.Start();
        // The lock hours only work while WinSolve runs, so it should start with Windows.
        var autoStart = Edition.IsPortable ? false : await Task.Run(AutoStart.IsEnabled);
        if (IsDisposed) return;
        _autoStartNote.Text = autoStart ? "" : Loc.T(Edition.IsPortable
            ? "The portable version only counts and locks while it is open."
            : "Screen time is only counted, and the PC only locks, while WinSolve is running. Turn on 'Start WinSolve with Windows' in Settings.");
    }

    public override void OnHidden() => _refresh.Stop();

    internal void Fill()
    {
        if (Data is not { } data) return;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var days = data.LastDays(today);
        var previous = data.LastDays(today.AddDays(-7));
        var week = TimeSpan.FromTicks(days.Sum(d => d.Total.Ticks));
        var previousWeek = TimeSpan.FromTicks(previous.Sum(d => d.Total.Ticks));
        var lockTime = TimeSpan.FromTicks(days.Sum(d => d.LockHours.Ticks));
        var unlocks = days.Sum(d => d.Unlocks);

        _today.Text = ScreenTimeData.Format(days[^1].Total);
        var schedule = data.Schedule;
        _todayDetail.Text = ScreenTimeWatcher.Instance?.LockAt is { } at
            ? string.Format(Loc.T("Locks again at {0}"), at.ToString("HH:mm"))
            : schedule.Enabled
                ? string.Format(Loc.T("Lock hours: {0} to {1}"), LockSchedule.Clock(schedule.StartMinute), LockSchedule.Clock(schedule.EndMinute))
                : Loc.T("No lock hours set");
        _week.Text = ScreenTimeData.Format(week);
        var change = previousWeek.TotalMinutes >= 30 ? (week - previousWeek) / previousWeek : (double?)null;
        _weekDetail.Text = string.Format(Loc.T("{0} a day on average"), ScreenTimeData.Format(week / 7))
                           + (change is { } c ? "  ·  " + string.Format(Loc.T(c >= 0 ? "{0}% more than the 7 days before" : "{0}% less than the 7 days before"), Math.Abs(Math.Round(c * 100))) : "");
        _lockHours.Text = ScreenTimeData.Format(lockTime);
        _lockHours.ForeColor = lockTime > TimeSpan.Zero ? Theme.Warn : Theme.Text;
        _lockDetail.Text = string.Format(Loc.T(unlocks == 1 ? "Unlocked {0} time" : "Unlocked {0} times"), unlocks);
        _bars.SetDays(days);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refresh.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Bar chart of screen time per day, with the part used during lock hours on top.</summary>
public sealed class UsageBars : Control
{
    private List<DayTotal> _days = [];

    public UsageBars()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Font = Theme.Small;
    }

    public void SetDays(List<DayTotal> days)
    {
        _days = days;
        AccessibleName = string.Join(", ", days.Select(d => $"{d.Date.ToString("dddd", Loc.Culture)} {ScreenTimeData.Format(d.Total)}"));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.SurfaceColor(this));
        if (_days.Count == 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = DeviceDpi / 96f;
        var labelHeight = (int)(22 * scale);
        var valueHeight = (int)(18 * scale);
        var chart = new Rectangle(0, valueHeight, Width, Height - labelHeight - valueHeight);
        var max = Math.Max(_days.Max(d => d.Total.TotalMinutes), 60);
        var slot = Width / (float)_days.Count;
        var barWidth = Math.Min(slot * 0.55f, 56 * scale);
        var today = DateOnly.FromDateTime(DateTime.Now);

        using var track = new SolidBrush(Theme.Track);
        using var bar = new SolidBrush(Theme.Accent);
        using var late = new SolidBrush(Theme.Warn);
        for (var i = 0; i < _days.Count; i++)
        {
            var d = _days[i];
            var x = slot * i + (slot - barWidth) / 2;
            var full = new RectangleF(x, chart.Top, barWidth, chart.Height);
            using (var path = Rounded(full, 4 * scale)) g.FillPath(track, path);

            var h = (float)(d.Total.TotalMinutes / max * chart.Height);
            if (h > 0.5f)
            {
                var used = new RectangleF(x, chart.Bottom - h, barWidth, h);
                using (var path = Rounded(used, 4 * scale)) g.FillPath(bar, path);
                var lh = (float)(d.LockHours.TotalMinutes / max * chart.Height);
                if (lh > 0.5f)
                {
                    var lockPart = new RectangleF(x, chart.Bottom - h, barWidth, Math.Min(lh, h));
                    using var path = Rounded(lockPart, 4 * scale);
                    g.FillPath(late, path);
                }
            }

            var value = d.Total > TimeSpan.Zero ? ScreenTimeData.Format(d.Total) : "";
            var valueTop = Math.Max(0, (int)(chart.Bottom - h) - valueHeight);
            TextRenderer.DrawText(g, value, Font, new Rectangle((int)(slot * i), valueTop, (int)slot, valueHeight), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.NoPrefix);
            var name = d.Date == today ? Loc.T("Today") : d.Date.ToString("ddd d", Loc.Culture);
            TextRenderer.DrawText(g, name, d.Date == today ? Theme.BodyBold : Font, new Rectangle((int)(slot * i), chart.Bottom + (int)(4 * scale), (int)slot, labelHeight),
                d.Date == today ? Theme.Text : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d < 1)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
