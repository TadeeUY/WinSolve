using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

/// <summary>
/// Tweaks page laid out like Chris Titus Tech's WinUtil: Essential and Advanced tweaks with
/// presets, Run / Undo, instant preference toggles, power plans, DNS, Windows Update policy
/// and optional Windows features.
/// </summary>
public sealed class TweaksPage : Page
{
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000, InitialDelay = 300 };
    private readonly Dictionary<string, (CheckBox Box, Label Badge)> _rows = [];
    private readonly Dictionary<string, CheckBox> _toggles = [];
    private readonly Label _selection = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly ComboBox _dns = Theme.Combo(DnsService.Providers.Select(p => p.Name).ToArray());
    private readonly Label _dnsCurrent = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly FlatBtn _wuDefault, _wuSecurity;
    private readonly List<(WindowsFeature Feature, CheckBox Toggle)> _features = [];
    private readonly Label _featureStatus = Theme.Label("", Theme.Small, Theme.Muted);
    private bool _loading;

    public override string Key => "tweaks";

    public TweaksPage() : base("Tweaks",
        "Select tweaks and run them, or use a preset. Everything except one-shot actions can be undone. Preferences apply instantly.")
    {
        _wuDefault = Theme.Button("Default settings", (_, _) => SetUpdatePolicy(security: false));
        _wuSecurity = Theme.Button("Security settings (recommended)", (_, _) => SetUpdatePolicy(security: true));
        _dns.Width = 300;

        AddRow(Theme.Row(
            Theme.Button("Standard", (_, _) => Select(TweakCatalog.StandardPreset), glyph: ""),
            Theme.Button("Minimal", (_, _) => Select(TweakCatalog.MinimalPreset)),
            Theme.Button("Clear", (_, _) => Select([])),
            Theme.Button("Run tweaks", async (_, _) => await RunSelectedAsync(undo: false), primary: true, glyph: ""),
            Theme.Button("Undo selected tweaks", async (_, _) => await RunSelectedAsync(undo: true), glyph: "")));
        AddRow(_selection);

        var columns = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));

        var left = new Stack();
        left.Add(TweakList("Essential tweaks", "Safe for every PC. 'Standard' selects the recommended set.", TweakGroup.Essential, caution: false));
        left.Add(TweakList("Advanced tweaks - caution", "Only run these if you know you need them. Read each description (hover the info icon).", TweakGroup.Advanced, caution: true));

        var right = new Stack();
        right.Add(PreferencesCard(), PowerCard(), DnsCard(), UpdatesCard(), FeaturesCard());

        left.Margin = new Padding(0, 0, 8, 0);
        columns.Controls.Add(left, 0, 0);
        columns.Controls.Add(right, 1, 0);
        left.Dock = right.Dock = DockStyle.Fill;

        AddRow(new Stack(scroll: true).Add(columns), fill: true);
        UpdateSelectionLabel();
    }

    // ───────────── Essential / Advanced lists ─────────────

    private Control TweakList(string title, string subtitle, TweakGroup group, bool caution)
    {
        var card = new StackCard();
        card.Add(Theme.Label(title, Theme.H2, caution ? Theme.Warn : Theme.Text), Theme.Paragraph(subtitle));
        foreach (var t in TweakCatalog.All.Where(t => t.Group == group))
        {
            var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 1, 0, 1) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var box = Theme.Check(t.Title, false);
            box.CheckedChanged += (_, _) => UpdateSelectionLabel();
            var badge = Theme.Label("", Theme.Small, Theme.Muted);
            badge.Anchor = AnchorStyles.Right;
            badge.Margin = new Padding(6, 4, 6, 0);
            var info = new Label { Text = "", Font = Theme.IconsSmall, ForeColor = Theme.Muted, AutoSize = true, Cursor = Cursors.Help, Margin = new Padding(0, 6, 0, 0) };
            var tip = Loc.T(t.Description) + (t.Warning is null ? "" : "\n\n" + Loc.T(t.Warning)) + (t.NeedsReboot ? "\n\n" + Loc.T("Requires a restart.") : "");
            _tips.SetToolTip(info, tip);
            _tips.SetToolTip(box, tip);

            row.Controls.Add(box, 0, 0);
            row.Controls.Add(badge, 1, 0);
            row.Controls.Add(info, 2, 0);
            card.Add(row);
            _rows[t.Id] = (box, badge);
        }
        return card;
    }

    private void Select(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        foreach (var (id, row) in _rows) row.Box.Checked = set.Contains(id);
        UpdateSelectionLabel();
    }

    private List<Tweak> Selected() => _rows.Where(r => r.Value.Box.Checked).Select(r => TweakCatalog.Find(r.Key)!).ToList();

    private void UpdateSelectionLabel()
    {
        var applied = TweakCatalog.All.Count(t => t.Group != TweakGroup.Preference && !t.IsAction && _rows.ContainsKey(t.Id) && _rows[t.Id].Badge.Tag is true);
        _selection.Text = $"{Selected().Count} selected  ·  {applied} already applied";
    }

    private void RefreshBadges()
    {
        foreach (var (id, row) in _rows)
        {
            var t = TweakCatalog.Find(id)!;
            var applied = !t.IsAction && t.SafeIsApplied();
            row.Badge.Tag = applied;
            row.Badge.Text = t.IsAction ? "Action" : applied ? "Applied" : "";
            row.Badge.ForeColor = applied ? Theme.Good : Theme.Muted;
        }
        UpdateSelectionLabel();
    }

    private async Task RunSelectedAsync(bool undo)
    {
        var selected = Selected();
        var work = undo
            ? selected.Where(t => !t.IsAction && t.SafeIsApplied()).ToList()
            : selected.Where(t => t.IsAction || !t.SafeIsApplied()).ToList();
        if (work.Count == 0)
        {
            Info(undo ? "None of the selected tweaks are applied." : "Select tweaks first, or every selected tweak is already applied.");
            return;
        }

        var warnings = undo ? [] : work.Where(t => t.Warning is not null).Select(t => $"{Loc.T(t.Title)}: {Loc.T(t.Warning)}").ToList();
        var message = (undo ? $"Undo {work.Count} tweak(s)?" : $"Run {work.Count} tweak(s)?") + "\n\n- " + string.Join("\n- ", work.Select(t => Loc.T(t.Title)))
                      + (warnings.Count > 0 ? "\n\n" + string.Join("\n", warnings) : "");
        if (!Confirm(message)) return;

        bool explorer = false, reboot = false;
        RunDialog.Run(this, undo ? "Undoing tweaks" : "Running tweaks", async (log, progress, ct) =>
        {
            if (!undo && AppSettings.Current.CreateRestorePoint)
                await TaskCatalog.CreateRestorePoint(new TaskContext(log, ct), "WinSolve - before tweaks");
            for (int i = 0; i < work.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = work[i];
                log($"> {t.Title}");
                try
                {
                    await Task.Run(() => { if (undo) t.Revert(); else t.Apply(); }, ct);
                    log($"OK  {t.Title}");
                    explorer |= t.NeedsExplorerRestart;
                    reboot |= t.NeedsReboot;
                }
                catch (Exception ex)
                {
                    log($"ERROR  {t.Title}: {ex.Message}");
                }
                progress(i + 1, work.Count);
            }
            log("Done.");
        });

        RefreshBadges();
        if (explorer && Loc.Show(this, "Restart Explorer now to see the changes?", "WinSolve", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            await TaskCatalog.ExplorerRestart(new TaskContext(Logger.Write, default));
        if (reboot) AskReboot(this);
    }

    // ───────────── Preferences (instant toggles) ─────────────

    private Control PreferencesCard()
    {
        var card = new StackCard();
        card.Add(Theme.Label("Customize preferences", Theme.H2), Theme.Paragraph("Applied as soon as you flip the switch."));
        foreach (var t in TweakCatalog.All.Where(t => t.Group == TweakGroup.Preference))
        {
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var label = Theme.Label(t.Title, Theme.Body);
            label.Anchor = AnchorStyles.Left;
            var toggle = Theme.Toggle("", false);
            toggle.Anchor = AnchorStyles.Right;
            _tips.SetToolTip(label, Loc.T(t.Description));
            toggle.CheckedChanged += async (_, _) =>
            {
                if (_loading) return;
                var on = toggle.Checked;
                try
                {
                    await Task.Run(() => { if (on) t.Apply(); else t.Revert(); });
                    Logger.Write($"Preference '{t.Title}' -> {(on ? "on" : "off")}");
                    if (t.NeedsExplorerRestart) _explorerHint.Visible = true;
                }
                catch (Exception ex)
                {
                    Info($"Could not change '{Loc.T(t.Title)}': {ex.Message}");
                }
            };
            row.Controls.Add(label, 0, 0);
            row.Controls.Add(toggle, 1, 0);
            card.Add(row);
            _toggles[t.Id] = toggle;
        }
        _explorerHint.Visible = false;
        card.Add(_explorerHint);
        return card;
    }

    private Control _explorerHint => _explorerHintRow ??= Theme.Row(
        Theme.Label("Some changes appear after restarting Explorer.", Theme.Small, Theme.Muted),
        Theme.Button("Restart Explorer", async (_, _) => await TaskCatalog.ExplorerRestart(new TaskContext(Logger.Write, default))));

    private FlowLayoutPanel? _explorerHintRow;

    // ───────────── Power plans ─────────────

    private Control PowerCard() => new StackCard().Add(
        Theme.Label("Performance plans", Theme.H2),
        Theme.Paragraph("The Ultimate Performance plan keeps the CPU at full clocks. Best on desktops; uses more power."),
        Theme.Row(
            Theme.Button("Add and activate Ultimate Performance", (_, _) => RunDialog.RunTasks(this, "Ultimate Performance", [TaskCatalog.Find("perf-ultimate-power-plan")!])),
            Theme.Button("Remove Ultimate Performance", (_, _) => RunDialog.Run(this, "Removing Ultimate Performance", (log, _, ct) => PowerPlans.RemoveUltimateAsync(log, ct)))));

    // ───────────── DNS ─────────────

    private Control DnsCard() => new StackCard().Add(
        Theme.Label("DNS", Theme.H2),
        Theme.Paragraph("Changes the DNS servers of every connected adapter. Cloudflare and Google are fast; Quad9 and the filtering options block malicious sites."),
        _dnsCurrent,
        Theme.Row(_dns, Theme.Button("Apply DNS", (_, _) =>
        {
            var provider = DnsService.Providers[Math.Max(0, _dns.SelectedIndex)];
            RunDialog.Run(this, "Changing DNS", (log, _, ct) => DnsService.ApplyAsync(provider, log, ct));
            _ = LoadDnsAsync();
        }, primary: true)));

    private async Task LoadDnsAsync()
    {
        var current = await DnsService.CurrentAsync();
        _dnsCurrent.Text = $"Current: {Loc.T(current)}";
        var index = Array.FindIndex(DnsService.Providers, p => p.Name == current);
        if (index >= 0) _dns.SelectedIndex = index;
    }

    // ───────────── Windows Update ─────────────

    private Control UpdatesCard() => new StackCard().Add(
        Theme.Label("Windows Update", Theme.H2),
        Theme.Paragraph("Security settings delay feature updates by a year and security updates by 4 days, skip drivers from Windows Update and avoid automatic restarts. You still get every security fix."),
        Theme.Row(_wuSecurity, _wuDefault));

    private void SetUpdatePolicy(bool security)
    {
        try
        {
            if (security) UpdatePolicy.ApplySecurity(); else UpdatePolicy.ApplyDefault();
        }
        catch (Exception ex)
        {
            Info("Could not change the Windows Update policy: " + ex.Message);
        }
        RefreshUpdatePolicy();
    }

    private void RefreshUpdatePolicy()
    {
        var security = UpdatePolicy.IsSecurityMode();
        _wuSecurity.Primary = security;
        _wuDefault.Primary = !security;
    }

    // ───────────── Windows features ─────────────

    private Control FeaturesCard()
    {
        var card = new StackCard();
        card.Add(Theme.Label("Windows features", Theme.H2), Theme.Paragraph("Turn optional components on or off, then select Apply. Most need a restart."));
        foreach (var f in WindowsFeatures.Catalog())
        {
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var text = new Stack().Add(Theme.Label(f.Name, Theme.Body), Theme.Label(f.Description, Theme.Small, Theme.Muted));
            text.Dock = DockStyle.Fill;
            var toggle = Theme.Toggle("", false);
            toggle.Anchor = AnchorStyles.Right;
            toggle.Enabled = false;
            row.Controls.Add(text, 0, 0);
            row.Controls.Add(toggle, 1, 0);
            card.Add(row);
            _features.Add((f, toggle));
        }
        card.Add(_featureStatus, Theme.Row(Theme.Button("Apply features", (_, _) => ApplyFeatures())));
        return card;
    }

    private async Task LoadFeaturesAsync()
    {
        _featureStatus.Text = "Reading Windows features...";
        var list = _features.Select(f => f.Feature).ToList();
        await WindowsFeatures.LoadStatesAsync(list);
        _loading = true;
        foreach (var (f, toggle) in _features)
        {
            toggle.Enabled = f.Enabled is not null;
            toggle.Checked = f.Enabled == true;
        }
        _loading = false;
        _featureStatus.Text = _features.Any(f => f.Feature.Enabled is null)
            ? "Features marked as unavailable are not supported by this Windows edition."
            : "";
    }

    private void ApplyFeatures()
    {
        var changes = _features.Where(f => f.Feature.Enabled is not null && f.Toggle.Checked != f.Feature.Enabled).ToList();
        if (changes.Count == 0)
        {
            Info("There are no changes to apply.");
            return;
        }
        RunDialog.Run(this, "Windows features", async (log, progress, ct) =>
        {
            for (int i = 0; i < changes.Count; i++)
            {
                await WindowsFeatures.SetAsync(changes[i].Feature, changes[i].Toggle.Checked, log, ct);
                progress(i + 1, changes.Count);
            }
            log("Done. Restart the PC to finish.");
        });
        _ = LoadFeaturesAsync();
        AskReboot(this);
    }

    // ───────────── Load ─────────────

    public override async void OnShown()
    {
        _loading = true;
        await Task.Run(() => { }); // keep the UI responsive while the page appears
        RefreshBadges();
        foreach (var (id, toggle) in _toggles) toggle.Checked = TweakCatalog.Find(id)!.SafeIsApplied();
        RefreshUpdatePolicy();
        _loading = false;
        _ = LoadDnsAsync();
        if (_features.All(f => f.Feature.Enabled is null)) _ = LoadFeaturesAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
