using WinSolve.Core;
using WinSolve.Services;
using WinSolve.UI.Pages;

namespace WinSolve.UI;

public sealed class MainForm : Form
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const int SC_RESTORE = 0xF120;

    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly Dictionary<string, Func<Page>> _factories;
    private readonly Dictionary<string, Page> _pages = [];
    private readonly List<NavButton> _nav = [];
    private readonly NotifyIcon _tray;
    private readonly bool _startHidden;
    private bool _exiting;
    private bool _animating;
    private bool _trayHintShown;
    private readonly FlatBtn _updateButton = Theme.Button("Update available", primary: true);
    private UpdateInfo? _update;
    private Label? _brandSubtitle, _versionLabel;

    private static string BrandSubtitle => Admin.IsElevated ? "PC health & maintenance" : "Not running as administrator";

    public MainForm(bool startHidden = false)
    {
        _startHidden = startHidden;
        Text = "WinSolve";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 700);
        Size = new Size(1240, 800);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath); } catch { }

        _factories = new()
        {
            ["home"] = () => new DashboardPage(),
            ["optimize"] = () => new OptimizePage(),
            ["space"] = () => new SpacePage(),
            ["tools"] = () => new ToolsPage(),
            ["monitor"] = () => new MonitorPage(),
            ["hardware"] = () => new HardwarePage(),
            ["tweaks"] = () => new TweaksPage(),
            ["startup"] = () => new StartupPage(),
            ["apps"] = () => new AppsPage(),
            ["drivers"] = () => new DriversPage(),
            ["activation"] = () => new ActivationPage(),
            ["settings"] = () => new SettingsPage(),
        };

        Controls.Add(_content);
        Controls.Add(BuildSidebar());

        _tray = BuildTray();

        Load += (_, _) =>
        {
            ErrorMonitor.Instance.AlertRaised += OnAlert;
            ErrorMonitor.Instance.Apply();
            _ = CheckForUpdateAsync();
            if (!_startHidden) Navigate("home");
        };
        Shown += async (_, _) => await AnimateInAsync();
        HandleCreated += (_, _) => Theme.StyleWindow(this);
        FormClosing += OnFormClosing;
    }

    // ───────────── Hidden start (--tray) ─────────────

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && !IsHandleCreated)
        {
            CreateHandle();
            value = false;
            OnLoad(EventArgs.Empty);
        }
        base.SetVisibleCore(value);
    }

    // ───────────── Notification area ─────────────

    private NotifyIcon BuildTray()
    {
        var menu = Menus.Create();
        menu.Items.Add("Open WinSolve", null, (_, _) => ShowFromTray());
        menu.Items.Add("One-click optimization", null, (_, _) => { ShowFromTray(); Navigate("optimize"); });
        menu.Items.Add("Disk space", null, (_, _) => { ShowFromTray(); Navigate("space"); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        var tray = new NotifyIcon
        {
            Text = Localization.Loc.T("WinSolve - watching for Windows errors"),
            Icon = Icon ?? SystemIcons.Shield,
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => ShowFromTray();
        return tray;
    }

    public async void ShowFromTray()
    {
        if (!Visible)
        {
            Opacity = 0;
            Show();
            if (_pages.Count == 0) Navigate("home");
            else CurrentPage?.OnShown();
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        await AnimateInAsync();
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_exiting || e.CloseReason != CloseReason.UserClosing || !AppSettings.Current.CloseToTray) 
        {
            _tray.Visible = false;
            return;
        }
        e.Cancel = true;
        await AnimateOutAsync();
        CurrentPage?.OnHidden();
        Hide();
        Opacity = 1;
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(3000, Localization.Loc.T("WinSolve is still running"),
                Localization.Loc.T("You'll be alerted if Windows reports an error. Double-click the icon to open WinSolve."), ToolTipIcon.Info);
        }
    }

    public void ExitApp()
    {
        _exiting = true;
        _tray.Visible = false;
        ErrorMonitor.Instance.Dispose();
        Application.Exit();
    }

    // ───────────── Updates ─────────────

    private async Task CheckForUpdateAsync()
    {
        var update = await UpdateService.CheckIfDueAsync();
        if (update is not null) ShowUpdate(update);
    }

    public void ShowUpdate(UpdateInfo update)
    {
        _update = update;
        _updateButton.Text = $"Update to {update.Tag}";
        _updateButton.Visible = true;
        if (!Visible)
            _tray.ShowBalloonTip(4000, Localization.Loc.T("WinSolve update available"), Localization.Loc.T($"Version {update.Tag} is ready to install."), ToolTipIcon.Info);
    }

    public void InstallUpdate(UpdateInfo update)
    {
        if (Localization.Loc.Show(this, $"Install WinSolve {update.Tag}? WinSolve will close and restart when the update is installed.\n\nWhat's new:\n{update.Notes}",
                "WinSolve update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            return;

        var ok = false;
        RunDialog.Run(this, "Updating WinSolve", async (log, progress, ct) =>
        {
            await UpdateService.InstallAsync(update, log, progress, ct);
            ok = true;
        });
        if (ok) ExitApp();
    }

    // ───────────── Alerts ─────────────

    private void OnAlert(Alert alert) => AlertWindow.Show(alert, FixAlert);

    private void FixAlert(Alert alert)
    {
        ShowFromTray();
        if (alert.FixTaskIds.Length > 0)
        {
            var tasks = alert.FixTaskIds.Select(TaskCatalog.Find).OfType<SystemTask>().ToList();
            RunDialog.RunTasks(this, alert.Title, tasks);
        }
        else if (alert.DeviceInstanceId is { } id)
        {
            RunDialog.Run(this, "Repair device", async (log, _, ct) =>
            {
                log("Restarting the device...");
                var r = await HardwareService.RestartDeviceAsync(id, log, ct);
                if (!r.Success)
                {
                    log("Reinstalling the device...");
                    await HardwareService.ReinstallDeviceAsync(id, log, ct);
                }
            });
        }
        else if (alert.NavigateTo is { } target)
        {
            Navigate(target);
        }
    }

    // ───────────── Minimize / restore animations ─────────────

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_SYSCOMMAND && AppSettings.Current.Animations && !_animating)
        {
            var cmd = m.WParam.ToInt64() & 0xFFF0;
            if (cmd == SC_MINIMIZE)
            {
                _ = MinimizeAnimatedAsync();
                return;
            }
            if (cmd == SC_RESTORE && WindowState == FormWindowState.Minimized)
            {
                Opacity = 0;
                base.WndProc(ref m);
                _ = AnimateInAsync();
                return;
            }
        }
        base.WndProc(ref m);
    }

    private async Task MinimizeAnimatedAsync()
    {
        await AnimateOutAsync();
        WindowState = FormWindowState.Minimized;
        Opacity = 1;
    }

    /// <summary>Fades out while sliding down, as if dropping into the taskbar.</summary>
    private async Task AnimateOutAsync()
    {
        if (!AppSettings.Current.Animations) return;
        _animating = true;
        var top = Top;
        const int steps = 12;
        for (int i = 1; i <= steps; i++)
        {
            var t = Ease.InCubic(i / (double)steps);
            Opacity = 1 - t;
            if (WindowState == FormWindowState.Normal) Top = top + (int)(36 * t);
            await Task.Delay(11);
        }
        if (WindowState == FormWindowState.Normal) Top = top;
        _animating = false;
    }

    /// <summary>Fades in while sliding up.</summary>
    private async Task AnimateInAsync()
    {
        if (!AppSettings.Current.Animations)
        {
            Opacity = 1;
            return;
        }
        _animating = true;
        var top = Top;
        var slide = WindowState == FormWindowState.Normal;
        const int steps = 16;
        for (int i = 1; i <= steps; i++)
        {
            var t = Ease.OutCubic(i / (double)steps);
            Opacity = t;
            if (slide) Top = top + (int)(28 * (1 - t));
            await Task.Delay(11);
        }
        if (slide) Top = top;
        Opacity = 1;
        _animating = false;
    }

    // ───────────── Navigation ─────────────

    private Control BuildSidebar()
    {
        var side = new Panel { Dock = DockStyle.Left, Width = 240, BackColor = Theme.Sidebar, Padding = new Padding(4, 14, 4, 10) };

        var list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Sidebar,
            AutoScroll = false,
        };

        (string key, string glyph, string text)[] items =
        [
            ("home", "\uE80F", "Home"),
            ("optimize", "\uE945", "One-click optimization"),
            ("tools", "\uE90F", "Cleanup & repair"),
            ("space", "\uEDA2", "Disk space"),
            ("monitor", "\uE9D9", "Monitor"),
            ("hardware", "\uE950", "Hardware"),
            ("drivers", "\uE772", "Drivers"),
            ("tweaks", "\uE9E9", "Tweaks"),
            ("startup", "\uE7E8", "Startup apps"),
            ("apps", "\uE71D", "Apps"),
            ("activation", "\uE8D7", "Activation"),
            ("settings", "\uE713", "Settings"),
        ];

        foreach (var (key, glyph, text) in items)
        {
            var b = new NavButton(key, glyph, text) { Width = side.Width - side.Padding.Horizontal - 2 };
            b.Click += (_, _) => Navigate(key);
            _nav.Add(b);
            list.Controls.Add(b);
        }

        var brand = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = Theme.Sidebar };
        brand.Controls.Add(new Label
        {
            Text = "WinSolve",
            Font = Theme.H2,
            ForeColor = Theme.Text,
            AutoSize = true,
            UseMnemonic = false,
            Location = new Point(18, 2),
        });
        brand.Controls.Add(_brandSubtitle = new Localization.LocLabel
        {
            Text = BrandSubtitle,
            Font = Theme.Small,
            ForeColor = Admin.IsElevated ? Theme.Muted : Theme.Warn,
            AutoSize = true,
            UseMnemonic = false,
            Location = new Point(19, 28),
        });

        _updateButton.Dock = DockStyle.Bottom;
        _updateButton.Visible = false;
        _updateButton.Click += (_, _) => { if (_update is not null) InstallUpdate(_update); };

        var version = _versionLabel = new Localization.LocLabel
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            Text = $"Version {Application.ProductVersion.Split('+')[0]}",
            ForeColor = Theme.Muted,
            Font = Theme.Small,
            Padding = new Padding(18, 0, 0, 0),
        };

        side.Controls.Add(list);
        side.Controls.Add(_updateButton);
        side.Controls.Add(version);
        side.Controls.Add(brand);
        return side;
    }

    /// <summary>Navigates to a page, or opens an external URI (ms-settings:, https:...).</summary>
    public void Navigate(string key)
    {
        if (key.Contains(':'))
        {
            ProcessRunner.ShellOpen(key);
            return;
        }
        if (!_factories.TryGetValue(key, out var factory)) return;

        if (!_pages.TryGetValue(key, out var page))
        {
            page = factory();
            _pages[key] = page;
        }

        if (CurrentPage is { } previous && previous != page) previous.OnHidden();

        _content.SuspendLayout();
        _content.Controls.Clear();
        _content.Controls.Add(page);
        _content.ResumeLayout();

        foreach (var b in _nav) b.Selected = b.Key == key;
        page.OnShown();
    }

    private Page? CurrentPage => _content.Controls.Count > 0 ? _content.Controls[0] as Page : null;

    /// <summary>Recreates every page (e.g. after the accent color changes).</summary>
    public void Reload(string current)
    {
        _content.Controls.Clear();
        foreach (var p in _pages.Values) p.Dispose();
        _pages.Clear();
        foreach (var b in _nav) b.Invalidate();
        if (_brandSubtitle is not null) _brandSubtitle.Text = BrandSubtitle;
        if (_versionLabel is not null) _versionLabel.Text = $"Version {Application.ProductVersion.Split('+')[0]}";
        if (_update is not null) _updateButton.Text = $"Update to {_update.Tag}";
        _tray.Text = Localization.Loc.T("WinSolve - watching for Windows errors");
        Navigate(current);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tray.Dispose();
        base.Dispose(disposing);
    }
}
