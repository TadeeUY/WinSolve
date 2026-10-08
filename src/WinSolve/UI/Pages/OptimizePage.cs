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

        AddRow(new Stack(scroll: true).Add(card), fill: true);
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
                  (pendingTweaks.Count > 0 ? ": " + string.Join(", ", pendingTweaks.Select(t => t.Title).Take(6)) + (pendingTweaks.Count > 6 ? ", ..." : "") : ""));
        lines.Add($"• {tasks.Count} task(s): " + string.Join(", ", tasks.Select(t => t.Title)));
        _plan.Text = string.Join("\n", lines);
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
