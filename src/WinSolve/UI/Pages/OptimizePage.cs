using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class OptimizePage : Page
{
    private readonly TaskRunnerView _runner = new();
    private readonly Label _hardware = Theme.Label("", Theme.Body, Theme.Muted);
    private readonly Label _plan = Theme.Paragraph("", Theme.Text);
    private readonly Label _levelHelp = Theme.Paragraph("");
    private readonly FlatBtn _start;
    private readonly FlatBtn _desktop, _laptop, _custom;
    private readonly FlatBtn[] _levels;
    private readonly StackCard _results = new() { Visible = false };

    public override string Key => "optimize";

    public OptimizePage() : base("One-click optimization",
        "Choose the device type and level. WinSolve tailors the changes to the hardware it detects.")
    {
        _start = Theme.Button("Start optimization", async (_, _) => await StartAsync(), primary: true);
        _runner.BusyChanged += busy => _start.Enabled = !busy;

        _desktop = Theme.Button("Desktop PC", (_, _) => SetDevice(DeviceKind.PC));
        _laptop = Theme.Button("Laptop", (_, _) => SetDevice(DeviceKind.Laptop));
        _custom = Theme.Button("Custom list", (_, _) =>
        {
            AppSettings.Current.UseCustomOneClick = true;
            AppSettings.Current.Save();
            OnShown();
        });
        _levels =
        [
            Theme.Button("Light", (_, _) => SetLevel(OptimizationLevel.Light)),
            Theme.Button("Balanced", (_, _) => SetLevel(OptimizationLevel.Balanced)),
            Theme.Button("Maximum performance", (_, _) => SetLevel(OptimizationLevel.Maximum)),
        ];

        var card = new StackCard().Add(
            _hardware,
            new Divider(),
            Theme.Label("Device type", Theme.BodyBold),
            Theme.Row(_desktop, _laptop),
            Theme.Label("Optimization level", Theme.BodyBold),
            Theme.Row([.. _levels, _custom]),
            _levelHelp,
            new Divider(),
            Theme.Label("Planned changes", Theme.BodyBold),
            _plan,
            Theme.Row(_start, Theme.Button("Edit custom list", (_, _) => Main.Navigate("settings"))));

        AddRow(new Stack(scroll: true).Add(_results, card), fill: true);
        AddRow(_runner, height: 240);
    }

    private void SetDevice(DeviceKind kind)
    {
        AppSettings.Current.Device = kind;
        AppSettings.Current.UseCustomOneClick = false;
        AppSettings.Current.Save();
        OnShown();
    }

    private void SetLevel(OptimizationLevel level)
    {
        AppSettings.Current.Level = level;
        AppSettings.Current.UseCustomOneClick = false;
        AppSettings.Current.Save();
        OnShown();
    }

    public override async void OnShown()
    {
        _plan.Text = "Detecting hardware...";
        var hw = await Task.Run(HardwareProfile.Detect);
        var custom = AppSettings.Current.UseCustomOneClick;
        var device = OneClickOptimizer.CurrentDevice;
        var level = AppSettings.Current.Level;

        _hardware.Text = "Detected: " + hw.Describe();
        _desktop.Primary = !custom && device == DeviceKind.PC;
        _laptop.Primary = !custom && device == DeviceKind.Laptop;
        _desktop.Text = hw.IsLaptop ? "Desktop PC" : "Desktop PC (detected)";
        _laptop.Text = hw.IsLaptop ? "Laptop (detected)" : "Laptop";
        for (int i = 0; i < _levels.Length; i++) _levels[i].Primary = !custom && (int)level == i;
        _custom.Primary = custom;

        _levelHelp.Text = custom
            ? "Runs the tasks and tweaks you picked in Settings."
            : level switch
            {
                OptimizationLevel.Light => "Light: basic cleanup and privacy settings. Power settings are not touched.",
                OptimizationLevel.Balanced => "Balanced: full cleanup, TRIM or defragmentation, and the recommended tweaks.",
                _ => "Maximum performance: everything in Balanced plus the highest power plan, no background recording, and GPU and network tuning.",
            };

        var tasks = await Task.Run(() => OneClickOptimizer.SelectedTasks().ToList());
        var tweaks = await Task.Run(() => OneClickOptimizer.SelectedTweaks().ToList());
        var pendingTweaks = tweaks.Where(t => !t.SafeIsApplied()).ToList();

        var lines = new List<string>();
        if (!custom) lines.AddRange(OneClickOptimizer.CurrentPlan().Reasons.Select(r => "• " + r));
        if (AppSettings.Current.CreateRestorePoint) lines.Add("• A System Restore point is created first, so every change can be undone.");
        lines.Add($"• {pendingTweaks.Count} tweak(s) to apply ({tweaks.Count - pendingTweaks.Count} already applied)" +
                  (pendingTweaks.Count > 0 ? ": " + string.Join(", ", pendingTweaks.Select(t => Localization.Loc.T(t.Title)).Take(6)) + (pendingTweaks.Count > 6 ? ", ..." : "") : ""));
        lines.Add($"• {tasks.Count} task(s): " + string.Join(", ", tasks.Select(t => Localization.Loc.T(t.Title))));
        _plan.Text = string.Join("\n", lines);

        RenderResults(await Task.Run(OptimizationHistory.Latest));
    }

    /// <summary>Before / after table of the most recent optimization.</summary>
    private void RenderResults(OptimizationRun? run)
    {
        _results.Body.Controls.Clear();
        if (run?.After is not { } after)
        {
            _results.Visible = false;
            return;
        }
        var before = run.Before;
        _results.Visible = true;
        _results.Add(Theme.Label($"Last optimization  ·  {run.Time:g}  ·  {run.Profile}", Theme.H2));

        var table = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 4) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
        for (int i = 0; i < 3; i++) table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));

        void Header(string text) => table.Controls.Add(Theme.Label(text, Theme.Small, Theme.Muted));
        Header(""); Header("Before"); Header("After"); Header("Change");

        void Row(string name, string b, string a, string change, bool better)
        {
            table.Controls.Add(Theme.Label(name, Theme.Body, Theme.Muted));
            table.Controls.Add(Theme.Label(b, Theme.Body));
            table.Controls.Add(Theme.Label(a, Theme.BodyBold));
            table.Controls.Add(Theme.Label(change, Theme.Body, change == "—" ? Theme.Muted : better ? Theme.Good : Theme.Warn));
        }

        static string Signed(long v) => (v >= 0 ? "+" : "-") + Format.Bytes(Math.Abs(v));
        static string SignedInt(int v) => v == 0 ? "—" : (v > 0 ? "+" : "") + v;

        var freeDelta = after.SystemDriveFree - before.SystemDriveFree;
        Row("Free space on system drive", Format.Bytes(before.SystemDriveFree), Format.Bytes(after.SystemDriveFree), Signed(freeDelta), freeDelta >= 0);
        var ramDelta = after.RamUsed - before.RamUsed;
        Row("Memory in use", Format.Bytes(before.RamUsed), Format.Bytes(after.RamUsed), Signed(ramDelta), ramDelta <= 0);
        Row("Startup apps enabled", $"{before.StartupApps}", $"{after.StartupApps}", SignedInt(after.StartupApps - before.StartupApps), after.StartupApps <= before.StartupApps);
        Row("Running processes", $"{before.Processes}", $"{after.Processes}", SignedInt(after.Processes - before.Processes), after.Processes <= before.Processes);
        Row("Running services", $"{before.RunningServices}", $"{after.RunningServices}", SignedInt(after.RunningServices - before.RunningServices), after.RunningServices <= before.RunningServices);
        Row("Tweaks on", $"{before.TweaksOn}", $"{after.TweaksOn}", SignedInt(after.TweaksOn - before.TweaksOn), true);

        var bootBefore = before.BootSeconds is { } bb ? $"{bb:0.0} s" : "—";
        if (run.BootSecondsAfter is { } ba)
            Row("Windows boot time", bootBefore, $"{ba:0.0} s",
                before.BootSeconds is { } b0 ? $"{ba - b0:+0.0;-0.0} s" : "—", before.BootSeconds is null || ba <= before.BootSeconds);
        else
            Row("Windows boot time", bootBefore, "after restart", "—", true);

        _results.Add(table);
        _results.Add(Theme.Paragraph("Memory and process counts vary with what you have open; boot time is measured by Windows on the next restart."));
    }

    private async Task StartAsync()
    {
        if (!AppSettings.Current.UseCustomOneClick && AppSettings.Current.Level == OptimizationLevel.Maximum
            && OneClickOptimizer.CurrentDevice == DeviceKind.Laptop
            && !Confirm("Maximum performance on a laptop increases power draw and heat and shortens battery life. Continue?"))
            return;
        if (!Confirm("Start the optimization? You can keep using the PC while it runs.")) return;

        _runner.Clear();
        OptimizeSummary? summary = null;
        await _runner.RunAsync("Optimizing", async (log, progress, ct) =>
        {
            summary = await OneClickOptimizer.RunAsync(log, progress, ct);
        });

        if (summary is { } s)
        {
            _runner.Log($"Done. {s.TasksOk} task(s) completed, {s.TweaksApplied} tweak(s) applied, {Format.Bytes(s.FreedBytes)} freed."
                        + (s.TasksFailed > 0 ? $" {s.TasksFailed} task(s) reported errors." : ""));
            if (s.RebootRecommended) AskReboot(this);
        }
        OnShown();
    }
}
