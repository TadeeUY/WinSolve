using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

/// <summary>
/// Tweaks page laid out like Chris Titus Tech's WinUtil: Essential and Advanced tweaks with
/// Minimal / Standard / Maximum levels, Save (with confirmation) / Undo, preference toggles, power plans, DNS, Windows Update policy
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
    private readonly Segmented _presets = new();
    private readonly FlatBtn _save;
    // Preference switches flipped by the user, waiting for Save (id -> wanted state).
    private readonly Dictionary<string, bool> _pending = [];
    private Dictionary<string, bool> _states = [];
    private bool _loading, _selecting;

    public override string Key => "tweaks";

    public TweaksPage() : base("Tweaks",
        "Pick a level or choose tweaks yourself, then select Save. Nothing changes until you confirm, and everything except one-shot actions can be undone.")
    {
        _wuDefault = Theme.Button("Default settings", (_, _) => SetUpdatePolicy(security: false));
        _wuSecurity = Theme.Button("Security settings (recommended)", (_, _) => SetUpdatePolicy(security: true));
        _dns.Width = 300;

        _presets.Add("min", "Minimal").Add("std", "Standard").Add("max", "Maximum");
        _presets.Margin = new Padding(0, 4, 2, 4); // line up with the buttons next to it
        _presets.SelectedChanged += key => Select(key switch
        {
            "min" => TweakCatalog.MinimalPreset,
            "max" => TweakCatalog.MaximumPreset,
            _ => TweakCatalog.StandardPreset,
        }, fromPreset: true);
        _save = Theme.Button("Save", async (_, _) => await SaveAsync(), primary: true, glyph: "\uE74E");
        AddRow(Theme.Row(
            _presets,
            Theme.Button("Clear", (_, _) => Select([])),
            _save,
            Theme.Button("Undo selected tweaks", async (_, _) => await RunSelectedAsync(undo: true), glyph: "\uE7A7")));
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
        // "Standard" is the default level every time the page is opened.
        _presets.Select("std");
    }

    // ───────────── Essential / Advanced lists ─────────────

    private Control TweakList(string title, string subtitle, TweakGroup group, bool caution)
    {
        var card = new StackCard();
        card.Add(Theme.Label(title, Theme.H2, caution ? Theme.Warn : Theme.Text), Theme.Paragraph(subtitle));
        foreach (var t in TweakCatalog.All.Where(t => t.Group == group && t.SafeIsAvailable()))
        {
            var row = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 1, 0, 1) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var box = Theme.Check(t.Title, false);
            box.CheckedChanged += (_, _) =>
            {
                // A manual change means the list no longer matches a level.
                if (!_selecting) _presets.ClearSelection();
                UpdateSelectionLabel();
            };
            var badge = Theme.Label("", Theme.Small, Theme.Muted);
            badge.Anchor = AnchorStyles.Right;
            badge.Margin = new Padding(6, 4, 6, 0);
            var info = new Label { Text = "", Font = Theme.IconsSmall, ForeColor = Theme.Muted, AutoSize = true, Cursor = Cursors.Help, Margin = new Padding(0, 6, 0, 0) };
            var tip = Loc.T(t.Description) + (t.Warning is null ? "" : "\n\n" + Loc.T(t.Warning)) + (t.NeedsReboot ? "\n\n" + Loc.T("Requires a restart.") : "")
                      + (t.AllUsers ? "\n\n" + Loc.T("Applies to all users of this PC.") : "");
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

    private void Select(IEnumerable<string> ids, bool fromPreset = false)
    {
        var set = ids.ToHashSet();
        _selecting = true;
        foreach (var (id, row) in _rows)
            if (row.Box.Checked != set.Contains(id)) row.Box.Checked = set.Contains(id);
        _selecting = false;
        if (!fromPreset) _presets.ClearSelection();
        UpdateSelectionLabel();
    }

    /// <summary>Scrolls to a tweak and selects it (from the command palette).</summary>
    public void Reveal(string id)
    {
        Control? target = _rows.TryGetValue(id, out var row) ? row.Box : _toggles.GetValueOrDefault(id);
        if (target is null) return;
        if (_rows.ContainsKey(id)) row.Box.Checked = true;
        for (var p = target.Parent; p is not null; p = p.Parent)
        {
            if (p is ScrollableControl { AutoScroll: true } scroll)
            {
                scroll.ScrollControlIntoView(target);
                break;
            }
        }
        target.Focus();
    }

    private List<Tweak> Selected() => _rows.Where(r => r.Value.Box.Checked).Select(r => TweakCatalog.Find(r.Key)!).ToList();

    private void UpdateSelectionLabel()
    {
        var applied = TweakCatalog.All.Count(t => t.Group != TweakGroup.Preference && !t.IsAction && _rows.ContainsKey(t.Id) && _rows[t.Id].Badge.Tag is true);
        var changes = Selected().Count(t => t.IsAction || _rows[t.Id].Badge.Tag is not true) + _pending.Count;
        _selection.Text = $"{Selected().Count} selected  ·  {applied} already applied  ·  {changes} change(s) to save";
    }

    private void RefreshBadges(IReadOnlyDictionary<string, bool>? states = null)
    {
        foreach (var (id, row) in _rows)
        {
            var t = TweakCatalog.Find(id)!;
            var applied = !t.IsAction && (states?.GetValueOrDefault(id) ?? t.SafeIsApplied());
            row.Badge.Tag = applied;
            // Only touch what changed: each text change re-measures the page.
            var text = Loc.T(t.IsAction ? "Action" : applied ? "Applied" : "");
            if (row.Badge.Text != text) row.Badge.Text = text;
            var color = applied ? Theme.Good : Theme.Muted;
            if (row.Badge.ForeColor != color) row.Badge.ForeColor = color;
        }
        UpdateSelectionLabel();
    }

    /// <summary>Applies the selected tweaks and the changed preferences after one confirmation.</summary>
    private async Task SaveAsync()
    {
        var work = Selected().Where(t => t.IsAction || !t.SafeIsApplied())
            .Select(t => (Tweak: t, On: true))
            .Concat(_pending.Select(p => (Tweak: TweakCatalog.Find(p.Key)!, On: p.Value)))
            .ToList();
        if (work.Count == 0)
        {
            Info("Nothing to save: the selected tweaks are already applied.");
            return;
        }

        string Line(Tweak t, bool on) => t.Group == TweakGroup.Preference
            ? $"{Loc.T(t.Title)}: {Loc.T(on ? "on" : "off")}"
            : Loc.T(t.Title);
        var warnings = work.Where(w => w.On && w.Tweak.Warning is not null).Select(w => $"{Loc.T(w.Tweak.Title)}: {Loc.T(w.Tweak.Warning)}").ToList();
        var policyWarning = await Task.Run(() => ManagedPc.PolicyWarning(work.Select(w => w.Tweak).ToList()));
        var message = Loc.T($"Are you sure you want to apply these {work.Count} change(s)?")
                      + "\n\n- " + string.Join("\n- ", work.Select(w => Line(w.Tweak, w.On)))
                      + (warnings.Count > 0 ? "\n\n" + string.Join("\n", warnings) : "")
                      + (policyWarning.Length > 0 ? "\n\n" + policyWarning : "")
                      + (AppSettings.Current.CreateRestorePoint ? "\n\n" + Loc.T("A restore point is created first, so you can go back.") : "");
        // Always asked, even with "confirm actions" turned off: this is the Save step itself.
        if (Loc.Show(this, message, "Save tweaks", MessageBoxButtons.YesNo, policyWarning.Length > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) return;

        bool explorer = false, reboot = false, changed = false;
        string? protectionDrive = null;
        RunDialog.Run(this, "Saving tweaks", async (log, progress, ct) =>
        {
            if (AppSettings.Current.CreateRestorePoint)
                await RestorePointOrAskAsync(log, ct, "WinSolve - before tweaks", drive => protectionDrive = drive);
            for (int i = 0; i < work.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (t, on) = work[i];
                log($"> {t.Title}");
                try
                {
                    await Task.Run(() => { if (on) t.Apply(); else t.Revert(); }, ct);
                    log($"OK  {t.Title}");
                    changed = true;
                    explorer |= t.NeedsExplorerRestart;
                    reboot |= t.NeedsReboot;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log($"ERROR  {t.Title}: {ex.Message}");
                }
                progress(i + 1, work.Count);
            }
            if (explorer) TaskCatalog.RefreshShell(new TaskContext(log, ct));
            log("Done.");
        });

        if (protectionDrive is not null) Info(TaskCatalog.ProtectionTurnedOnMessage(protectionDrive));
        // Stopped before anything changed (e.g. no restore point): keep the switches the user flipped.
        await RefreshStatesAsync(resetToggles: changed);
        if (explorer && Loc.Show(this, "Restart Explorer now to see the changes?", "WinSolve", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            await TaskCatalog.ExplorerRestart(new TaskContext(Logger.Write, default));
        if (reboot) AskReboot(this);
    }

    /// <summary>
    /// Runs inside RunDialog's worker: creates the restore point and, if that fails, asks whether to
    /// go on without one (default No; answering No cancels before anything changes).
    /// </summary>
    private async Task RestorePointOrAskAsync(Action<string> log, CancellationToken ct, string description, Action<string> protectionTurnedOn)
    {
        var r = await TaskCatalog.CreateRestorePointAsync(new TaskContext(log, ct), description);
        if (r.ProtectionTurnedOn) protectionTurnedOn(r.Drive);
        if (r.Created) return;
        if (!AskFromWorker("Could not create a restore point. Continue without it?"))
        {
            log("Stopped: nothing was changed because no restore point could be created.");
            throw new OperationCanceledException();
        }
        log("Continuing without a restore point.");
    }

    /// <summary>Asks a Yes/No question (default No) from a background thread, on the UI thread over the open RunDialog.</summary>
    private bool AskFromWorker(string message) => !IsDisposed && (bool)Invoke(() =>
    {
        IWin32Window owner = Application.OpenForms.OfType<RunDialog>().LastOrDefault() ?? (IWin32Window)this;
        return Loc.Show(owner, message, "WinSolve", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    });

    private async Task RunSelectedAsync(bool undo)
    {
        var selected = Selected();
        var work = undo
            ? selected.Where(t => !t.IsAction && t.SafeCanUndo()).ToList()
            : selected.Where(t => t.IsAction || !t.SafeIsApplied()).ToList();
        if (work.Count == 0)
        {
            Info(undo ? "None of the selected tweaks are applied." : "Select tweaks first, or every selected tweak is already applied.");
            return;
        }

        var warnings = undo ? [] : work.Where(t => t.Warning is not null).Select(t => $"{Loc.T(t.Title)}: {Loc.T(t.Warning)}").ToList();
        var policyWarning = await Task.Run(() => ManagedPc.PolicyWarning(work));
        var message = Loc.T(undo ? $"Undo {work.Count} tweak(s)?" : $"Run {work.Count} tweak(s)?") + "\n\n- " + string.Join("\n- ", work.Select(t => Loc.T(t.Title)))
                      + (warnings.Count > 0 ? "\n\n" + string.Join("\n", warnings) : "")
                      + (policyWarning.Length > 0 ? "\n\n" + policyWarning : "");
        if (policyWarning.Length > 0 ? !ConfirmDanger(message) : !Confirm(message)) return;

        bool explorer = false, reboot = false;
        string? protectionDrive = null;
        RunDialog.Run(this, undo ? "Undoing tweaks" : "Running tweaks", async (log, progress, ct) =>
        {
            if (!undo && AppSettings.Current.CreateRestorePoint)
                await RestorePointOrAskAsync(log, ct, "WinSolve - before tweaks", drive => protectionDrive = drive);
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
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log($"ERROR  {t.Title}: {ex.Message}");
                }
                progress(i + 1, work.Count);
            }
            if (explorer) TaskCatalog.RefreshShell(new TaskContext(log, ct));
            log("Done.");
        });

        if (protectionDrive is not null) Info(TaskCatalog.ProtectionTurnedOnMessage(protectionDrive));
        RefreshBadges();
        if (explorer && Loc.Show(this, "Restart Explorer now to see the changes?", "WinSolve", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            await TaskCatalog.ExplorerRestart(new TaskContext(Logger.Write, default));
        if (reboot) AskReboot(this);
    }

    // ───────────── Preferences (applied on Save) ─────────────

    private Control PreferencesCard()
    {
        var card = new StackCard();
        card.Add(Theme.Label("Customize preferences", Theme.H2), Theme.Paragraph("Applied when you select Save."));
        foreach (var t in TweakCatalog.All.Where(t => t.Group == TweakGroup.Preference && t.SafeIsAvailable()))
        {
            var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var label = Theme.Label(t.Title, Theme.Body);
            label.Anchor = AnchorStyles.Left;
            var toggle = Theme.Toggle("", false);
            toggle.Anchor = AnchorStyles.Right;
            _tips.SetToolTip(label, Loc.T(t.Description) + (t.AllUsers ? "\n\n" + Loc.T("Applies to all users of this PC.") : ""));
            toggle.CheckedChanged += (_, _) =>
            {
                if (_loading) return;
                // Flipping back to the current state cancels the pending change.
                if (_states.TryGetValue(t.Id, out var current) && current == toggle.Checked) _pending.Remove(t.Id);
                else _pending[t.Id] = toggle.Checked;
                UpdateSelectionLabel();
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
            Theme.Button("Remove Ultimate Performance", (_, _) => RunDialog.Run(this, "Removing Ultimate Performance", (log, _, ct) => PowerPlans.RemoveUltimateAsync(log, ct))),
            Theme.Button("Restore previous plan", (_, _) =>
            {
                if (!PowerPlans.HasSavedPlan)
                {
                    Info("There is no saved power plan: WinSolve hasn't switched plans on this PC (or the previous one is already back).");
                    return;
                }
                RunDialog.Run(this, "Restoring the previous power plan", (log, _, ct) => PowerPlans.RestorePreviousAsync(log, ct));
            })));

    // ───────────── DNS ─────────────

    private Control DnsCard() => new StackCard().Add(
        Theme.Label("DNS", Theme.H2),
        Theme.Paragraph("Changes the DNS servers of the connected network adapters (never on a work domain network). The current settings are saved first; choose Default (DHCP) to put them back. Cloudflare and Google are fast; Quad9 and the filtering options block malicious sites."),
        _dnsCurrent,
        Theme.Row(_dns, Theme.Button("Apply DNS", async (_, _) => await ApplyDnsAsync(), primary: true)));

    private bool _dnsBusy;

    private async Task ApplyDnsAsync()
    {
        if (_dnsBusy) return;
        _dnsBusy = true;
        try
        {
            var provider = DnsService.Providers[Math.Max(0, _dns.SelectedIndex)];
            List<DnsAdapter> adapters;
            Dictionary<string, DnsBackupEntry> backup;
            try
            {
                adapters = await DnsService.GetAdaptersAsync();
                backup = await Task.Run(DnsService.LoadBackup);
            }
            catch (Exception ex)
            {
                Info(Loc.T("Could not read the network adapters.") + "\n\n" + ex.Message);
                return;
            }

            var domain = adapters.Where(a => a.Domain).ToList();
            var eligible = adapters.Where(a => !a.Domain).ToList();
            var domainNote = domain.Count == 0 ? ""
                : "\n\n" + string.Format(Loc.T("Not changed, because they are on a network managed by your organization (domain): {0}"), string.Join(", ", domain.Select(a => a.Name)));
            if (eligible.Count == 0)
            {
                Info(Loc.T("There is no connected network adapter whose DNS WinSolve can change.") + domainNote);
                return;
            }

            string Servers(IEnumerable<string> s) => s.Any() ? string.Join(", ", s) : Loc.T("none");
            List<DnsRestorePlan>? plans = null;
            string message;
            if (provider.IsDefault)
            {
                plans = eligible.Select(a => DnsService.PlanDefault(a, backup.GetValueOrDefault(a.Guid))).ToList();
                var lines = plans.Select(p => p.Kind switch
                {
                    DnsRestoreKind.SetSaved => string.Format(Loc.T("{0}: back to its previous DNS servers ({1})"), p.Adapter.Name, Servers(p.Servers)),
                    DnsRestoreKind.ResetToDhcp => string.Format(Loc.T("{0}: DNS servers from the router (DHCP)"), p.Adapter.Name),
                    _ => string.Format(Loc.T("{0}: kept as it is ({1}). It has a static IP address, so without these servers it would have no DNS."), p.Adapter.Name, Servers(p.Adapter.V4.Concat(p.Adapter.V6))),
                }).ToList();
                if (plans.All(p => p.Kind == DnsRestoreKind.Keep))
                {
                    Info(Loc.T("Nothing to change:") + "\n\n- " + string.Join("\n- ", lines) + domainNote);
                    return;
                }
                message = Loc.T("Restore the DNS settings of these adapters?") + "\n\n- " + string.Join("\n- ", lines) + domainNote;
            }
            else
            {
                message = string.Format(Loc.T("Change the DNS servers to {0} on these adapters?"), Loc.T(provider.Name))
                          + "\n\n- " + string.Join("\n- ", eligible.Select(a => $"{a.Name} ({Servers(a.V4.Concat(a.V6))})"))
                          + "\n\n" + Loc.T("Their current settings are saved first: choose Default (DHCP) later to put them back.")
                          + domainNote;
            }
            if (Loc.Show(this, message, "DNS", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

            RunDialog.Run(this, "Changing DNS", (log, _, ct) => plans is not null
                ? DnsService.RestoreAsync(plans, log, ct)
                : DnsService.ApplyAsync(provider, eligible, log, ct));
            await LoadDnsAsync();
        }
        finally
        {
            _dnsBusy = false;
        }
    }

    private async Task LoadDnsAsync()
    {
        var current = await DnsService.CurrentAsync();
        _dnsCurrent.Text = $"Current: {Loc.T(current)}";
        var index = Array.FindIndex(DnsService.Providers, p => p.Name == current);
        if (index >= 0) _dns.SelectedIndex = index;
    }

    // ───────────── Windows Update ─────────────

    private const string HomeUpdateNote = "Windows Home ignores the update delay policies: on this PC only the driver and automatic restart settings take effect.";

    private readonly Label _wuHomeNote = Theme.Label(HomeUpdateNote, Theme.Small, Theme.Warn);

    private Control UpdatesCard()
    {
        _wuHomeNote.Visible = false;
        return new StackCard().Add(
            Theme.Label("Windows Update", Theme.H2),
            Theme.Paragraph("Security settings delay feature updates by a year and security updates by 4 days, skip drivers from Windows Update and avoid automatic restarts. You still get every security fix."),
            _wuHomeNote,
            Theme.Row(_wuSecurity, _wuDefault));
    }

    private void SetUpdatePolicy(bool security)
    {
        var home = UpdatePolicy.IsHomeEdition;
        var message = security
            ? Loc.T("Apply the Windows Update security settings?") + "\n\n"
              + Loc.T("Feature updates are delayed by a year and security updates by 4 days, drivers aren't installed through Windows Update and the PC doesn't restart by itself while someone is signed in.")
              + (home ? "\n\n" + Loc.T(HomeUpdateNote) : "")
            : Loc.T("Go back to the default Windows Update settings?") + "\n\n"
              + Loc.T("Only the settings that 'Security settings' changed are put back as they were before.");
        if (Loc.Show(this, message, "Windows Update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
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

        // Turning a feature off can break things people rely on: say what, and always ask.
        var disabling = changes.Where(c => !c.Toggle.Checked).Select(c => c.Feature).ToList();
        var enabling = changes.Where(c => c.Toggle.Checked).Select(c => c.Feature).ToList();
        var parts = new List<string>();
        if (disabling.Count > 0)
            parts.Add(Loc.T("Turn off these Windows features?") + "\n\n"
                      + string.Join("\n", disabling.Select(f => $"- {Loc.T(f.Name)}: {Loc.T(f.DisableEffect)}")));
        if (enabling.Count > 0)
            parts.Add(Loc.T("Turn on:") + "\n- " + string.Join("\n- ", enabling.Select(f => Loc.T(f.Name))));
        parts.Add(Loc.T("A restart is needed to finish."));
        var message = string.Join("\n\n", parts);
        if (disabling.Count > 0 ? !ConfirmDanger(message, "Windows features") : !Confirm(message, "Windows features")) return;
        var ok = RunDialog.Run(this, "Windows features", async (log, progress, ct) =>
        {
            for (int i = 0; i < changes.Count; i++)
            {
                await WindowsFeatures.SetAsync(changes[i].Feature, changes[i].Toggle.Checked, log, ct);
                progress(i + 1, changes.Count);
            }
            log("Done. Restart the PC to finish.");
        });
        _ = LoadFeaturesAsync();
        if (ok) AskReboot(this);
    }

    // ───────────── Load ─────────────

    public override async void OnShown()
    {
        await RefreshStatesAsync(resetToggles: false);
        if (IsDisposed) return;
        _ = LoadDnsAsync();
        if (_features.All(f => f.Feature.Enabled is null)) _ = LoadFeaturesAsync();
    }

    /// <summary>
    /// Reads every tweak's state off the UI thread, then updates badges and switches in one
    /// batch. Unsaved switch changes are kept unless <paramref name="resetToggles"/> is set.
    /// </summary>
    private async Task RefreshStatesAsync(bool resetToggles)
    {
        var ids = _rows.Keys.Concat(_toggles.Keys).Distinct().ToList();
        var states = await Task.Run(() => ids.ToDictionary(id => id, id => TweakCatalog.Find(id) is { IsAction: false } t && t.SafeIsApplied()));
        var security = await Task.Run(UpdatePolicy.IsSecurityMode);
        if (IsDisposed) return;
        _states = states;
        if (resetToggles) _pending.Clear();
        else foreach (var id in _pending.Where(p => states.GetValueOrDefault(p.Key) == p.Value).Select(p => p.Key).ToList()) _pending.Remove(id);
        bool Wanted(string id) => _pending.TryGetValue(id, out var v) ? v : states.GetValueOrDefault(id);

        _loading = true;
        var changed = _rows.Any(r => r.Value.Badge.Tag is not bool b || b != states.GetValueOrDefault(r.Key))
                      || _toggles.Any(t => t.Value.Checked != Wanted(t.Key));
        void Apply()
        {
            RefreshBadges(states);
            foreach (var (id, toggle) in _toggles)
                if (toggle.Checked != Wanted(id)) toggle.Checked = Wanted(id);
        }
        if (changed) LayoutBatch.Run(this, Apply); else UpdateSelectionLabel();
        _loading = false;
        _wuSecurity.Primary = security;
        _wuDefault.Primary = !security;
        _wuHomeNote.Visible = await Task.Run(() => UpdatePolicy.IsHomeEdition);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }
}
