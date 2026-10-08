using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class SettingsPage : Page
{
    private readonly CheckedListBox _tasks = NewList();
    private readonly CheckedListBox _tweaks = NewList();
    private readonly CheckBox _restorePoint, _confirm, _scanOnStartup, _alerts, _tray, _animations, _autoStart;
    private readonly Panel _accentPreview = new() { Size = new Size(36, 36), Margin = new Padding(0, 4, 8, 4) };
    private string _accent = AppSettings.Current.AccentColor;

    public override string Key => "settings";

    private static CheckedListBox NewList() => new()
    {
        BackColor = Theme.Card,
        ForeColor = Theme.Text,
        BorderStyle = BorderStyle.None,
        CheckOnClick = true,
        Font = Theme.Body,
        Height = 300,
        IntegralHeight = false,
    };

    public SettingsPage() : base("Settings", "Customize how WinSolve works.")
    {
        var s = AppSettings.Current;
        _restorePoint = Theme.Check("Create a restore point before making changes", s.CreateRestorePoint);
        _confirm = Theme.Check("Ask for confirmation before changing the system", s.ConfirmActions);
        _scanOnStartup = Theme.Check("Scan the PC when WinSolve opens", s.ScanOnStartup);
        _alerts = Theme.Check("Alert me when Windows reports an error (blue screens, crashes, disk, drivers)", s.ErrorAlerts);
        _tray = Theme.Check("Keep running in the notification area when the window is closed", s.CloseToTray);
        _animations = Theme.Check("Minimize and restore animations", s.Animations);
        _autoStart = Theme.Check("Start WinSolve with Windows (in the notification area)", false);

        var general = new StackCard().Add(
            Theme.Label("General", Theme.H2),
            _restorePoint, _confirm, _scanOnStartup, _alerts, _tray, _animations, _autoStart,
            Theme.Row(Theme.Button("Show a test alert", (_, _) => ErrorMonitor.Instance.RaiseTest())),
            Theme.Label("Accent color", Theme.BodyBold),
            Theme.Row(_accentPreview,
                Theme.Button("Custom color", (_, _) => PickColor()),
                Swatch("#0067C0"), Swatch("#4F6BED"), Swatch("#107C10"), Swatch("#C42B1C"), Swatch("#CA5010"), Swatch("#5C5C5C")));

        foreach (var t in TaskCatalog.All) _tasks.Items.Add(t);
        foreach (var t in TweakCatalog.All) _tweaks.Items.Add(new TweakItem(t));

        var lists = new Panel { Height = 320, BackColor = Color.Transparent };
        _tweaks.Dock = DockStyle.Fill;
        _tasks.Dock = DockStyle.Left;
        lists.Controls.Add(_tweaks);
        lists.Controls.Add(_tasks);
        lists.Resize += (_, _) => _tasks.Width = lists.Width / 2 - 6;

        var oneClick = new StackCard().Add(
            Theme.Label("Custom one-click list", Theme.H2),
            Theme.Paragraph("Used when you pick 'Custom list' on the One-click page. Left: tasks. Right: tweaks."),
            Theme.Row(
                Theme.Button("Copy current profile", (_, _) => LoadFromPlan()),
                Theme.Button("Recommended", (_, _) => CheckWhere(t => t.Recommended, t => t.Recommended)),
                Theme.Button("Cleanup only", (_, _) => CheckWhere(t => t.Category == TaskCategory.Cleanup && !t.Slow, _ => false)),
                Theme.Button("None", (_, _) => CheckWhere(_ => false, _ => false))),
            lists);

        var about = new StackCard().Add(
            Theme.Label("About", Theme.H2),
            Theme.Paragraph($"WinSolve {Application.ProductVersion.Split('+')[0]}  ·  Logs: {Logger.LogDirectory}"),
            Theme.Row(
                Theme.Button("Open log folder", (_, _) => { Directory.CreateDirectory(Logger.LogDirectory); ProcessRunner.ShellOpen(Logger.LogDirectory); }),
                Theme.Button("Reset settings", (_, _) =>
                {
                    if (!Confirm("Restore the default settings?")) return;
                    AppSettings.Reset();
                    Main.Reload("settings");
                })));

        var save = Theme.Button("Save", async (_, _) => await SaveAsync(), primary: true);
        var body = new Stack(scroll: true).Add(general, oneClick, about);
        AddRow(body, fill: true);
        AddRow(Theme.Row(save));
        LoadChecks();
        UpdatePreview();
    }

    public override async void OnShown()
    {
        _autoStart.Checked = await Task.Run(AutoStart.IsEnabled);
    }

    private sealed record TweakItem(Tweak Tweak)
    {
        public override string ToString() => $"{Tweak.Category}: {Tweak.Title}";
    }

    private Control Swatch(string hex)
    {
        var b = new Panel
        {
            Size = new Size(26, 26),
            BackColor = ColorTranslator.FromHtml(hex),
            Cursor = Cursors.Hand,
            Margin = new Padding(4, 9, 4, 4),
        };
        b.Click += (_, _) => { _accent = hex; UpdatePreview(); };
        return b;
    }

    private void UpdatePreview()
    {
        try { _accentPreview.BackColor = ColorTranslator.FromHtml(_accent); } catch { }
    }

    private void PickColor()
    {
        using var dlg = new ColorDialog { Color = _accentPreview.BackColor, FullOpen = true };
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
        for (int i = 0; i < _tasks.Items.Count; i++)
        {
            var t = (SystemTask)_tasks.Items[i];
            _tasks.SetItemChecked(i, tasks?.Contains(t.Id) ?? t.Recommended);
        }
        for (int i = 0; i < _tweaks.Items.Count; i++)
        {
            var t = ((TweakItem)_tweaks.Items[i]).Tweak;
            _tweaks.SetItemChecked(i, tweaks?.Contains(t.Id) ?? t.Recommended);
        }
    }

    private void CheckWhere(Func<SystemTask, bool> task, Func<Tweak, bool> tweak)
    {
        for (int i = 0; i < _tasks.Items.Count; i++) _tasks.SetItemChecked(i, task((SystemTask)_tasks.Items[i]));
        for (int i = 0; i < _tweaks.Items.Count; i++) _tweaks.SetItemChecked(i, tweak(((TweakItem)_tweaks.Items[i]).Tweak));
    }

    private void LoadFromPlan()
    {
        var plan = OptimizationPlanner.Build(OneClickOptimizer.CurrentDevice, AppSettings.Current.Level, HardwareProfile.Detect());
        var taskIds = plan.Tasks.Select(t => t.Id).ToHashSet();
        var tweakIds = plan.Tweaks.Select(t => t.Id).ToHashSet();
        CheckWhere(t => taskIds.Contains(t.Id), t => tweakIds.Contains(t.Id));
    }

    private async Task SaveAsync()
    {
        var s = AppSettings.Current;
        s.CreateRestorePoint = _restorePoint.Checked;
        s.ConfirmActions = _confirm.Checked;
        s.ScanOnStartup = _scanOnStartup.Checked;
        s.ErrorAlerts = _alerts.Checked;
        s.CloseToTray = _tray.Checked;
        s.Animations = _animations.Checked;
        s.OneClickTasks = _tasks.CheckedItems.Cast<SystemTask>().Select(t => t.Id).ToList();
        s.OneClickTweaks = _tweaks.CheckedItems.Cast<TweakItem>().Select(t => t.Tweak.Id).ToList();
        var accentChanged = !string.Equals(s.AccentColor, _accent, StringComparison.OrdinalIgnoreCase);
        s.AccentColor = _accent;
        s.Save();

        if (_autoStart.Checked != await Task.Run(AutoStart.IsEnabled))
            await AutoStart.SetAsync(_autoStart.Checked);

        ErrorMonitor.Instance.Apply();

        if (accentChanged) Main.Reload("settings");
        else Info("Settings saved.");
    }
}
