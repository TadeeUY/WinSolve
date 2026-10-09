using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class SettingsPage : Page
{
    private readonly Dictionary<SystemTask, CheckBox> _tasks = [];
    private readonly Dictionary<Tweak, CheckBox> _tweaks = [];
    private readonly Label _listSummary = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly List<ColorSwatch> _swatches = [];
    private readonly FlowLayoutPanel _swatchRow = Theme.Row();
    private ColorSwatch? _customSwatch;
    private readonly CheckBox _restorePoint, _confirm, _scanOnStartup, _alerts, _tray, _animations, _autoStart, _updates, _autoUpdate;
    private readonly ComboBox _schedule = Theme.Combo(Maintenance.Schedules);
    private readonly ComboBox _language = Theme.Combo("English", "Español");
    private readonly Label _autoStartDescription = Theme.Label("Starts hidden in the notification area.", Theme.Small, Theme.Muted);
    private string _accent = AppSettings.Current.AccentColor;

    public override string Key => "settings";

    public SettingsPage() : base("Settings", "Customize how WinSolve works.")
    {
        var s = AppSettings.Current;
        _restorePoint = Theme.Toggle("", s.CreateRestorePoint);
        _confirm = Theme.Toggle("", s.ConfirmActions);
        _scanOnStartup = Theme.Toggle("", s.ScanOnStartup);
        _alerts = Theme.Toggle("", s.ErrorAlerts);
        _tray = Theme.Toggle("", s.CloseToTray);
        _animations = Theme.Toggle("", s.Animations);
        _autoStart = Theme.Toggle("", false);
        _updates = Theme.Toggle("", s.CheckForUpdates);
        _autoUpdate = Theme.Toggle("", s.AutoInstallUpdates);
        _schedule.SelectedItem = Maintenance.Schedules.Contains(s.MaintenanceSchedule) ? s.MaintenanceSchedule : "Off";
        _language.SelectedIndex = s.Language == "es" ? 1 : 0;
        _schedule.Width = _language.Width = 170;

        var general = new StackCard().Add(
            Theme.SectionHeader("\uE713", "General", "Language, safety and behavior."),
            Theme.SettingRow("Language", "English or Spanish.", _language),
            new Divider(),
            Theme.SettingRow("Create a restore point before making changes", "Lets you undo optimizations and tweaks with System Restore.", _restorePoint),
            new Divider(),
            Theme.SettingRow("Ask for confirmation before changing the system", null, _confirm),
            new Divider(),
            Theme.SettingRow("Scan the PC when WinSolve opens", null, _scanOnStartup),
            new Divider(),
            Theme.SettingRow("Minimize and restore animations", null, _animations));

        var background = new StackCard().Add(
            Theme.SectionHeader("\uE9F5", "Background", "What WinSolve does while you're not looking at it."),
            Theme.SettingRow("Alert me when Windows reports an error", "Blue screens, crashes, disk and driver problems.", _alerts),
            Theme.Row(Theme.Button("Show a test alert", (_, _) => ErrorMonitor.Instance.RaiseTest())),
            new Divider(),
            Theme.SettingRow("Keep running in the notification area when the window is closed", null, _tray),
            new Divider(),
            Theme.SettingRow("Start WinSolve with Windows", null, _autoStart, _autoStartDescription),
            new Divider(),
            Theme.SettingRow("Automatic maintenance",
                "Runs a light cleanup in the background (temporary files, error reports, Delivery Optimization cache, DNS cache, Defender definitions) at 3:00 AM, or as soon as the PC is on afterwards. Not on battery power.",
                _schedule));

        var updates = new StackCard().Add(
            Theme.SectionHeader("\uE895", "Updates", "Keep WinSolve up to date."),
            Theme.SettingRow("Check for updates automatically", "At startup and every 4 hours.", _updates),
            new Divider(),
            Theme.SettingRow("Install updates automatically (no need to click)", "Updates are verified (SHA-256) and installed without asking.", _autoUpdate),
            Theme.Row(Theme.Button("Check for updates now", async (_, _) => await CheckUpdatesNow(), glyph: "\uE895")));

        var swatches = _swatchRow;
        foreach (var hex in new[] { "#0067C0", "#4F6BED", "#8764B8", "#0099BC", "#107C10", "#CA5010", "#C42B1C", "#E3008C", "#5C5C5C" })
        {
            var sw = new ColorSwatch(hex);
            sw.Click += (_, _) => { _accent = hex; UpdatePreview(); };
            _swatches.Add(sw);
            swatches.Controls.Add(sw);
        }
        swatches.Controls.Add(Theme.Button("Custom color", (_, _) => PickColor(), glyph: "\uE790"));
        var appearance = new StackCard().Add(
            Theme.SectionHeader("\uE771", "Accent color", "Used for buttons, selections and charts."),
            swatches);

        var tasksPanel = CheckList();
        foreach (var group in TaskCatalog.All.GroupBy(t => t.Category))
        {
            tasksPanel.Controls.Add(GroupLabel(group.Key == TaskCategory.Repair ? "Repair" : group.Key.ToString()));
            foreach (var t in group) tasksPanel.Controls.Add(_tasks[t] = ListCheck(t.Title));
        }
        var tweaksPanel = CheckList();
        foreach (var group in TweakCatalog.All.GroupBy(t => t.Category))
        {
            tweaksPanel.Controls.Add(GroupLabel(group.Key));
            foreach (var t in group) tweaksPanel.Controls.Add(_tweaks[t] = ListCheck(t.Title));
        }

        var lists = new TableLayoutPanel { ColumnCount = 2, RowCount = 2, Height = 380, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 0) };
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        lists.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        lists.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lists.Controls.Add(Theme.Label("Tasks", Theme.BodyBold), 0, 0);
        lists.Controls.Add(Theme.Label("Tweaks", Theme.BodyBold), 1, 0);
        var left = Theme.InCard(tasksPanel, 10);
        var right = Theme.InCard(tweaksPanel, 10);
        left.Margin = new Padding(0, 4, 6, 0);
        right.Margin = new Padding(6, 4, 0, 0);
        lists.Controls.Add(left, 0, 1);
        lists.Controls.Add(right, 1, 1);

        var oneClick = new StackCard().Add(
            Theme.SectionHeader("\uE945", "Custom one-click list", "Used when you pick 'Custom list' on the One-click page."),
            Theme.Row(
                Theme.Button("Copy current profile", (_, _) => LoadFromPlan(), glyph: "\uE8C8"),
                Theme.Button("Recommended", (_, _) => CheckWhere(t => t.Recommended, t => t.Recommended), glyph: "\uE73A"),
                Theme.Button("Cleanup only", (_, _) => CheckWhere(t => t.Category == TaskCategory.Cleanup && !t.Slow, _ => false)),
                Theme.Button("None", (_, _) => CheckWhere(_ => false, _ => false))),
            _listSummary,
            lists);

        var about = new StackCard().Add(
            Theme.SectionHeader("\uE946", "About", $"WinSolve {Application.ProductVersion.Split('+')[0]}  ·  Logs: {Logger.LogDirectory}"),
            Theme.Row(
                Theme.Button("Create bug report", async (_, _) => await CreateBugReport(), glyph: "\uEBE8"),
                Theme.Button("Open log folder", (_, _) => { Directory.CreateDirectory(Logger.LogDirectory); ProcessRunner.ShellOpen(Logger.LogDirectory); }),
                Theme.Button("Reset settings", async (_, _) =>
                {
                    if (_saving || !Confirm("Restore the default settings?")) return;
                    _saving = true;
                    try
                    {
                        // The default schedule is Off: remove the scheduled task too, not just the setting.
                        if (AppSettings.Current.MaintenanceSchedule != "Off") await Maintenance.ApplyScheduleAsync("Off");
                        AppSettings.Reset();
                        ErrorMonitor.Instance.Apply();
                        if (!Main.Reload("settings")) Info(RestartToApply);
                    }
                    finally { _saving = false; }
                })));

        var save = Theme.Button("Save", async (_, _) => await SaveAsync(), primary: true, glyph: "\uE74E");
        var body = new Stack(scroll: true).Add(general, background, updates, appearance, oneClick, about);
        AddRow(body, fill: true);
        AddRow(Theme.Row(save));
        LoadChecks();
        UpdatePreview();
    }

    public override async void OnShown()
    {
        // Read once: re-reading on every visit would silently undo an unsaved change to the switch.
        if (_autoStartLoaded) return;
        _autoStartLoaded = true;
        _autoStart.Checked = await Task.Run(AutoStart.IsEnabled);
        _autoStart.Enabled = AutoStart.IsAllowed || _autoStart.Checked;
        _autoStartDescription.Text = AutoStart.IsAllowed ? "Starts hidden in the notification area." : "Requires an all-users install (in Program Files).";
    }

    private Task CheckUpdatesNow() => Main.CheckForUpdatesNowAsync();

    private async Task CreateBugReport()
    {
        string? zip = null;
        RunDialog.Run(this, "Creating bug report", async (log, _, ct) => zip = await BugReport.CreateAsync(log, ct));
        if (zip is not null)
        {
            ProcessRunner.ShellOpen("explorer.exe", $"/select,\"{zip}\"");
            await Task.CompletedTask;
        }
    }

    private static FlowLayoutPanel CheckList() => new()
    {
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        BackColor = Theme.Card,
    };

    private static Control GroupLabel(string text)
    {
        var l = Theme.Label(text.ToUpperInvariant(), Theme.Small, Theme.Muted);
        l.Text = Localization.Loc.T(text).ToUpperInvariant();
        l.Margin = new Padding(2, 10, 0, 2);
        return l;
    }

    private CheckBox ListCheck(string title)
    {
        var c = Theme.Check(title, false);
        c.Margin = new Padding(2, 2, 0, 2);
        c.CheckedChanged += (_, _) => UpdateSummary();
        return c;
    }

    private void UpdateSummary()
    {
        _listSummary.Text = $"{_tasks.Values.Count(c => c.Checked)} task(s) and {_tweaks.Values.Count(c => c.Checked)} tweak(s) selected.";
    }

    private void UpdatePreview()
    {
        foreach (var sw in _swatches) sw.Selected = string.Equals(sw.Hex, _accent, StringComparison.OrdinalIgnoreCase);
        if (_swatches.Any(sw => sw.Selected)) return;

        // A custom color gets its own swatch, placed before the "Custom color" button.
        if (_customSwatch is not null)
        {
            _swatchRow.Controls.Remove(_customSwatch);
            _customSwatch.Dispose();
        }
        try { _customSwatch = new ColorSwatch(_accent) { Selected = true }; }
        catch { _customSwatch = null; return; }
        _swatchRow.Controls.Add(_customSwatch);
        _swatchRow.Controls.SetChildIndex(_customSwatch, _swatches.Count);
    }

    private void PickColor()
    {
        Color current;
        try { current = ColorTranslator.FromHtml(_accent); } catch { current = Theme.Accent; }
        using var dlg = new ColorDialog { Color = current, FullOpen = true };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _accent = $"#{dlg.Color.R:X2}{dlg.Color.G:X2}{dlg.Color.B:X2}";
            UpdatePreview();
        }
    }

    private void LoadChecks()
    {
        var tasks = AppSettings.Current.OneClickTasks?.ToHashSet();
        var tweaks = AppSettings.Current.OneClickTweaks?.ToHashSet();
        CheckWhere(t => tasks?.Contains(t.Id) ?? t.Recommended, t => tweaks?.Contains(t.Id) ?? t.Recommended);
    }

    private void CheckWhere(Func<SystemTask, bool> task, Func<Tweak, bool> tweak)
    {
        foreach (var (t, c) in _tasks) c.Checked = task(t);
        foreach (var (t, c) in _tweaks) c.Checked = tweak(t);
        UpdateSummary();
    }

    private void LoadFromPlan()
    {
        var plan = OptimizationPlanner.Build(OneClickOptimizer.CurrentDevice, AppSettings.Current.Level, HardwareProfile.Detect());
        var taskIds = plan.Tasks.Select(t => t.Id).ToHashSet();
        var tweakIds = plan.Tweaks.Select(t => t.Id).ToHashSet();
        CheckWhere(t => taskIds.Contains(t.Id), t => tweakIds.Contains(t.Id));
    }

    private const string RestartToApply = "Settings saved. A task is still running, so the new color or language will apply the next time WinSolve starts.";
    private bool _saving;
    private bool _autoStartLoaded;

    private async Task SaveAsync()
    {
        if (_saving) return;
        _saving = true;
        try { await SaveCoreAsync(); }
        finally { _saving = false; }
    }

    private async Task SaveCoreAsync()
    {
        var s = AppSettings.Current;
        s.CreateRestorePoint = _restorePoint.Checked;
        s.ConfirmActions = _confirm.Checked;
        s.ScanOnStartup = _scanOnStartup.Checked;
        s.ErrorAlerts = _alerts.Checked;
        s.CloseToTray = _tray.Checked;
        s.Animations = _animations.Checked;
        s.CheckForUpdates = _updates.Checked || _autoUpdate.Checked;
        s.AutoInstallUpdates = _autoUpdate.Checked;
        var languageChanged = s.Language != (_language.SelectedIndex == 1 ? "es" : "en");
        s.Language = _language.SelectedIndex == 1 ? "es" : "en";
        var schedule = _schedule.SelectedItem as string ?? "Off";
        if (schedule != s.MaintenanceSchedule)
        {
            if (await Maintenance.ApplyScheduleAsync(schedule)) s.MaintenanceSchedule = schedule;
            else
            {
                _schedule.SelectedItem = s.MaintenanceSchedule;
                Info("Automatic maintenance is only available when WinSolve is installed for all users (in Program Files).");
            }
        }
        s.OneClickTasks = _tasks.Where(p => p.Value.Checked).Select(p => p.Key.Id).ToList();
        s.OneClickTweaks = _tweaks.Where(p => p.Value.Checked).Select(p => p.Key.Id).ToList();
        var accentChanged = !string.Equals(s.AccentColor, _accent, StringComparison.OrdinalIgnoreCase);
        s.AccentColor = _accent;
        if (!s.Save())
        {
            Info($"Settings could not be saved. Details are in the log ({Logger.LogDirectory}).");
            return;
        }

        if (_autoStart.Checked != await Task.Run(AutoStart.IsEnabled) && !await AutoStart.SetAsync(_autoStart.Checked))
            Info("Start with Windows is only available when WinSolve is installed for all users (in Program Files). " +
                 "Running it elevated at sign-in from a folder your user account can write to would let other programs gain administrator rights.");

        ErrorMonitor.Instance.Apply();

        if (accentChanged || languageChanged)
        {
            if (!Main.Reload("settings")) Info(RestartToApply);
        }
        else Info("Settings saved.");
    }
}
