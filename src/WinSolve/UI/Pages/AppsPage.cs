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
    private readonly FlatBtn _tabPrograms, _tabStore;
    private List<InstalledProgram> _programList = [];
    private List<StoreApp> _storeList = [];
    private bool _storeLoaded;
    private bool _showStore;

    public override string Key => "apps";

    public AppsPage() : base("Apps",
        "Uninstall programs normally, or force-remove them: silent uninstall, then leftover files, shortcuts and registry entries are deleted.")
    {
        _tabPrograms = Theme.Button("Installed programs", (_, _) => ShowTab(false));
        _tabStore = Theme.Button("Microsoft Store apps", (_, _) => ShowTab(true));
        _search.TextChanged += (_, _) => Fill();
        _onlyBloat.CheckedChanged += (_, _) => Fill();

        AddRow(Theme.Row(_tabPrograms, _tabStore));
        AddRow(Theme.Row(_search,
            Theme.Button("Uninstall", async (_, _) => await UninstallSelected(force: false)),
            Theme.Button("Force uninstall", async (_, _) => await UninstallSelected(force: true), primary: true),
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
        var split = new SplitContainer { Orientation = Orientation.Horizontal, SplitterWidth = 10, BackColor = Theme.Background };
        split.Panel1.Controls.Add(_host);
        _host.Dock = DockStyle.Fill;
        split.Panel2.Controls.Add(_runner);
        split.HandleCreated += (_, _) => { try { split.SplitterDistance = (int)(split.Height * 0.62); } catch { } };
        AddRow(split, fill: true);
        ShowTab(false);
    }

    private async void ShowTab(bool store)
    {
        _showStore = store;
        _tabPrograms.Primary = !store;
        _tabStore.Primary = store;
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
                var i = _store.Rows.Add(a.FriendlyName, a.Name, a.IsBloat ? "Bloatware" : "");
                _store.Rows[i].Tag = a;
            }
        }
        else
        {
            _programs.Rows.Clear();
            foreach (var p in _programList.Where(p => Matches(p.DisplayName, p.Publisher)))
            {
                var i = _programs.Rows.Add(p.DisplayName, p.Publisher, p.Version, p.SizeBytes > 0 ? Format.Bytes(p.SizeBytes) : "", p.Scope);
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

        var names = selected.Select(o => o switch { StoreApp a => a.FriendlyName, InstalledProgram p => p.DisplayName, _ => "" }).ToList();

        // Show exactly which folders will be deleted before anything happens.
        var folders = selected.OfType<InstalledProgram>().ToDictionary(p => p, AppsService.GetForceRemovalFolders);
        var folderText = force && !_showStore
            ? "\n\nFolders that will be permanently deleted:\n" +
              (folders.Values.Any(f => f.Count > 0) ? string.Join("\n", folders.Values.SelectMany(f => f).Select(f => "  " + f)) : "  (none found)")
            : "";

        var message = force
            ? $"Force-remove {selected.Count} app(s)?\n\n- {string.Join("\n- ", names)}\n\n" +
              (_showStore
                  ? "They are removed for all users and deprovisioned so Windows won't reinstall them."
                  : "The uninstaller runs silently, then the app's processes are closed and its folders, shortcuts and registry entry are deleted. This cannot be undone.")
              + folderText
            : $"Uninstall {selected.Count} app(s)?\n\n- {string.Join("\n- ", names)}";
        if (!ConfirmDanger(message)) return;

        if (force && AppSettings.Current.CreateRestorePoint)
            await _runner.RunAsync("Creating restore point", async (log, _, ct) => await TaskCatalog.CreateRestorePoint(new TaskContext(log, ct), "WinSolve - before force uninstall"));

        await _runner.RunAsync(force ? "Force uninstall" : "Uninstall", async (log, progress, ct) =>
        {
            for (int i = 0; i < selected.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                log($"> {names[i]}");
                switch (selected[i])
                {
                    case StoreApp a:
                        await AppsService.ForceRemoveStoreAppAsync(a, log, ct);
                        break;
                    case InstalledProgram p when force:
                        await AppsService.ForceUninstallAsync(p, folders[p], log, ct);
                        break;
                    case InstalledProgram p:
                        await AppsService.UninstallAsync(p, log, ct);
                        break;
                }
                progress(i + 1, selected.Count);
            }
        });
        await LoadAsync(true);
    }
}
