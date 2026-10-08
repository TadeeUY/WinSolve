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
    private readonly InfoBar _infoBar = new() { Dock = DockStyle.Top, Visible = false };
    private UpdateInfo? _update;
    private bool _updating;
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
        Icon = Theme.AppIcon(32);

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

        // Content area: optional info bar (updates) above the current page.
        var main = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(0) };
        var barHost = new Panel { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Background, Padding = new Padding(32, 14, 32, 0) };
        barHost.Controls.Add(_infoBar);
        barHost.Visible = false;
        main.Controls.Add(_content);
        main.Controls.Add(barHost);
        Controls.Add(main);
        Controls.Add(BuildSidebar());

        _tray = BuildTray();

        Load += (_, _) =>
        {
            ErrorMonitor.Instance.AlertRaised += OnAlert;
            ErrorMonitor.Instance.Apply();
            _ = CheckForUpdateAsync();
            // Keep checking while WinSolve sits in the notification area.
            var updateTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromHours(4).TotalMilliseconds };
            updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
            updateTimer.Start();
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
            Icon = Theme.AppIcon(16),
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
        if (update is null) return;
        if (AppSettings.Current.AutoInstallUpdates) await InstallUpdateAsync(update);
        else ShowUpdate(update);
    }

    /// <summary>Shows the "update available" bar at the top of every page.</summary>
    public void ShowUpdate(UpdateInfo update)
    {
        _update = update;
        var install = Theme.Button("Update now", async (_, _) => await InstallUpdateAsync(update), primary: true, glyph: "");
        var notes = Theme.Button("What's new", (_, _) => ProcessRunner.ShellOpen(update.ReleaseUrl));
        _infoBar.Show($"WinSolve {update.Tag} is available",
            $"You have {UpdateService.CurrentVersion}. Updating takes less than a minute and keeps your settings.", install, notes);
        if (!Visible)
            _tray.ShowBalloonTip(4000, Localization.Loc.T("WinSolve update available"), Localization.Loc.T($"Version {update.Tag} is ready to install."), ToolTipIcon.Info);
    }

    /// <summary>
    /// One-click update: downloads the new installer (SHA-256 verified), runs it silently for
    /// the same install scope and reopens WinSolve. No dialogs.
    /// </summary>
    public async Task InstallUpdateAsync(UpdateInfo update)
    {
        if (_updating) return;
        _updating = true;
        ShowFromTray();
        _infoBar.Show($"Updating to WinSolve {update.Tag}", "Downloading...");
        _infoBar.SetProgress(0);
        try
        {
            await Task.Run(() => UpdateService.InstallAsync(update,
                line => Logger.Write("[update] " + line),
                (done, total) => BeginInvoke(() =>
                {
                    _infoBar.SetProgress(total == 0 ? 1 : (double)done / total);
                    _infoBar.SetMessage($"Downloading... {done}%");
                }),
                CancellationToken.None));
            _infoBar.SetMessage("Installing. WinSolve will reopen in a moment.");
            _infoBar.SetProgress(-1);
            await Task.Delay(800);
            ExitApp();
        }
        catch (Exception ex)
        {
            Logger.Write($"Update failed: {ex}");
            _updating = false;
            var retry = Theme.Button("Try again", async (_, _) => await InstallUpdateAsync(update), primary: true);
            var manual = Theme.Button("Download manually", (_, _) => ProcessRunner.ShellOpen(update.ReleaseUrl));
            _infoBar.Show("The update could not be installed", ex.Message, retry, manual);
        }
    }

    /// <summary>Manual check from Settings: shows the bar, or says you're up to date.</summary>
    public async Task CheckForUpdatesNowAsync()
    {
        try
        {
            var update = await UpdateService.CheckAsync();
            if (update is null)
                _infoBar.Show("WinSolve is up to date", $"Version {UpdateService.CurrentVersion} is the latest release.");
            else
                ShowUpdate(update);
        }
        catch (Exception ex)
        {
            _infoBar.Show("Could not check for updates", ex.Message);
        }
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
        var side = new Panel { Dock = DockStyle.Left, Width = 248, BackColor = Theme.Sidebar, Padding = new Padding(4, 12, 4, 10) };

        var list = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Sidebar,
            AutoScroll = false,
        };

        // A null key is a section header.
        (string? key, string glyph, string text)[] items =
        [
            ("home", "", "Home"),
            ("optimize", "", "One-click optimization"),
            (null, "", "Maintain"),
            ("tools", "", "Cleanup & repair"),
            ("space", "", "Disk space"),
            ("startup", "", "Startup apps"),
            ("apps", "", "Apps"),
            ("drivers", "", "Drivers"),
            (null, "", "Diagnose"),
            ("monitor", "", "Monitor"),
            ("hardware", "", "Hardware"),
            (null, "", "System"),
            ("tweaks", "", "Tweaks"),
            ("activation", "", "Activation"),
        ];

        var width = side.Width - side.Padding.Horizontal - 2;
        foreach (var (key, glyph, text) in items)
        {
            if (key is null) list.Controls.Add(new NavHeader(text) { Width = width });
            else list.Controls.Add(NavItem(key, glyph, text, width));
        }

        // Brand: logo + name.
        var brand = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Sidebar };
        brand.Controls.Add(new PictureBox
        {
            Image = Theme.AppIcon(32).ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(32, 32),
            Location = new Point(16, 6),
            BackColor = Color.Transparent,
        });
        brand.Controls.Add(new Label
        {
            Text = "WinSolve",
            Font = Theme.H2,
            ForeColor = Theme.Text,
            AutoSize = true,
            UseMnemonic = false,
            Location = new Point(56, 2),
        });
        brand.Controls.Add(_brandSubtitle = new Localization.LocLabel
        {
            Text = BrandSubtitle,
            Font = Theme.Small,
            ForeColor = Admin.IsElevated ? Theme.Muted : Theme.Warn,
            AutoSize = true,
            UseMnemonic = false,
            Location = new Point(57, 26),
        });

        // Footer: settings + version.
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Theme.Sidebar, WrapContents = false };
        footer.Controls.Add(new Divider { Width = width - 24, Margin = new Padding(12, 4, 12, 4), BackColor = Theme.Border });
        footer.Controls.Add(NavItem("settings", "", "Settings", width));
        footer.Controls.Add(_versionLabel = new Localization.LocLabel
        {
            Text = $"Version {Application.ProductVersion.Split('+')[0]}",
            ForeColor = Color.FromArgb(120, 120, 120),
            Font = Theme.Small,
            AutoSize = true,
            Margin = new Padding(18, 6, 0, 0),
        });

        side.Controls.Add(list);
        side.Controls.Add(footer);
        side.Controls.Add(brand);
        return side;
    }

    private NavButton NavItem(string key, string glyph, string text, int width)
    {
        var b = new NavButton(key, glyph, text) { Width = width };
        b.Click += (_, _) => Navigate(key);
        _nav.Add(b);
        return b;
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
        if (_update is not null && !_updating) ShowUpdate(_update);
        _tray.Text = Localization.Loc.T("WinSolve - watching for Windows errors");
        Navigate(current);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tray.Dispose();
        base.Dispose(disposing);
    }
}
