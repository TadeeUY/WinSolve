using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

/// <summary>Pause Windows Update, hide a problem update, see the history and roll back a driver.</summary>
public sealed class WindowsUpdatePage : Page
{
    private readonly Label _pauseStatus = Theme.Label("", Theme.BodyBold);
    private readonly Segmented _tabs = new();
    private readonly FlowLayoutPanel _actions = Theme.Row();
    private readonly Label _status = Theme.Label("", Theme.Small, Theme.Muted);
    private readonly DataGridView _grid = new();
    private readonly Dictionary<string, object> _cache = [];
    private int _loadId;
    private string _empty = "";

    public override string Key => "winupdate";

    public WindowsUpdatePage() : base("Windows Update",
        "Pause updates, stop a problem update from reinstalling, check what was installed and roll back a driver.")
    {
        var pause = new StackCard().Add(
            Theme.SectionHeader("", "Pause updates", "Nothing is downloaded or installed until the date below. Then updates resume by themselves."),
            _pauseStatus,
            Theme.Row(
                Theme.Button("1 week", (_, _) => Pause(7), glyph: ""),
                Theme.Button("2 weeks", (_, _) => Pause(14)),
                Theme.Button("5 weeks (maximum)", (_, _) => Pause(WindowsUpdateService.MaxPauseDays)),
                Theme.Button("Resume updates", (_, _) => Resume(), glyph: ""),
                Theme.Button("Open Windows Update", (_, _) => ProcessRunner.ShellOpen("ms-settings:windowsupdate"))));
        AddRow(pause);

        _tabs.Add("pending", "Available", "")
             .Add("hidden", "Hidden", "")
             .Add("history", "History", "")
             .Add("rollback", "Roll back a driver", "");
        _tabs.Margin = new Padding(0, 14, 0, 8);
        _tabs.SelectedChanged += async _ => await ShowTabAsync(false);
        AddRow(_tabs);
        AddRow(_actions);
        AddRow(_status);

        Theme.StyleGrid(_grid);
        _grid.MultiSelect = false;
        _grid.Paint += (_, e) =>
        {
            if (_grid.Rows.Count > 0 || _empty.Length == 0) return;
            var area = new Rectangle(20, _grid.ColumnHeadersHeight + 30, _grid.Width - 40, 60);
            TextRenderer.DrawText(e.Graphics, Localization.Loc.T(_empty), Theme.Body, area, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        };
        AddRow(Theme.InCard(_grid), fill: true);
        _tabs.Select("pending", notify: false);
    }

    public override void OnShown()
    {
        UpdatePauseStatus();
        if (_grid.Columns.Count == 0) _ = ShowTabAsync(false);
    }

    // ───────────── Pause ─────────────

    private void UpdatePauseStatus()
    {
        var until = WindowsUpdateService.PausedUntil();
        _pauseStatus.Text = until is { } u
            ? string.Format(Localization.Loc.T("Paused until {0}"), u.ToString("D"))
            : Localization.Loc.T("Updates are on");
        _pauseStatus.ForeColor = until is null ? Theme.Good : Theme.Warn;
    }

    private void Pause(int days)
    {
        try { WindowsUpdateService.Pause(days); }
        catch (Exception ex) { Info($"Could not pause updates: {ex.Message}"); }
        UpdatePauseStatus();
    }

    private void Resume()
    {
        try { WindowsUpdateService.Resume(); }
        catch (Exception ex) { Info($"Could not resume updates: {ex.Message}"); }
        UpdatePauseStatus();
    }

    // ───────────── Lists ─────────────

    private async Task ShowTabAsync(bool refresh)
    {
        var tab = _tabs.Selected ?? "pending";
        var id = ++_loadId;
        BuildActions(tab);
        BuildColumns(tab);
        if (refresh) _cache.Remove(tab);
        if (!_cache.TryGetValue(tab, out var data))
        {
            _status.Text = Localization.Loc.T(tab is "pending" or "hidden"
                ? "Asking Windows Update... this can take a minute."
                : "Loading...");
            SetEmpty("");
            try
            {
                data = tab switch
                {
                    "pending" => await Task.Run(() => (object)WindowsUpdateService.GetPending(hidden: false)),
                    "hidden" => await Task.Run(() => (object)WindowsUpdateService.GetPending(hidden: true)),
                    "history" => await Task.Run(() => (object)WindowsUpdateService.GetHistory()),
                    _ => await Task.Run(() => (object)WindowsUpdateService.GetRollbackCandidates()),
                };
            }
            catch (Exception ex)
            {
                Logger.Write($"Windows Update page ({tab}): {ex}");
                if (id != _loadId) return;
                _status.Text = string.Format(Localization.Loc.T("Windows Update didn't answer: {0}"), ex.Message);
                return;
            }
            _cache[tab] = data;
        }
        if (id != _loadId) return; // another tab was picked meanwhile
        Fill(tab, data);
    }

    private void BuildActions(string tab)
    {
        Theme.ClearAndDispose(_actions);
        switch (tab)
        {
            case "pending":
                _actions.Controls.Add(Theme.Button("Hide selected update", async (_, _) => await SetHiddenAsync(true), primary: true, glyph: ""));
                break;
            case "hidden":
                _actions.Controls.Add(Theme.Button("Show again", async (_, _) => await SetHiddenAsync(false), primary: true, glyph: ""));
                break;
            case "rollback":
                _actions.Controls.Add(Theme.Button("Roll back selected driver", async (_, _) => await RollBackAsync(), primary: true, glyph: ""));
                _actions.Controls.Add(Theme.Button("Device Manager", (_, _) => ProcessRunner.ShellOpen("devmgmt.msc")));
                break;
            default:
                _actions.Controls.Add(Theme.Button("Uninstall an update", (_, _) => ProcessRunner.ShellOpen("ms-settings:windowsupdate-history")));
                break;
        }
        _actions.Controls.Add(Theme.Button("Refresh", async (_, _) => await ShowTabAsync(true), glyph: ""));
        Localization.Loc.Apply(_actions);
    }

    private void BuildColumns(string tab)
    {
        _grid.Rows.Clear();
        _grid.Columns.Clear();
        void Col(string name, string header, int width = 0, bool muted = false)
        {
            var c = new DataGridViewTextBoxColumn
            {
                Name = name, HeaderText = Localization.Loc.T(header), ReadOnly = true,
                AutoSizeMode = width == 0 ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            };
            if (width > 0) c.Width = width;
            if (muted) c.DefaultCellStyle.ForeColor = Theme.Muted;
            _grid.Columns.Add(c);
        }
        switch (tab)
        {
            case "pending" or "hidden":
                Col("title", "Update");
                Col("kind", "Type", 110, muted: true);
                Col("size", "Size", 110, muted: true);
                break;
            case "history":
                Col("date", "Date", 150, muted: true);
                Col("title", "Update");
                Col("op", "Action", 120, muted: true);
                Col("result", "Result", 170);
                break;
            default:
                Col("name", "Device");
                Col("class", "Type", 120, muted: true);
                Col("provider", "Provider", 170, muted: true);
                Col("version", "Driver version", 150, muted: true);
                Col("date", "Date", 110, muted: true);
                break;
        }
    }

    private void Fill(string tab, object data)
    {
        _grid.Rows.Clear();
        switch (data)
        {
            case List<PendingUpdate> updates:
                foreach (var u in updates)
                    _grid.Rows[_grid.Rows.Add(u.Title, Localization.Loc.T(u.Kind), u.SizeBytes > 0 ? Format.Bytes(u.SizeBytes) : "—")].Tag = u;
                SetEmpty(tab == "hidden" ? "No hidden updates." : "Windows is up to date.");
                _status.Text = tab == "hidden"
                    ? Localization.Loc.T("Hidden updates are not offered or installed until you show them again.")
                    : Localization.Loc.T("If an update keeps failing or breaks something, select it and hide it so Windows stops installing it.");
                break;
            case List<UpdateHistoryEntry> history:
                foreach (var h in history)
                {
                    var i = _grid.Rows.Add(h.Date.ToString("g"), h.Title, Localization.Loc.T(h.Operation), Localization.Loc.T(h.Result));
                    _grid.Rows[i].Cells["result"].Style.ForeColor = h.Failed ? Theme.Bad : Theme.Good;
                }
                SetEmpty("No update history.");
                var failed = history.Count(h => h.Failed);
                _status.Text = failed == 0
                    ? string.Format(Localization.Loc.T("{0} entries."), history.Count)
                    : string.Format(Localization.Loc.T("{0} entries, {1} failed. An update that keeps failing can be hidden in Available."), history.Count, failed);
                break;
            case List<RollbackDevice> devices:
                foreach (var d in devices)
                    _grid.Rows[_grid.Rows.Add(d.Name, d.Class, d.Provider, d.Version, d.Date?.ToString("d") ?? "—")].Tag = d;
                SetEmpty("No devices with third-party drivers.");
                _status.Text = Localization.Loc.T("Goes back to the driver the device used before its last driver update, like Device Manager does.");
                break;
        }
    }

    private void SetEmpty(string text)
    {
        _empty = text;
        _grid.Invalidate();
    }

    // ───────────── Actions ─────────────

    private async Task SetHiddenAsync(bool hide)
    {
        if (_grid.SelectedRows.Count == 0 || _grid.SelectedRows[0].Tag is not PendingUpdate u)
        {
            Info("Select an update first.");
            return;
        }
        if (hide && !Confirm($"Hide \"{u.Title}\"?\n\nWindows won't offer or install it until you show it again under Hidden."))
            return;
        _status.Text = Localization.Loc.T("Working...");
        try
        {
            await Task.Run(() => WindowsUpdateService.SetHidden(u.Id, hide));
        }
        catch (Exception ex)
        {
            Info($"Windows Update refused the change: {ex.Message}");
        }
        _cache.Remove("pending");
        _cache.Remove("hidden");
        await ShowTabAsync(false);
    }

    private async Task RollBackAsync()
    {
        if (_grid.SelectedRows.Count == 0 || _grid.SelectedRows[0].Tag is not RollbackDevice d)
        {
            Info("Select a device first.");
            return;
        }
        if (!ConfirmDanger($"Roll back the driver of \"{d.Name}\"?\n\nThe device goes back to the driver it used before version {d.Version}. The screen may flicker or the device may stop for a moment."))
            return;
        _status.Text = Localization.Loc.T("Rolling back the driver...");
        try
        {
            var reboot = await Task.Run(() => WindowsUpdateService.RollBack(d.InstanceId));
            Toast.Show(new ToastOptions
            {
                Title = "Driver rolled back",
                Detail = reboot ? "Restart the PC to finish." : "The previous driver is in use now.",
                Glyph = "", SecondaryText = "OK", AutoCloseSeconds = 8, Sound = false,
            });
        }
        catch (Exception ex)
        {
            Info(ex.Message);
        }
        await ShowTabAsync(true);
    }
}
