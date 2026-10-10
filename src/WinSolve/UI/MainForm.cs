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
    private bool _loaded, _shownOnce;
    private readonly InfoBar _infoBar = new() { Dock = DockStyle.Top, Visible = false };
    private UpdateInfo? _update;
    private bool _updating;
    private Label? _brandSubtitle, _versionLabel;

    public const string Slogan = "The multitool for Windows";

    private static string VersionText => $"Version {Application.ProductVersion.Split('+')[0]}" + (Edition.IsPortable ? " (portable)" : "");

    private static string BrandSubtitle => Admin.IsElevated ? Slogan : "Not running as administrator";

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
            ["toolbox"] = () => new ToolboxPage(),
            ["winupdate"] = () => new WindowsUpdatePage(),
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
        // Stay invisible until the first page is fully drawn (no gray flash); the splash covers the wait.
        if (!startHidden) Opacity = 0;

        Load += (_, _) =>
        {
            // With --tray, OnLoad is called by hand and again by WinForms on the first Show().
            if (_loaded) return;
            _loaded = true;
            // If WinSolve was closed during a driver install, put Windows Update's driver setting back.
            _ = Task.Run(() => DriverService.RestoreWindowsUpdateDrivers(Logger.Write));
            _ = Task.Run(Maintenance.UpgradeTaskAsync);
            ErrorMonitor.Instance.AlertRaised += OnAlert;
            ErrorMonitor.Instance.Apply();
            _ = CheckForUpdateAsync();
            // Keep checking while WinSolve sits in the notification area.
            var updateTimer = new System.Windows.Forms.Timer { Interval = (int)TimeSpan.FromHours(4).TotalMilliseconds };
            updateTimer.Tick += async (_, _) => await CheckForUpdateAsync();
            updateTimer.Start();
            if (!_startHidden)
            {
                SplashScreen.SetStatus("Preparing the interface...");
                Navigate("home");
            }
        };
        Shown += async (_, _) =>
        {
            if (_shownOnce || !Visible) return;
            _shownOnce = true;
            SplashScreen.SetStatus("Loading pages...");
            PreloadPages();
            Refresh();
            SplashScreen.SetStatus("Ready");
            await SplashScreen.CloseAsync();
            Activate();
            // When started in the tray, ShowFromTray runs the fade-in itself.
            if (!_startHidden) await AnimateInAsync();
            if (!_startHidden && AppSettings.Current.IsFirstRun) BeginInvoke(ShowWelcome);
        };
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
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Clean temporary files", null, (_, _) => QuickAction(QuickClean));
        menu.Items.Add("Free up memory", null, (_, _) => QuickAction(QuickFreeMemory));
        menu.Items.Add("Flush DNS cache", null, (_, _) => QuickAction(QuickFlushDns));
        menu.Items.Add("Restart Windows Explorer", null, (_, _) => QuickAction(QuickRestartExplorer));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("One-click optimization", null, (_, _) => { ShowFromTray(); Navigate("optimize"); });
        menu.Items.Add("Disk space", null, (_, _) => { ShowFromTray(); Navigate("space"); });
        menu.Items.Add("Toolbox", null, (_, _) => { ShowFromTray(); Navigate("toolbox"); });
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

    // ───────────── Quick actions (tray menu) ─────────────

    private bool _quickBusy;

    private async void QuickAction(Func<Task<ToastOptions>> action)
    {
        if (_quickBusy) return;
        _quickBusy = true;
        ToastOptions result;
        try
        {
            result = await action();
        }
        catch (Exception ex)
        {
            Logger.Write($"Quick action failed: {ex}");
            result = new ToastOptions { Title = "That didn't work", Detail = ex.Message, Severity = IssueSeverity.Warning, AutoCloseSeconds = 8, Sound = false };
        }
        finally { _quickBusy = false; }
        Toast.Show(result);
    }

    private static ToastOptions Done(string title, string detail, string glyph) => new()
    {
        Title = title, Detail = detail, Glyph = glyph, SecondaryText = "OK", AutoCloseSeconds = 6, Sound = false,
    };

    private static async Task<ToastOptions> QuickClean()
    {
        var ctx = new TaskContext(Logger.Write, default);
        foreach (var id in new[] { "clean-temp-user", "clean-temp-windows" })
            if (TaskCatalog.Find(id) is { } task) await Task.Run(() => task.Run(ctx));
        return Done("Temporary files cleaned",
            ctx.FreedBytes > 0 ? string.Format(Localization.Loc.T("{0} freed. Files in use were skipped."), Format.Bytes(ctx.FreedBytes))
                               : "There was nothing left to clean.", "\uE74D");
    }

    private static async Task<ToastOptions> QuickFreeMemory()
    {
        var r = await Task.Run(MemoryCleaner.PurgeStandbyList);
        return Done("Memory freed",
            string.Format(Localization.Loc.T("{0} released from the cache. {1} of {2} is now free."),
                Format.Bytes(r.Freed), Format.Bytes(r.FreeAfter), Format.Bytes(r.Total)), "\uE964");
    }

    private static async Task<ToastOptions> QuickFlushDns()
    {
        var r = await ProcessRunner.RunAsync("ipconfig.exe", "/flushdns", _ => { }, default);
        if (!r.Success) throw new InvalidOperationException(Localization.Loc.T("ipconfig couldn't flush the DNS cache."));
        return Done("DNS cache flushed", "Websites will be looked up again from scratch.", "\uE774");
    }

    private static async Task<ToastOptions> QuickRestartExplorer()
    {
        await TaskCatalog.ExplorerRestart(new TaskContext(Logger.Write, default));
        return Done("Explorer restarted", "The taskbar and desktop were reloaded.", "\uE8B7");
    }

    public async void ShowFromTray()
    {
        // Only fade in when the window was actually hidden or minimized.
        var wasHidden = !Visible || WindowState == FormWindowState.Minimized;
        if (!Visible)
        {
            Opacity = 0;
            Show();
            if (CurrentPage is null) Navigate("home");
            else CurrentPage.OnShown();
        }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        if (wasHidden) await AnimateInAsync();
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
            Toast.Show(new ToastOptions
            {
                Title = "WinSolve is still running",
                Detail = "You'll be alerted if Windows reports an error. Double-click the icon to open WinSolve.",
                Glyph = "\uE9F5",
                Open = ShowFromTray,
                SecondaryText = "OK",
                AutoCloseSeconds = 8,
                Sound = false,
            });
        }
    }

    private void ShowWelcome()
    {
        var language = AppSettings.Current.Language;
        using var dlg = new WelcomeDialog();
        var ok = dlg.ShowDialog(this) == DialogResult.OK;
        if (ok && dlg.NeedsRestart) RestartApp();
        else if (AppSettings.Current.Language != language || !ok) Reload(CurrentPage?.Key ?? "home");
    }

    /// <summary>Starts a new WinSolve (it waits for this one to close) and exits.</summary>
    public void RestartApp()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath ?? Application.ExecutablePath, "--restarted")
            {
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Logger.Write($"Restart failed: {ex.Message}");
            return;
        }
        ExitApp();
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
        if (AppSettings.Current.AutoInstallUpdates && !Edition.IsPortable) await InstallUpdateAsync(update);
        else ShowUpdate(update);
    }

    /// <summary>Shows the "update available" bar at the top of every page.</summary>
    public void ShowUpdate(UpdateInfo update)
    {
        _update = update;
        var notes = Theme.Button("What's new", (_, _) => ProcessRunner.ShellOpen(update.ReleaseUrl));
        if (Edition.IsPortable)
        {
            // Nothing to install: the user replaces the .exe on the USB stick.
            var download = Theme.Button("Download the new version", (_, _) => ProcessRunner.ShellOpen(update.ReleaseUrl), primary: true, glyph: "\uE896");
            _infoBar.Show($"WinSolve {update.Tag} is available",
                $"You have {UpdateService.CurrentVersion}. Download WinSolve-Portable.exe and replace this file.", download);
            return;
        }
        var install = Theme.Button("Update now", async (_, _) => await InstallUpdateAsync(update), primary: true, glyph: "\uE896");
        _infoBar.Show($"WinSolve {update.Tag} is available",
            $"You have {UpdateService.CurrentVersion}. Updating takes less than a minute and keeps your settings.", install, notes);
        if (!Visible)
            Toast.Show(new ToastOptions
            {
                Title = "WinSolve update available",
                Detail = $"Version {update.Tag} is ready to install.",
                Glyph = "\uE896",
                PrimaryText = "Update now",
                Primary = () => _ = InstallUpdateAsync(update),
                SecondaryText = "Later",
                Open = ShowFromTray,
                AutoCloseSeconds = 20,
                Sound = false,
            });
    }

    /// <summary>
    /// One-click update: downloads the new installer (SHA-256 verified), runs it silently for
    /// the same install scope and reopens WinSolve. No dialogs.
    /// </summary>
    public async Task InstallUpdateAsync(UpdateInfo update)
    {
        if (Edition.IsPortable)
        {
            ProcessRunner.ShellOpen(update.ReleaseUrl);
            return;
        }
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

    private void OnAlert(Alert alert) => AlertWindow.Show(alert, FixAlert, ShowFromTray);

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
        if (!AppSettings.Current.Animations || _animating) return;
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
        // Overlapping fades would read Top mid-slide and leave the window lower each time.
        if (_animating) return;
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
            ("toolbox", "\uEC7A", "Toolbox"),
            ("winupdate", "\uE895", "Windows Update"),
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
            Text = VersionText,
            ForeColor = Theme.Faint,
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

    // ───────────── Command palette (Ctrl+K) ─────────────

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData is (Keys.Control | Keys.K) or (Keys.Control | Keys.F))
        {
            OpenPalette();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public void OpenPalette() => CommandPalette.ShowFor(this, PaletteItems());

    /// <summary>Everything the palette can find: pages, tasks, tweaks and tools.</summary>
    private List<PaletteItem> PaletteItems()
    {
        var items = new List<PaletteItem>();
        foreach (var b in _nav)
        {
            var key = b.Key;
            items.Add(new PaletteItem(b.Text, "Open this page", Page.PageGlyphs.GetValueOrDefault(key, b.Glyph), "Page", () => Navigate(key),
                key == "winupdate" ? "pause resume hide history rollback driver pausar reanudar ocultar historial revertir" : ""));
        }
        items.Add(new PaletteItem("Pause Windows Update for a week", "Nothing is downloaded or installed for 7 days.", "\uE769", "Action", () =>
        {
            try
            {
                WindowsUpdateService.Pause(7);
                Toast.Show(new ToastOptions { Title = "Windows Update paused", Detail = "Updates resume by themselves in 7 days.", Glyph = "\uE769", SecondaryText = "OK", AutoCloseSeconds = 6, Sound = false });
            }
            catch (Exception ex) { Localization.Loc.Show(this, ex.Message, "WinSolve", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            if (CurrentPage?.Key == "winupdate") CurrentPage.OnShown();
        }, "pausar actualizaciones"));
        items.Add(new PaletteItem("Free up memory", "Releases the standby cache so more RAM shows as free.", "\uE964", "Action",
            () => QuickAction(QuickFreeMemory), "ram liberar"));
        foreach (var t in TaskCatalog.All)
        {
            var task = t;
            items.Add(new PaletteItem(t.Title, t.Description, t.Category switch
            {
                TaskCategory.Cleanup => "\uE74D", TaskCategory.Repair => "\uE90F", TaskCategory.Network => "\uE968",
                TaskCategory.Security => "\uE83D", _ => "\uE945",
            }, "Task", () =>
            {
                if (!AppSettings.Current.ConfirmActions || Localization.Loc.Show(this, $"Run '{Localization.Loc.T(task.Title)}'?", "WinSolve",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    RunDialog.RunTasks(this, task.Title, [task]);
            }, t.Category.ToString()));
        }
        foreach (var t in TweakCatalog.All)
        {
            var id = t.Id;
            items.Add(new PaletteItem(t.Title, t.Description, "\uE9E9", "Tweak", () =>
            {
                Navigate("tweaks");
                (CurrentPage as Pages.TweaksPage)?.Reveal(id);
            }, t.Category));
        }
        foreach (var tool in Toolbox.All)
        {
            var open = tool.Open;
            items.Add(new PaletteItem(tool.Title, tool.Description, tool.Glyph, "Tool", () => open(this), tool.Keywords));
        }
        return items;
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

        var previous = CurrentPage;
        if (previous == page)
        {
            page.OnShown();
            return;
        }
        previous?.OnHidden();
        foreach (var b in _nav) b.Selected = b.Key == key;

        var animate = AppSettings.Current.Animations && Visible && Opacity > 0 && previous is not null && WindowState != FormWindowState.Minimized;

        _content.SuspendLayout();
        foreach (var old in _content.Controls.OfType<Page>().ToList()) _content.Controls.Remove(old);
        if (animate)
        {
            // The new page slides up into place (Windows 11 Settings style); moving an already
            // drawn window is cheap, so this stays smooth even on heavy pages.
            page.Dock = DockStyle.None;
            page.Bounds = new Rectangle(0, SlideDistance, _content.ClientSize.Width, _content.ClientSize.Height);
            page.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        }
        else page.Dock = DockStyle.Fill;
        _content.Controls.Add(page);
        _content.ResumeLayout(true);

        _currentPage = page;
        page.OnShown();
        if (animate) SlideIn(page);
    }

    private const int SlideDistance = 28;
    private Page? _currentPage;

    private void SlideIn(Page page)
    {
        page.Update();
        double offset = SlideDistance;
        Animator.Animate((this, "page"), () => offset, 0, v =>
        {
            offset = v;
            if (page.IsDisposed || page.Parent != _content) return;
            if (v <= 0.5)
            {
                page.Dock = DockStyle.Fill;
                return;
            }
            page.Top = (int)Math.Round(v);
            page.Update();
        }, 240);
    }

    /// <summary>Builds the other pages up front (while the splash is visible) so switching is instant.</summary>
    private void PreloadPages()
    {
        foreach (var (key, factory) in _factories)
        {
            if (_pages.ContainsKey(key)) continue;
            try { _pages[key] = factory(); }
            catch (Exception ex) { Logger.Write($"Preloading page '{key}' failed: {ex.Message}"); }
        }
    }

    private Page? CurrentPage => _currentPage is not null && _content.Controls.Contains(_currentPage) ? _currentPage : null;

    /// <summary>Recreates every page (e.g. after the accent color changes).</summary>
    /// <summary>True while any page is running a task (optimization, uninstall, driver install...).</summary>
    public bool IsBusy => _pages.Values.Any(p => p.IsBusy);

    /// <summary>Returns false (and does nothing) while a task is running.</summary>
    public bool Reload(string current)
    {
        // Recreating pages would orphan running work (no log, no Cancel) and allow a second run.
        if (IsBusy) return false;
        _currentPage = null;
        _content.Controls.Clear();
        foreach (var p in _pages.Values) p.Dispose();
        _pages.Clear();
        foreach (var b in _nav) b.Invalidate();
        if (_brandSubtitle is not null) _brandSubtitle.Text = BrandSubtitle;
        if (_versionLabel is not null) _versionLabel.Text = VersionText;
        if (_update is not null && !_updating) ShowUpdate(_update);
        _tray.Text = Localization.Loc.T("WinSolve - watching for Windows errors");
        Navigate(current);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tray.Dispose();
        base.Dispose(disposing);
    }
}
