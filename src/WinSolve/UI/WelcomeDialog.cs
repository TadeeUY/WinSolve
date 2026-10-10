using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>First start: language and theme, what WinSolve does, and the background protection.</summary>
public sealed class WelcomeDialog : Form
{
    private readonly Panel _page = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    private readonly StepDots _dots = new() { Dock = DockStyle.Left, Width = 80 };
    private readonly FlatBtn _back, _next;
    private readonly ComboBox _language = Theme.Combo("English", "Español");
    private readonly ComboBox _theme = Theme.Combo("Same as Windows", "Dark mode", "Light mode");
    private readonly CheckBox _alerts, _tray, _restore;
    private int _step;
    private const int Steps = 3;

    /// <summary>The theme changed: WinSolve has to restart to show it.</summary>
    public bool NeedsRestart { get; private set; }

    public WelcomeDialog()
    {
        var s = AppSettings.Current;
        Text = "WinSolve";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        Size = new Size(680, 470);
        Padding = new Padding(28, 22, 28, 18);

        _language.SelectedIndex = s.Language == "es" ? 1 : 0;
        _theme.SelectedIndex = s.ThemeMode switch { "Dark" => 1, "Light" => 2, _ => 0 };
        _language.Width = _theme.Width = 190;
        _alerts = Theme.Toggle("", s.ErrorAlerts);
        _tray = Theme.Toggle("", s.CloseToTray);
        _restore = Theme.Toggle("", s.CreateRestorePoint);
        // Language applies right away, so the rest of the tour is readable.
        _language.SelectedIndexChanged += (_, _) =>
        {
            AppSettings.Current.Language = _language.SelectedIndex == 1 ? "es" : "en";
            ShowStep(_step);
        };

        _back = Theme.Button("Back", (_, _) => ShowStep(_step - 1));
        _next = Theme.Button("Next", (_, _) => { if (_step == Steps - 1) Finish(); else ShowStep(_step + 1); }, primary: true);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, BackColor = Color.Transparent };
        bar.Controls.Add(_next);
        bar.Controls.Add(_back);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 44, BackColor = Color.Transparent };
        footer.Controls.Add(bar);
        footer.Controls.Add(_dots);
        bar.Dock = DockStyle.Fill;
        bar.BringToFront();

        Controls.Add(_page);
        Controls.Add(footer);
        HandleCreated += (_, _) => Theme.StyleWindow(this);
        // Closed with X: keep the defaults, but don't show the welcome again.
        FormClosed += (_, _) => { if (DialogResult != DialogResult.OK) AppSettings.Current.Save(); };
        ShowStep(0);
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, Steps - 1);
        Theme.ClearAndDispose(_page);
        var content = _step switch { 0 => StepWelcome(), 1 => StepTour(), _ => StepProtection() };
        content.Dock = DockStyle.Fill;
        _page.Controls.Add(content);
        Loc.Apply(_page);
        _back.Visible = _step > 0;
        _back.Text = Loc.T("Back");
        _next.Text = Loc.T(_step == Steps - 1 ? "Start using WinSolve" : "Next");
        _dots.Set(_step, Steps);
    }

    private static TableLayoutPanel Column()
    {
        var t = new TableLayoutPanel { ColumnCount = 1, BackColor = Color.Transparent, AutoScroll = false };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    private static void Add(TableLayoutPanel t, Control c)
    {
        t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        c.Dock = DockStyle.Fill;
        t.Controls.Add(c, 0, t.RowCount - 1);
    }

    private static Control Heading(string title, string subtitle)
    {
        var t = Column();
        t.AutoSize = true;
        t.Margin = new Padding(0, 0, 0, 14);
        Add(t, Theme.Label(title, Theme.H1));
        var sub = Theme.Paragraph(subtitle, Theme.Muted);
        Add(t, sub);
        return t;
    }

    private Control StepWelcome()
    {
        var t = Column();
        var logo = new PictureBox { Image = Theme.AppIcon(48).ToBitmap(), SizeMode = PictureBoxSizeMode.CenterImage, Height = 56, Width = 56, Margin = new Padding(0, 0, 0, 8) };
        Add(t, logo);
        logo.Dock = DockStyle.Left;
        Add(t, Heading("Welcome to WinSolve", Loc.T(MainForm.Slogan) + ". " + Loc.T("Let's set it up in three quick steps.")));
        var card = new StackCard().Add(
            Theme.SettingRow("Language", "English or Spanish.", _language),
            new Divider(),
            Theme.SettingRow("Theme", "Light or dark. You can change it later in Settings.", _theme));
        Add(t, card);
        return t;
    }

    private Control StepTour()
    {
        var t = Column();
        Add(t, Heading("What you can do", "Everything is one or two clicks away."));
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        (string Glyph, string Title, string Text)[] items =
        [
            ("", "One-click optimization", "Cleans, repairs and tunes Windows for your PC, with a restore point first."),
            ("", "Disk space", "See what takes up space and clean it safely."),
            ("", "Toolbox", "Disk speed, network test, Wi-Fi passwords, battery report and more."),
            ("", "Search with Ctrl+K", "Type what you want to do and WinSolve finds it."),
        ];
        for (int i = 0; i < items.Length; i++)
        {
            var (glyph, title, text) = items[i];
            var tile = new OptionCard(glyph, title, text) { Dock = DockStyle.Fill, Height = 96, Margin = new Padding(i % 2 == 0 ? 0 : 5, 0, i % 2 == 0 ? 5 : 0, 10), Cursor = Cursors.Default };
            grid.Controls.Add(tile, i % 2, i / 2);
        }
        Add(t, grid);
        return t;
    }

    private Control StepProtection()
    {
        var t = Column();
        Add(t, Heading("Stay protected", "WinSolve can keep an eye on your PC in the background."));
        var card = new StackCard().Add(
            Theme.SettingRow("Alert me when Windows reports an error", "Blue screens, crashes, failing drives and drivers.", _alerts),
            new Divider(),
            Theme.SettingRow("Keep running in the notification area when the window is closed", "Needed for the alerts and the quick actions in the tray.", _tray),
            new Divider(),
            Theme.SettingRow("Create a restore point before making changes", "Lets you undo optimizations and tweaks with System Restore.", _restore));
        Add(t, card);
        return t;
    }

    private void Finish()
    {
        var s = AppSettings.Current;
        var theme = _theme.SelectedIndex switch { 1 => "Dark", 2 => "Light", _ => "System" };
        NeedsRestart = Theme.WouldBeLight(theme) != Theme.IsLight;
        s.ThemeMode = theme;
        s.Language = _language.SelectedIndex == 1 ? "es" : "en";
        s.ErrorAlerts = _alerts.Checked;
        s.CloseToTray = _tray.Checked;
        s.CreateRestorePoint = _restore.Checked;
        s.Save();
        ErrorMonitor.Instance.Apply();
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Progress dots (●○○).</summary>
    private sealed class StepDots : Control
    {
        private int _current, _count;

        public StepDots()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        public void Set(int current, int count) { _current = current; _count = count; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var x = 2;
            for (int i = 0; i < _count; i++)
            {
                var w = i == _current ? 22 : 8;
                using var b = new SolidBrush(i == _current ? Theme.Accent : Theme.Track);
                using var path = Theme.RoundedRect(new Rectangle(x, Height / 2 - 4, w, 8), 4);
                g.FillPath(b, path);
                x += w + 6;
            }
        }
    }
}
