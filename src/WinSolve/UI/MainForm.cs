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
    private Label? _brandSubtitle, _versionLabel, _brandName;
    private const int SidebarWide = 248, SidebarNarrow = 60;
    private Panel? _side, _navViewport;
    private FlowLayoutPanel? _navList, _navFooter;
    private PictureBox? _logo;
    private Divider? _footerDivider;
    private NavToggle? _toggle;
    private Panel? _topBar;
    private bool _collapsed;
    private readonly List<NavHeader> _headers = [];
    private readonly ToolTip _navTip = new();
    private SidebarWheel? _wheel;

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
            ["screentime"] = () => new ScreenTimePage(),
            ["internet"] = () => new InternetPage(),
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
            Application.AddMessageFilter(_wheel = new SidebarWheel(this));
            // If WinSolve was closed during a driver install, put Windows Update's driver setting back.
            _ = Task.Run(() => DriverService.RestoreWindowsUpdateDrivers(Logger.Write));
            _ = Task.Run(Maintenance.UpgradeTaskAsync);
            if (AppSettings.Current.InstalledOn is null)
            {
                // Weekly summaries count from here.
                AppSettings.Current.InstalledOn = DateTime.Now;
                AppSettings.Current.Save();
            }
            ScreenTimeWatcher.Start();
            ErrorMonitor.Instance.AlertRaised += OnAlert;
            ErrorMonitor.Instance.Apply();
            _ = CheckForUpdateAsync();
            // Keep checking while WinSolve sits in the notification area, so a new version shows up
            // within a couple of minutes of being published; a waiting automatic update is retried.
            var updateTimer = new System.Windows.Forms.Timer { Interval = (int)UpdateService.CheckInterval.TotalMilliseconds };
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
            else if (!_startHidden) BeginInvoke(ShowWhatsNewIfUpdated);
        };
        HandleCreated += (_, _) => Theme.StyleWindow(this);
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => { if (_wheel is not null) Application.RemoveMessageFilter(_wheel); };
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
        menu.Items.Add("Screen time", null, (_, _) => { ShowFromTray(); Navigate("screentime"); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            // Exiting mid-task would kill a driver install, uninstall or optimization half-way.
            if (WorkRunning && Localization.Loc.Show(Visible ? this : null, Localization.Loc.T("A task is still running. Exiting now stops it half-way.\n\nExit anyway?"),
                    "WinSolve", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            ExitApp();
        });

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
        ActivityLog.Cleaned(ctx.FreedBytes, "quick clean");
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
        // Started hidden in the tray after an update: show what's new the first time it opens.
        if (_startHidden && !_whatsNewChecked && !AppSettings.Current.IsFirstRun) BeginInvoke(ShowWhatsNewIfUpdated);
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Elevated with another administrator's password: never stay behind in the tray, where the
        // signed-in (standard) user could keep using the administrator's rights.
        if (_exiting || e.CloseReason != CloseReason.UserClosing || !AppSettings.Current.CloseToTray || InteractiveUser.IsDifferent)
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

    /// <summary>After an update: what the new version brings, once, in the app's language.</summary>
    private bool _whatsNewChecked;

    private void ShowWhatsNewIfUpdated()
    {
        if (_whatsNewChecked) return;
        _whatsNewChecked = true;
        var s = AppSettings.Current;
        Version.TryParse(s.LastSeenVersion, out var last);
        var current = UpdateService.CurrentVersion;
        if (last is not null && last >= current) return;
        var entries = Changelog.Since(last, current);
        RememberVersion();
        if (entries.Count == 0) return;
        using var dlg = new WhatsNewDialog(entries);
        dlg.ShowDialog(this);
    }

    private static void RememberVersion()
    {
        var current = UpdateService.CurrentVersion.ToString(3);
        if (AppSettings.Current.LastSeenVersion == current) return;
        AppSettings.Current.LastSeenVersion = current;
        AppSettings.Current.Save();
    }

    private void ShowWelcome()
    {
        RememberVersion(); // a fresh install doesn't need "what's new"
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

    /// <summary>An automatic update that waited for running work; retried on the next timer tick.</summary>
    private UpdateInfo? _deferredUpdate;

    /// <summary>True while a page task, a run window or a tray quick action is working (installing would kill it).</summary>
    private bool WorkRunning => IsBusy || _quickBusy || Application.OpenForms.OfType<RunDialog>().Any(d => d.IsBusy);

    /// <summary>
    /// Any other WinSolve window is open (a tool such as the disk speed test or secure delete,
    /// a question, an editor): closing WinSolve would cut it off or lose what was typed.
    /// </summary>
    private bool OtherWindowOpen =>
        Application.OpenForms.Cast<Form>().Any(f => f != this && f.Visible && f is not Toast)
        // A message box is not in OpenForms, but it disables the window it belongs to.
        || (IsHandleCreated && Visible && !IsWindowEnabled(Handle));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    /// <summary>
    /// An automatic update never interrupts: nothing may be running or open in WinSolve, no
    /// full-screen game, video or presentation, and the person is either not looking at WinSolve
    /// (it is in the notification area) or has been away from the PC for 10 minutes.
    /// </summary>
    private bool CanUpdateInBackground(out bool hidden)
    {
        hidden = !Visible || WindowState == FormWindowState.Minimized;
        if (WorkRunning || OtherWindowOpen || UserActivity.InFullScreenOrQuiet) return false;
        return hidden || UserActivity.Idle >= TimeSpan.FromMinutes(10);
    }

    private string? _notifiedTag;

    private async Task CheckForUpdateAsync()
    {
        var update = _deferredUpdate ?? await UpdateService.CheckIfDueAsync();
        if (update is null || _updating || _queuedUpdate is not null) return;
        _deferredUpdate = null;
        var auto = AppSettings.Current.AutoInstallUpdates && !Edition.IsPortable;
        if (auto && CanUpdateInBackground(out var hidden))
        {
            await InstallUpdateAsync(update, background: hidden);
            return;
        }
        if (auto) _deferredUpdate = update; // tried again on the next timer tick
        // As soon as a version is published: the bar and one notification with "Update now".
        if (update.Tag != _notifiedTag)
        {
            _notifiedTag = update.Tag;
            ShowUpdate(update, notify: true);
        }
    }

    /// <summary>Shows the "update available" bar at the top of every page (and, with <paramref name="notify"/>, a notification).</summary>
    public void ShowUpdate(UpdateInfo update, bool notify = false)
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
        if (notify)
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
    public async Task InstallUpdateAsync(UpdateInfo update) => await InstallUpdateAsync(update, background: false);

    /// <param name="background">WinSolve is in the notification area: update silently and come back there, without opening any window.</param>
    private async Task InstallUpdateAsync(UpdateInfo update, bool background)
    {
        if (Edition.IsPortable)
        {
            ProcessRunner.ShellOpen(update.ReleaseUrl);
            return;
        }
        if (_updating) return;
        if (WorkRunning)
        {
            // Installing closes WinSolve, which would stop the running task half-way: it waits for it.
            QueueUpdate(update);
            return;
        }
        _queuedUpdate = null;
        _queueTimer?.Stop();
        _deferredUpdate = null;
        _updating = true;
        if (!background) ShowFromTray();
        _infoBar.Show($"Updating to WinSolve {update.Tag}", "Downloading...");
        _infoBar.SetProgress(0);
        try
        {
            await Task.Run(() => UpdateService.InstallAsync(update, background,
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
            if (background) _notifiedTag = null; // the next check offers it again
        }
    }

    /// <summary>"Update now" while a task runs: installed by itself as soon as nothing is running.</summary>
    private UpdateInfo? _queuedUpdate;
    private System.Windows.Forms.Timer? _queueTimer;

    private void QueueUpdate(UpdateInfo update)
    {
        if (_queuedUpdate is null) Logger.Write($"[update] {update.Tag} waits for the running task to finish.");
        _queuedUpdate = update;
        var cancel = Theme.Button("Cancel", (_, _) =>
        {
            _queuedUpdate = null;
            _queueTimer?.Stop();
            Logger.Write($"[update] Waiting update to {update.Tag} cancelled.");
            ShowUpdate(update);
        });
        _infoBar.Show($"WinSolve {update.Tag} installs when the running task finishes",
            "Keep using WinSolve: as soon as the task ends, it updates and reopens by itself.", cancel);
        if (_queueTimer is null)
        {
            _queueTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _queueTimer.Tick += async (_, _) =>
            {
                if (_queuedUpdate is not { } queued || WorkRunning) return;
                _queuedUpdate = null;
                _queueTimer.Stop();
                Logger.Write($"[update] The task finished; installing {queued.Tag}.");
                await InstallUpdateAsync(queued, background: !Visible);
            };
        }
        _queueTimer.Start();
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

    private async void FixAlert(Alert alert)
    {
        ShowFromTray();
        if (alert.FixTaskIds.Length > 0)
        {
            var tasks = alert.FixTaskIds.Select(TaskCatalog.Find).OfType<SystemTask>().ToList();
            RunDialog.RunTasks(this, alert.Title, tasks);
        }
        else if (alert.DeviceInstanceId is { } id)
        {
            // Only what a restart/reinstall can fix, and only after saying what will happen.
            // Win32_PnPEntity can take seconds: never on the UI thread.
            List<ProblemDevice> devices;
            try { devices = await Task.Run(HardwareService.GetProblemDevices); }
            catch (Exception ex)
            {
                Logger.Write($"Problem devices: {ex.Message}");
                Localization.Loc.Show(this, ex.Message, "WinSolve", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (IsDisposed) return;
            var device = devices.FirstOrDefault(d => string.Equals(d.InstanceId, id, StringComparison.OrdinalIgnoreCase));
            if (device is null)
            {
                Localization.Loc.Show(this, "This device has no error any more.", "WinSolve", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var plans = HardwareService.PlanRepairs([device]);
            if (plans.Count == 0)
            {
                Localization.Loc.Show(this, string.Format(Localization.Loc.T("WinSolve can't fix this automatically (code {0}): {1}\n\nOpen Device Manager to check the device."), device.ErrorCode, Localization.Loc.T(device.Explanation)),
                    "WinSolve", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (Localization.Loc.Show(this, Localization.Loc.T("These devices will be repaired:") + "\n\n" + Localization.Loc.T(HardwareService.DescribePlan(plans[0])) + "\n\n" + Localization.Loc.T("Continue?"),
                    "WinSolve", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            RunDialog.Run(this, "Repair device", (log, _, ct) => DriverService.RepairProblemDevicesAsync(plans, log, ct));
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
        var side = _side = new Panel { Dock = DockStyle.Left, Width = SidebarWide, BackColor = Theme.Sidebar, Padding = new Padding(4, 4, 4, 10) };

        var list = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Sidebar,
            // Scrolls when the window is short; its scroll bar sits outside the visible area (see the viewport below).
            AutoScroll = true,
        };
        _navList = list;

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
            ("screentime", "\uE823", "Screen time"),
            ("internet", "\uE774", "Internet"),
            ("tweaks", "", "Tweaks"),
            ("activation", "", "Activation"),
        ];

        var width = side.Width - side.Padding.Horizontal - 2;
        foreach (var (key, glyph, text) in items)
        {
            if (key is null)
            {
                var header = new NavHeader(text) { Width = width };
                _headers.Add(header);
                list.Controls.Add(header);
            }
            else list.Controls.Add(NavItem(key, glyph, text, width));
        }

        // Brand: logo + name.
        var brand = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Sidebar };
        brand.Controls.Add(_logo = new PictureBox
        {
            // The logo is wider than tall: a bigger box keeps it legible.
            Image = Theme.AppIcon(64).ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(42, 42),
            Location = new Point(10, 1),
            BackColor = Color.Transparent,
        });
        brand.Controls.Add(_brandName = new Label
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
        var footer = _navFooter = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.TopDown, AutoSize = true, BackColor = Theme.Sidebar, WrapContents = false };
        footer.Controls.Add(_footerDivider = new Divider { Width = width - 24, Margin = new Padding(12, 4, 12, 4), BackColor = Theme.Border });
        footer.Controls.Add(NavItem("settings", "", "Settings", width));
        footer.Controls.Add(_versionLabel = new Localization.LocLabel
        {
            Text = VersionText,
            ForeColor = Theme.Faint,
            Font = Theme.Small,
            AutoSize = true,
            Margin = new Padding(18, 6, 0, 0),
        });

        // The list is wider than the visible strip so its scroll bar is clipped away; the wheel scrolls it.
        var viewport = _navViewport = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Sidebar };
        viewport.Controls.Add(list);
        viewport.SizeChanged += (_, _) =>
            list.SetBounds(0, 0, viewport.Width + SystemInformation.VerticalScrollBarWidth, viewport.Height);

        // Top: the arrow that collapses the menu to the icons.
        var topBar = _topBar = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Theme.Sidebar };
        _toggle = new NavToggle();
        _toggle.Click += (_, _) => SetSidebarCollapsed(!_collapsed, animate: true);
        topBar.Controls.Add(_toggle);
        topBar.SizeChanged += (_, _) => PlaceToggle();

        side.Controls.Add(viewport);
        side.Controls.Add(footer);
        side.Controls.Add(brand);
        side.Controls.Add(topBar);
        SetSidebarCollapsed(AppSettings.Current.SidebarCollapsed, animate: false);
        return side;
    }

    // ───────────── Collapsing the side menu ─────────────

    private void PlaceToggle()
    {
        if (_toggle is null || _topBar is null) return;
        var x = _collapsed ? (_topBar.Width - _toggle.Width) / 2 : _topBar.Width - _toggle.Width - 6;
        _toggle.Location = new Point(Math.Max(0, x), 1);
    }

    /// <summary>Shows only the icons (collapsed) or icons and names; the choice is remembered.</summary>
    private void SetSidebarCollapsed(bool collapsed, bool animate)
    {
        if (_side is null) return;
        _collapsed = collapsed;
        var target = collapsed ? SidebarNarrow : SidebarWide;
        if (animate)
        {
            AppSettings.Current.SidebarCollapsed = collapsed;
            AppSettings.Current.Save();
        }
        if (!animate)
        {
            _side.Width = target;
            FitNav();
            ApplyCompact(collapsed);
            return;
        }
        // Names disappear at once when collapsing and come back once there is room for them.
        if (collapsed) ApplyCompact(true);
        Animator.Animate(_side, () => _side.Width, target, w =>
        {
            _side.Width = (int)Math.Round(w);
            FitNav();
            if (Math.Abs(w - target) < 0.5) ApplyCompact(collapsed);
        }, 180);
    }

    /// <summary>Gives every menu entry the width the menu has now.</summary>
    private void FitNav()
    {
        if (_side is null) return;
        var width = _side.Width - _side.Padding.Horizontal - 2;
        foreach (var b in _nav) b.Width = width;
        foreach (var h in _headers) h.Width = width;
        if (_footerDivider is not null) _footerDivider.Width = Math.Max(10, width - 24);
        if (_logo is not null) _logo.Left = _collapsed ? Math.Max(0, (_side.Width - _side.Padding.Horizontal - _logo.Width) / 2) : 10;
        PlaceToggle();
    }

    private void ApplyCompact(bool compact)
    {
        foreach (var b in _nav)
        {
            b.Compact = compact;
            // The name is not visible any more: show it when the mouse rests on the icon.
            _navTip.SetToolTip(b, compact ? Localization.Loc.T(b.Text) : "");
        }
        foreach (var h in _headers)
        {
            h.Compact = compact;
            h.Height = compact ? 20 : 26;
        }
        if (_brandName is not null) _brandName.Visible = !compact;
        if (_brandSubtitle is not null) _brandSubtitle.Visible = !compact;
        if (_versionLabel is not null) _versionLabel.Visible = !compact;
        if (_toggle is not null) _toggle.Collapsed = compact;
        FitNav();
    }

    // The wheel over the menu scrolls the menu (WinForms would send it to the page in front).
    private sealed class SidebarWheel(MainForm form) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m) => m.Msg == 0x020A && form.ScrollSidebar(m.WParam);
    }

    private bool ScrollSidebar(IntPtr wParam)
    {
        if (_navViewport is not { IsHandleCreated: true } viewport || _navList is null || ActiveForm != this) return false;
        if (!viewport.RectangleToScreen(viewport.ClientRectangle).Contains(Cursor.Position)) return false;
        var delta = (short)((long)wParam >> 16);
        _navList.AutoScrollPosition = new Point(0, Math.Max(0, -_navList.AutoScrollPosition.Y - Math.Sign(delta) * 54));
        return true;
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
                key switch
                {
                    "winupdate" => "pause resume hide history rollback driver pausar reanudar ocultar historial revertir",
                    "screentime" => "lock schedule hours usage break bloqueo horario horas uso pantalla descanso",
                    "internet" => "router wifi wi-fi password devices block gateway 192.168.1.1 modem contraseña dispositivos bloquear red conexion",
                    _ => "",
                }));
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
        items.Add(new PaletteItem("Lock the PC now", "Locks Windows; your apps stay open.", "\uE72E", "Action",
            ScreenTimeWatcher.LockNow, "bloquear lock"));
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
                if (!AppSettings.Current.ConfirmActions || Localization.Loc.Show(this, $"Run '{task.Title}'?", "WinSolve",
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

        // How long pages take to appear goes to the log (bug reports), to find slow ones.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        long createdMs = -1;
        if (!_pages.TryGetValue(key, out var page))
        {
            page = factory();
            _pages[key] = page;
            createdMs = timer.ElapsedMilliseconds;
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

        // Pages stay in the window once shown and are only hidden: taking a big page out of the
        // window (and back in) made Windows rebuild hundreds of controls, up to ~20 s per switch.
        _content.SuspendLayout();
        if (animate)
        {
            // The new page slides up into place (Windows 11 Settings style); moving an already
            // drawn window is cheap, so this stays smooth even on heavy pages.
            page.Dock = DockStyle.None;
            page.Bounds = new Rectangle(0, SlideDistance, _content.ClientSize.Width, _content.ClientSize.Height);
            page.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        }
        else page.Dock = DockStyle.Fill;
        if (page.Parent != _content) _content.Controls.Add(page);
        // Showing a page makes every control in it report "visible" to its container, and each
        // report re-measures the whole page: hundreds of passes on a big page. Freeze layout
        // while it appears and measure once at the end.
        LayoutBatch.Run(page, () =>
        {
            page.Visible = true;
            page.BringToFront();
            if (previous is not null) previous.Visible = false;
        });
        _content.ResumeLayout(true);

        _currentPage = page;
        page.OnShown();
        var shownMs = timer.ElapsedMilliseconds;
        if (animate) SlideIn(page);
        void Ready(object? s, EventArgs e)
        {
            Application.Idle -= Ready;
            var total = timer.ElapsedMilliseconds;
            if (total > 500)
                Logger.Write($"Page '{key}' was slow: {(createdMs >= 0 ? $"built in {createdMs} ms, " : "")}on screen after {total} ms (OnShown at {shownMs} ms).");
        }
        Application.Idle += Ready;
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
        // One page at a time while WinSolve is idle, so the window keeps responding: each page
        // is built and its window handles created hidden, which makes the first visit instant.
        var pending = new Queue<string>(_factories.Keys.Where(k => !_pages.ContainsKey(k) || _pages[k].Parent != _content));
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        timer.Tick += (_, _) =>
        {
            if (pending.Count == 0 || IsDisposed) { timer.Dispose(); return; }
            if (!Visible || Opacity < 1) return; // during the fade-in or while in the tray
            var key = pending.Dequeue();
            try
            {
                if (!_pages.TryGetValue(key, out var page)) _pages[key] = page = _factories[key]();
                if (page.Parent == _content) return;
                page.Visible = false;
                page.Dock = DockStyle.Fill;
                _content.Controls.Add(page);
                CreateHandles(page);
            }
            catch (Exception ex) { Logger.Write($"Preloading page '{key}' failed: {ex.Message}"); }
        };
        timer.Start();
    }

    private static void CreateHandles(Control c)
    {
        _ = c.Handle;
        foreach (Control child in c.Controls) CreateHandles(child);
    }

    private Page? CurrentPage => _currentPage is { IsDisposed: false } && _currentPage.Parent == _content ? _currentPage : null;

    /// <summary>Recreates every page (e.g. after the accent color changes).</summary>
    /// <summary>True while any page is running a task (optimization, uninstall, driver install...).</summary>
    public bool IsBusy => _pages.Values.Any(p => p.IsBusy);

    /// <summary>Returns false (and does nothing) while a task is running.</summary>
    public bool Reload(string current)
    {
        // Recreating pages would orphan running work (no log, no Cancel) and allow a second run.
        if (IsBusy) return false;
        _currentPage = null;
        // Disposing destroys each page's windows in one go (removing them first is very slow).
        _content.SuspendLayout();
        foreach (var p in _pages.Values) p.Dispose();
        _content.Controls.Clear();
        _content.ResumeLayout(false);
        _pages.Clear();
        foreach (var b in _nav) b.Invalidate();
        if (_collapsed) ApplyCompact(true);   // tooltips in the new language
        if (_brandSubtitle is not null) _brandSubtitle.Text = BrandSubtitle;
        if (_versionLabel is not null) _versionLabel.Text = VersionText;
        if (_queuedUpdate is not null) QueueUpdate(_queuedUpdate);
        else if (_update is not null && !_updating) ShowUpdate(_update);
        _tray.Text = Localization.Loc.T("WinSolve - watching for Windows errors");
        Navigate(current);
        PreloadPages();
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tray.Dispose();
        base.Dispose(disposing);
    }
}
