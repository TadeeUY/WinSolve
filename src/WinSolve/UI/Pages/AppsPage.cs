using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class AppsPage : Page
{
    private readonly DataGridView _programs = new();
    private readonly DataGridView _store = new();
    private readonly TaskRunnerView _runner = new();
    private readonly Panel _host = new() { BackColor = Color.Transparent };
    private readonly TextBox _search = Theme.TextBox("Search apps");
    private readonly CheckBox _onlyBloat = Theme.Check("Only show preinstalled bloatware", false);
    private readonly Segmented _tabs = new();
    private List<InstalledProgram> _programList = [];
    private List<StoreApp> _storeList = [];
    private bool _storeLoaded;
    private bool _showStore;

    public override string Key => "apps";

    public AppsPage() : base("Apps",
        "Uninstall programs normally, or force-remove them: silent uninstall, then leftover files, shortcuts and registry entries are deleted.")
    {
        _tabs.Add("programs", "Installed programs", "\uE71D").Add("store", "Microsoft Store apps", "\uE719");
        _tabs.SelectedChanged += k => ShowTab(k == "store");
        _search.TextChanged += (_, _) => Fill();
        _onlyBloat.CheckedChanged += (_, _) => Fill();

        AddRow(_tabs);
        AddRow(Theme.Row(_search,
            Theme.Button("Uninstall", async (_, _) => await UninstallSelected(force: false)),
            Theme.Button("Force uninstall", async (_, _) => await UninstallSelected(force: true), primary: true, glyph: "\uE74D"),
            Theme.Button("Refresh", async (_, _) => await LoadAsync(true)),
            _onlyBloat));

        Theme.StyleGrid(_programs);
        _programs.ReadOnly = true;
        _programs.MultiSelect = true;
        _programs.Columns.Add("name", "Name");
        _programs.Columns.Add("pub", "Publisher");
        _programs.Columns.Add("ver", "Version");
        _programs.Columns.Add("size", "Size");
        _programs.Columns.Add("scope", "Installed for");
        _programs.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _programs.Columns["name"]!.FillWeight = 220;
        _programs.Columns["pub"]!.FillWeight = 140;
        _programs.Columns["ver"]!.FillWeight = 70;
        _programs.Columns["size"]!.FillWeight = 50;
        _programs.Columns["scope"]!.FillWeight = 80;
        _programs.Columns["pub"]!.DefaultCellStyle.ForeColor = Theme.Muted;

        Theme.StyleGrid(_store);
        _store.ReadOnly = true;
        _store.MultiSelect = true;
        _store.Columns.Add("name", "App");
        _store.Columns.Add("pkg", "Package");
        _store.Columns.Add("bloat", "Notes");
        _store.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _store.Columns["pkg"]!.DefaultCellStyle.ForeColor = Theme.Muted;
        _store.Columns["bloat"]!.FillWeight = 50;

        _programs.Dock = _store.Dock = DockStyle.Fill;
        Theme.EmptyState(_programs, "No programs match your search.");
        Theme.EmptyState(_store, "No apps to show. Clear the search or the bloatware filter.");
        var split = new SplitContainer { Orientation = Orientation.Horizontal, SplitterWidth = 10, BackColor = Theme.Background };
        split.Panel1.Controls.Add(Theme.InCard(_host));
        split.Panel2.Controls.Add(_runner);
        split.HandleCreated += (_, _) => { try { split.SplitterDistance = (int)(split.Height * 0.62); } catch { } };
        AddRow(split, fill: true);
        _tabs.Select("programs");
    }

    private async void ShowTab(bool store)
    {
        _showStore = store;
        _onlyBloat.Visible = store;
        _host.Controls.Clear();
        _host.Controls.Add(store ? _store : _programs);
        if (store && !_storeLoaded) await LoadAsync(false);
        else Fill();
    }

    public override async void OnShown()
    {
        if (_programList.Count == 0) await LoadAsync(false);
    }

    private async Task LoadAsync(bool force)
    {
        if (!_showStore || force)
            _programList = await Task.Run(AppsService.GetPrograms);
        if (_showStore && (!_storeLoaded || force))
        {
            _runner.Log("Reading Microsoft Store apps...");
            _storeList = await AppsService.GetStoreAppsAsync();
            _storeLoaded = true;
            _runner.Log($"{_storeList.Count} Store apps, {_storeList.Count(a => a.IsBloat)} of them commonly removed bloatware.");
        }
        Fill();
    }

    private bool Matches(params string[] fields)
    {
        var q = _search.Text.Trim();
        return q.Length == 0 || fields.Any(f => f.Contains(q, StringComparison.CurrentCultureIgnoreCase));
    }

    private void Fill()
    {
        if (_showStore)
        {
            _store.Rows.Clear();
            foreach (var a in _storeList.Where(a => (!_onlyBloat.Checked || a.IsBloat) && Matches(a.FriendlyName, a.Name)))
            {
                var note = a.Risk switch
                {
                    StoreAppRisk.Required => AppsService.RequiredByWindows,
                    StoreAppRisk.Warn => "Built-in Windows app",
                    _ => a.IsBloat ? "Bloatware" : "",
                };
                var i = _store.Rows.Add(a.FriendlyName, a.Name, Localization.Loc.T(note));
                _store.Rows[i].Tag = a;
                if (a.Risk == StoreAppRisk.Required)
                {
                    // Shown, but greyed out: it can't be removed.
                    _store.Rows[i].DefaultCellStyle.ForeColor = Theme.DisabledText;
                    _store.Rows[i].Cells["pkg"].Style.ForeColor = Theme.DisabledText;
                }
            }
        }
        else
        {
            _programs.Rows.Clear();
            foreach (var p in _programList.Where(p => Matches(p.DisplayName, p.Publisher)))
            {
                var i = _programs.Rows.Add(p.DisplayName, p.Publisher, p.Version, p.SizeBytes > 0 ? Format.Bytes(p.SizeBytes) : "", Localization.Loc.T(p.Scope));
                _programs.Rows[i].Tag = p;
            }
        }
    }

    private async Task UninstallSelected(bool force)
    {
        if (_runner.IsBusy) return;
        var grid = _showStore ? _store : _programs;
        var selected = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<object>().ToList();
        if (selected.Count == 0)
        {
            Info("Select one or more apps first (Ctrl+click to select several).");
            return;
        }

        selected = FilterSelection(selected, force);
        if (selected.Count == 0) return;
        var names = selected.Select(o => o switch { StoreApp a => a.FriendlyName, InstalledProgram p => p.DisplayName, _ => "" }).ToList();

        // Force uninstall: the person picks the leftover folders (their data is never ticked for them).
        var folders = new Dictionary<InstalledProgram, List<string>>();
        var folderText = "";
        if (force && !_showStore)
        {
            var leftovers = selected.OfType<InstalledProgram>().SelectMany(AppsService.GetLeftovers).ToList();
            IReadOnlyList<Leftover> chosen = [];
            if (leftovers.Count > 0)
            {
                using var dlg = new LeftoversDialog(leftovers);
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                chosen = dlg.Chosen;
            }
            foreach (var p in selected.OfType<InstalledProgram>())
                folders[p] = chosen.Where(l => l.Program == p).Select(l => l.Path).ToList();
            folderText = "\n\nFolders to delete (to the Recycle Bin when possible):\n" +
                         (chosen.Count > 0 ? string.Join("\n", chosen.Select(l => "  " + l.Path)) : "  (none)");
        }

        var message = force
            ? $"Force-remove {selected.Count} app(s)?\n\n- {string.Join("\n- ", names)}\n\n" +
              (_showStore
                  ? "They are removed for your account only, with all their versions and data. Other users of this PC keep them."
                  : "The uninstaller runs silently, then the app's processes are closed and the folders you chose and its shortcuts are deleted. Its entry is removed from the list only if the program is really gone.")
              + folderText
            : $"Uninstall {selected.Count} app(s)?\n\n- {string.Join("\n- ", names)}";
        if (!ConfirmDanger(message)) return;

        var prompts = new UninstallPrompts
        {
            KeepWaiting = name => Invoke(() => Localization.Loc.Show(this,
                $"The uninstaller of {name} is still running.\n\nIt may be waiting for an answer in a window behind this one.\n\nKeep waiting? If you stop waiting, the uninstaller keeps running and WinSolve deletes nothing for this program.",
                "WinSolve", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes),
            ConfirmPermanentDelete = dirs => Invoke(() => ConfirmDanger(
                "These folders can't be moved to the Recycle Bin:\n\n" + string.Join("\n", dirs.Select(d => "  " + d)) +
                "\n\nDelete them permanently? This cannot be undone.")),
        };

        // Stop if the restore point was cancelled; ask if it failed (the user wanted one before force removal).
        if (force && AppSettings.Current.CreateRestorePoint)
        {
            var created = false;
            if (!await _runner.RunAsync("Creating restore point", async (log, _, ct) => created = await TaskCatalog.CreateRestorePoint(new TaskContext(log, ct), "WinSolve - before force uninstall")))
                return;
            if (!created && !ConfirmDanger("Could not create a restore point. Continue without it?")) return;
        }

        await _runner.RunAsync(force ? "Force uninstall" : "Uninstall", async (log, progress, ct) =>
        {
            for (int i = 0; i < selected.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                log($"> {names[i]}");
                switch (selected[i])
                {
                    case StoreApp a when force:
                        await AppsService.ForceRemoveStoreAppAsync(a, log, ct);
                        break;
                    case StoreApp a:
                        await AppsService.RemoveStoreAppAsync(a, log, ct);
                        break;
                    case InstalledProgram p when force:
                        await AppsService.ForceUninstallAsync(p, folders.GetValueOrDefault(p) ?? [], log, ct, prompts);
                        break;
                    case InstalledProgram p:
                        await AppsService.UninstallAsync(p, log, ct, prompts);
                        break;
                }
                progress(i + 1, selected.Count);
            }
        });
        await LoadAsync(true);
    }

    /// <summary>
    /// Drops what must not be removed (with an explanation) and asks again, naming the consequence,
    /// for what can be removed but breaks something. Returns what is left to remove.
    /// </summary>
    private List<object> FilterSelection(List<object> selected, bool force)
    {
        // Store apps Windows needs: never removed.
        var required = selected.OfType<StoreApp>().Where(a => a.Risk == StoreAppRisk.Required).ToList();
        if (required.Count > 0)
            Info("These apps are required by Windows and can't be removed:\n\n- " + string.Join("\n- ", required.Select(a => a.FriendlyName)) +
                 "\n\nWithout them you couldn't install apps, sign in, open pictures and videos or use the Start menu.");

        // WinSolve itself.
        var self = selected.OfType<InstalledProgram>().Where(p => AppsService.Classify(p, out _) == ProgramKind.WinSolve).ToList();
        if (self.Count > 0)
            Info("WinSolve can't uninstall itself from here. Use Settings > Apps > Installed apps, or WinSolve's uninstaller.");

        // Runtimes and drivers: never forced; the normal uninstaller only after a clear warning.
        var shared = selected.OfType<InstalledProgram>()
            .Select(p => (Program: p, Kind: AppsService.Classify(p, out var why), Why: why))
            .Where(x => x.Kind is ProgramKind.SharedRuntime or ProgramKind.Driver).ToList();
        if (force && shared.Count > 0)
            Info("Force uninstall is not available for these, because other programs or devices need them:\n\n- " +
                 string.Join("\n- ", shared.Select(x => x.Program.DisplayName)) +
                 "\n\nUse Uninstall to run their own uninstaller.");

        var rest = selected.Where(o => !required.Contains(o) && !self.Contains(o) && !(force && shared.Any(x => x.Program == o))).ToList();

        if (!force && shared.Count > 0 && rest.Count > 0)
        {
            var lines = shared.Select(x => $"- {x.Program.DisplayName}\n  {x.Why}");
            if (!ConfirmDanger("Other programs or devices need these:\n\n" + string.Join("\n", lines) +
                               "\n\nUninstall them only if you know they are not needed. Continue?"))
                return [];
        }

        // Built-in apps: removable, but say what stops working.
        var warn = rest.OfType<StoreApp>().Where(a => a.Risk == StoreAppRisk.Warn).ToList();
        if (warn.Count > 0)
        {
            var lines = warn.Select(a => $"- {a.FriendlyName}\n  {a.Consequence}");
            if (!ConfirmDanger("These are built-in Windows apps:\n\n" + string.Join("\n", lines) +
                               "\n\nRemove them anyway? You can reinstall them from the Microsoft Store."))
                return [];
        }
        return rest;
    }
}
