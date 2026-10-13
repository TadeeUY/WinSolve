using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class DriversPage : Page
{
    private readonly StackCard _gpuCard = new();
    private readonly TaskRunnerView _runner = new();
    private List<GpuInfo> _gpus = [];
    private readonly Dictionary<string, DriverRelease?> _latest = [];

    public override string Key => "drivers";

    public DriversPage() : base("Drivers",
        "Update, repair and clean-install drivers. Graphics: NVIDIA (GeForce / Game Ready) and AMD (Radeon Adrenalin).")
    {
        var other = new StackCard().Add(
            Theme.Label("All other drivers", Theme.H2),
            Theme.Paragraph("Windows Update hosts signed drivers from most manufacturers (chipset, audio, network, touchpad...)."),
            Theme.Row(
                Theme.Button("Check for driver updates", async (_, _) => await Run("Checking driver updates", DriverService.CheckWindowsUpdateDriversAsync)),
                Theme.Button("Install driver updates", async (_, _) => await InstallDriverUpdates()),
                Theme.Button("Clean up old drivers", (_, _) => { using var d = new OldDriversDialog(); d.ShowDialog(this); }, glyph: "\uE74D"),
                Theme.Button("Repair devices with errors", async (_, _) => await RepairDevices()),
                Theme.Button("Back up drivers", async (_, _) => await Run("Backing up drivers", async (log, ct) =>
                {
                    var folder = await DriverService.BackupDriversAsync(log, ct);
                    ProcessRunner.ShellOpen(folder);
                }))));

        AddRow(new Stack(scroll: true).Add(_gpuCard, other), fill: true);
        AddRow(_runner, height: 230);
    }

    private Task<bool> Run(string title, Func<Action<string>, CancellationToken, Task> work)
        => _runner.RunAsync(title, (log, _, ct) => work(log, ct));

    /// <summary>Asks on the UI thread from background work (default answer: No).</summary>
    private bool AskFromWork(string message) => IsDisposed ? false : (bool)Invoke(new Func<bool>(() => ConfirmDanger(message)));

    public override async void OnShown()
    {
        if (_gpus.Count == 0)
        {
            _gpus = await Task.Run(DriverService.GetGpus);
            Render();
        }
        // Old driver versions are only offered for cleanup once a restart proved the new ones work.
        await OldDriversDialog.OfferIfRebootedAsync(this);
    }

    private async Task InstallDriverUpdates()
    {
        List<DriverService.PendingDriverUpdate> pending = [];
        if (!await Run("Checking driver updates", async (log, ct) => pending = await DriverService.GetWindowsUpdateDriversAsync(log, ct))) return;
        if (pending.Count == 0)
        {
            Info("All drivers are up to date according to Windows Update.");
            return;
        }

        List<DriverService.PendingDriverUpdate> chosen;
        using (var dlg = new DriverUpdatesDialog(pending))
        {
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            chosen = dlg.Selected;
        }
        if (chosen.Count == 0) return;

        if (await Run("Installing driver updates", (log, ct) => DriverService.InstallWindowsUpdateDriversAsync(chosen, AskFromWork, log, ct)))
            OldDriversDialog.OfferAfterUpdate();
    }

    private async Task RepairDevices()
    {
        List<ProblemDevice> devices = [];
        try { devices = await Task.Run(HardwareService.GetProblemDevices); }
        catch (Exception ex) { Info(ex.Message); return; }
        var plans = HardwareService.PlanRepairs(devices);
        var skipped = devices.Where(d => plans.All(p => p.Device.InstanceId != d.InstanceId)).ToList();
        var skippedText = skipped.Count == 0 ? "" : "\n\n" + Localization.Loc.T("Not touched (a restart or reinstall can't fix these):") + "\n" +
            string.Join("\n", skipped.Select(d => $"• {d.Name} (code {d.ErrorCode}): {Localization.Loc.T(d.Explanation)}"));
        if (plans.Count == 0)
        {
            Info(Localization.Loc.T("No devices with problems that a restart or reinstall can fix.") + skippedText);
            return;
        }
        var list = string.Join("\n", plans.Select(p => Localization.Loc.T(HardwareService.DescribePlan(p))));
        if (!ConfirmDanger(Localization.Loc.T("These devices will be repaired:") + "\n\n" + list + skippedText + "\n\n" + Localization.Loc.T("Continue?")))
            return;
        await Run("Repairing devices", (log, ct) => DriverService.RepairProblemDevicesAsync(plans, log, ct));
    }

    private void Render()
    {
        _gpuCard.Body.Controls.Clear();
        _gpuCard.Add(Theme.Label("Graphics", Theme.H2));
        if (_gpus.Count == 0)
        {
            _gpuCard.Add(Theme.Paragraph("No graphics card with a vendor driver was found."));
            return;
        }

        bool first = true;
        foreach (var gpu in _gpus)
        {
            if (!first) _gpuCard.Add(new Divider());
            first = false;

            _gpuCard.Add(GpuHeader(gpu));

            if (_latest.TryGetValue(gpu.PnpId, out var latest))
            {
                if (latest is null)
                    _gpuCard.Add(Theme.Status("Could not check the latest version automatically.", Theme.Muted));
                else if (DriverService.IsNewer(latest.Version, gpu.FriendlyVersion))
                    _gpuCard.Add(Theme.Status($"Update available: {latest.Version}" + (latest.ReleaseDate is { } rd ? $" (released {rd})" : ""), Theme.Warn));
                else
                    _gpuCard.Add(Theme.Status($"Up to date (latest is {latest.Version})", Theme.Good));
            }

            var row = Theme.Row();
            switch (gpu.Vendor)
            {
                case GpuVendor.Nvidia:
                    row.Controls.Add(Theme.Button("Check latest version", async (_, _) => await CheckNvidia(gpu)));
                    row.Controls.Add(Theme.Button("Download and clean install", async (_, _) => await NvidiaCleanInstall(gpu), primary: true, glyph: "\uE896"));
                    row.Controls.Add(Theme.Button("Clean install from file", async (_, _) => await CleanInstallFromFile(gpu)));
                    row.Controls.Add(Theme.Button("NVIDIA drivers website", (_, _) => ProcessRunner.ShellOpen(DriverService.NvidiaDownloadPage)));
                    break;
                case GpuVendor.Amd:
                    row.Controls.Add(Theme.Button("Download Adrenalin", (_, _) => ProcessRunner.ShellOpen(DriverService.AmdDownloadPage)));
                    row.Controls.Add(Theme.Button("Clean install from file", async (_, _) => await CleanInstallFromFile(gpu), primary: true));
                    break;
                case GpuVendor.Intel:
                    row.Controls.Add(Theme.Button("Intel Driver & Support Assistant", (_, _) => ProcessRunner.ShellOpen(DriverService.IntelDownloadPage)));
                    row.Controls.Add(Theme.Button("Clean install from file", async (_, _) => await CleanInstallFromFile(gpu)));
                    break;
            }
            row.Controls.Add(Theme.Button("Remove driver only", async (_, _) => await RemoveDriverOnly(gpu)));
            _gpuCard.Add(row);

            if (gpu.Vendor == GpuVendor.Amd)
                _gpuCard.Add(Theme.Paragraph("AMD does not publish a public download API. Download the Adrenalin installer from AMD's website, then use 'Clean install from file'. WinSolve removes the old driver first and then opens the installer, where you can pick 'Factory Reset'."));
        }
    }

    /// <summary>The hybrid graphics warning as an extra paragraph, or nothing.</summary>
    private string HybridNote(GpuVendor vendor)
        => DriverService.HybridWarning(vendor, _gpus) is { } w ? "\n\n" + Localization.Loc.T("WARNING:") + " " + Localization.Loc.T(w) : "";

    private async Task RemoveDriverOnly(GpuInfo gpu)
    {
        if (!ConfirmDanger(Localization.Loc.T($"Remove every {gpu.VendorName} graphics driver package?") + "\n\n" +
                           Localization.Loc.T("1. A restore point is created") + "\n" +
                           Localization.Loc.T(@"2. A copy of the driver is saved in ProgramData\WinSolve\DriverBackup") + "\n" +
                           Localization.Loc.T("3. The driver is removed: the screen falls back to the basic Microsoft driver (low resolution, no games) until you install one again") + "\n\n" +
                           Localization.Loc.T("To get it back: run the vendor's driver installer, or in Device Manager choose the display adapter > Update driver > Browse my computer, and pick the backup folder shown in the log.") +
                           HybridNote(gpu.Vendor)))
            return;
        // The list is not refreshed: the card stays, so 'Clean install from file' is still at hand.
        await Run("Removing graphics driver", (log, ct) => DriverService.RemoveDisplayDriversAsync(gpu.Vendor, AskFromWork, log, ct));
    }

    private static Control GpuHeader(GpuInfo gpu)
    {
        var tint = gpu.Vendor switch
        {
            GpuVendor.Nvidia => Color.FromArgb(118, 185, 0),
            GpuVendor.Amd => Color.FromArgb(237, 28, 36),
            GpuVendor.Intel => Color.FromArgb(0, 113, 197),
            _ => Theme.Accent,
        };
        var head = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 0) };
        var title = Theme.Row(Theme.Label(gpu.Name, Theme.BodyBold), new Pill(gpu.VendorName, tint));
        title.Margin = new Padding(0);
        text.Controls.Add(title);
        text.Controls.Add(Theme.Label($"Installed driver: {gpu.FriendlyVersion}" +
                                      (gpu.DriverDate is { } d ? $"  ·  {d:d}" : "") +
                                      $"  ·  Windows version {gpu.WindowsDriverVersion}", Theme.Body, Theme.Muted));
        head.Controls.Add(new IconTile("\uE7F4", tint) { Margin = new Padding(0, 0, 14, 0) }, 0, 0);
        head.Controls.Add(text, 1, 0);
        return head;
    }

    private async Task CheckNvidia(GpuInfo gpu)
    {
        await Run("Checking NVIDIA drivers", async (log, ct) =>
        {
            var latest = await DriverService.GetLatestNvidiaAsync(gpu, log, ct);
            _latest[gpu.PnpId] = latest;
            log(latest is null ? "Could not find a driver for this GPU." : $"Latest Game Ready driver: {latest.Version}");
        });
        Render();
    }

    private async Task NvidiaCleanInstall(GpuInfo gpu)
    {
        if (!_latest.TryGetValue(gpu.PnpId, out var latest) || latest is null)
        {
            await CheckNvidia(gpu);
            if (!_latest.TryGetValue(gpu.PnpId, out latest) || latest is null)
            {
                Info("The latest driver could not be found automatically. Download it from NVIDIA's website and use 'Clean install from file'.");
                return;
            }
        }

        if (!ConfirmDanger($"Clean install NVIDIA driver {latest.Version}?\n\n" +
                           "1. A restore point is created\n2. The driver is downloaded from nvidia.com\n3. The new driver is installed silently with a clean profile, replacing the old one\n\n" +
                           "The screen will flicker and NVIDIA Control Panel settings are reset. Close games and video apps first."))
            return;

        DriverService.InstallOutcome? outcome = null;
        var ok = await _runner.RunAsync("NVIDIA clean install", async (log, progress, ct) =>
        {
            var file = await DriverService.DownloadAsync(latest.DownloadUrl, log, progress, ct);
            try
            {
                outcome = await DriverService.CleanInstallAsync(GpuVendor.Nvidia, file, requireVendorSignature: true, latest.Version, AskFromWork, log, ct);
            }
            finally
            {
                // The package is ~700 MB; don't leave it in ProgramData after installing.
                try { Directory.Delete(Path.GetDirectoryName(file)!, recursive: true); } catch { }
            }
        });
        _gpus = DriverService.GetGpus();
        _latest.Clear();
        Render();
        ShowOutcome(ok, outcome);
    }

    /// <summary>Says whether the driver really ended up installed, then offers the restart.</summary>
    private void ShowOutcome(bool ran, DriverService.InstallOutcome? outcome)
    {
        // Only when the install actually ran (not cancelled, failed or refused because busy).
        if (!ran || outcome is null) return;
        Localization.Loc.Show(this, outcome.Message, "WinSolve", MessageBoxButtons.OK,
            outcome.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        // The older versions are the rollback: their cleanup is offered only after a restart.
        if (outcome.Success) OldDriversDialog.OfferAfterUpdate();
        AskReboot(this, "Restart now to finish the driver installation?");
    }

    private async Task CleanInstallFromFile(GpuInfo gpu)
    {
        if (Pickers.PickersBlocked(this)) return;
        using var dlg = new OpenFileDialog
        {
            Title = Localization.Loc.T($"Select the {gpu.VendorName} driver installer"),
            Filter = Localization.Loc.T("Driver installer (*.exe)|*.exe"),
            InitialDirectory = InteractiveUser.Downloads,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        // Checked again on the protected copy before anything is removed.
        if (DriverService.InstallerProblem(gpu.Vendor, dlg.FileName) is { } problem)
        {
            Localization.Loc.Show(this, problem, "WinSolve", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var signed = DriverService.HasValidSignature(dlg.FileName, gpu.Vendor, out var signer);
        if (!signed && InteractiveUser.IsDifferent)
        {
            // Another account's administrator password was typed: never run an unverified file with it.
            Localization.Loc.Show(this, $"This file is not digitally signed by {gpu.VendorName} (signer: {signer ?? "none"}).\n\n" +
                "WinSolve is running with another account's administrator password, so it only installs drivers signed by the manufacturer. Download the installer from the manufacturer's website and try again.",
                "WinSolve", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!signed && !ConfirmDanger($"This file is not digitally signed by {gpu.VendorName} (signer: {signer ?? "none"}).\n\n" +
                                      "It will run with administrator rights. Only continue if you are sure it is a genuine driver installer. Continue anyway?"))
            return;

        var how = gpu.Vendor == GpuVendor.Nvidia
            ? Localization.Loc.T("The NVIDIA installer runs silently with a clean install, replacing the current driver. The screen will flicker.")
            : Localization.Loc.T("1. A restore point is created") + "\n" +
              Localization.Loc.T(@"2. A copy of the driver is saved in ProgramData\WinSolve\DriverBackup") + "\n" +
              Localization.Loc.T($"3. Every installed {gpu.VendorName} display driver is removed, then the installer opens. The screen will flicker and may change resolution until the new driver is installed.") + "\n\n" +
              Localization.Loc.T("If the new driver doesn't end up active, WinSolve puts the saved driver back.") + HybridNote(gpu.Vendor);
        if (!ConfirmDanger(Localization.Loc.T($"Clean install using {Path.GetFileName(dlg.FileName)}?") + "\n\n" + how))
            return;

        DriverService.InstallOutcome? outcome = null;
        var ok = await _runner.RunAsync($"{gpu.VendorName} clean install", async (log, _, ct) =>
            outcome = await DriverService.CleanInstallAsync(gpu.Vendor, dlg.FileName, requireVendorSignature: signed || InteractiveUser.IsDifferent, expectedVersion: null, AskFromWork, log, ct));
        _gpus = DriverService.GetGpus();
        Render();
        ShowOutcome(ok, outcome);
    }
}

/// <summary>
/// Lets the user pick which Windows Update driver updates to install. Recommended ones are
/// checked; optional ones (that Windows wouldn't install by itself) are not.
/// </summary>
internal sealed class DriverUpdatesDialog : ToolDialog
{
    private readonly DataGridView _grid = Grid(("title", "Driver update", 300), ("size", "Size", 80), ("kind", "Type", 90));

    public List<DriverService.PendingDriverUpdate> Selected { get; private set; } = [];

    public DriverUpdatesDialog(List<DriverService.PendingDriverUpdate> updates) : base("", "Driver updates",
        "Choose the driver updates to install. Optional updates are not checked: install them only if you have a problem they fix. A restore point is created first.", new Size(820, 520))
    {
        var check = new DataGridViewCheckBoxColumn { Name = "install", HeaderText = "Install", FillWeight = 40 };
        _grid.Columns.Insert(0, check);
        _grid.ReadOnly = false;
        foreach (DataGridViewColumn c in _grid.Columns) c.ReadOnly = c != check;
        foreach (var u in updates)
            _grid.Rows[_grid.Rows.Add(!u.Optional, u.Title, u.Size > 0 ? Format.Bytes(u.Size) : "", u.Optional ? Localization.Loc.T("Optional") : Localization.Loc.T("Recommended"))].Tag = u;
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };

        AddRow(Theme.InCard(_grid), fill: true);
        Buttons.Controls.Add(Theme.Button("Install selected", (_, _) =>
        {
            _grid.EndEdit();
            Selected = _grid.Rows.Cast<DataGridViewRow>()
                .Where(r => r.Cells[0].Value is true).Select(r => r.Tag).OfType<DriverService.PendingDriverUpdate>().ToList();
            if (Selected.Count == 0) { Tell("Select at least one driver update."); return; }
            DialogResult = DialogResult.OK;
            Close();
        }, primary: true, glyph: ""));
        Status.Text = string.Format(Localization.Loc.T("{0} driver update(s) available."), updates.Count);
    }
}
