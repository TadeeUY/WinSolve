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
                Theme.Button("Install all driver updates", async (_, _) =>
                {
                    if (!Confirm("Download and install every pending driver update from Windows Update?")) return;
                    await Run("Installing driver updates", DriverService.InstallWindowsUpdateDriversAsync);
                }),
                Theme.Button("Repair devices with errors", async (_, _) => await Run("Repairing devices", DriverService.RepairProblemDevicesAsync)),
                Theme.Button("Back up drivers", async (_, _) => await Run("Backing up drivers", async (log, ct) =>
                {
                    var folder = await DriverService.BackupDriversAsync(log, ct);
                    ProcessRunner.ShellOpen(folder);
                }))));

        AddRow(new Stack(scroll: true).Add(_gpuCard, other), fill: true);
        AddRow(_runner, height: 230);
    }

    private Task Run(string title, Func<Action<string>, CancellationToken, Task> work)
        => _runner.RunAsync(title, (log, _, ct) => work(log, ct));

    public override async void OnShown()
    {
        if (_gpus.Count == 0)
        {
            _gpus = await Task.Run(DriverService.GetGpus);
            Render();
        }
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
            row.Controls.Add(Theme.Button("Remove driver only", async (_, _) =>
            {
                if (!ConfirmDanger($"Remove every {gpu.VendorName} graphics driver package? The display will fall back to the basic Microsoft driver until you install a new one.")) return;
                await Run("Removing graphics driver", (log, ct) => DriverService.RemoveDisplayDriversAsync(gpu.Vendor, log, ct));
            }));
            _gpuCard.Add(row);

            if (gpu.Vendor == GpuVendor.Amd)
                _gpuCard.Add(Theme.Paragraph("AMD does not publish a public download API. Download the Adrenalin installer from AMD's website, then use 'Clean install from file'. WinSolve removes the old driver first and then opens the installer, where you can pick 'Factory Reset'."));
        }
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
                           "1. A restore point is created\n2. The driver is downloaded from nvidia.com\n3. Every installed NVIDIA display driver is removed\n4. The new driver is installed silently with a clean profile\n\n" +
                           "The screen will flicker and NVIDIA Control Panel settings are reset. Close games and video apps first."))
            return;

        await _runner.RunAsync("NVIDIA clean install", async (log, progress, ct) =>
        {
            var file = await DriverService.DownloadAsync(latest.DownloadUrl, log, progress, ct);
            await DriverService.CleanInstallAsync(GpuVendor.Nvidia, file, requireVendorSignature: true, log, ct);
        });
        _gpus = DriverService.GetGpus();
        _latest.Clear();
        Render();
        AskReboot(this, "Restart now to finish the driver installation?");
    }

    private async Task CleanInstallFromFile(GpuInfo gpu)
    {
        using var dlg = new OpenFileDialog
        {
            Title = Localization.Loc.T($"Select the {gpu.VendorName} driver installer"),
            Filter = Localization.Loc.T("Driver installer (*.exe)|*.exe"),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        var signed = DriverService.HasValidSignature(dlg.FileName, gpu.Vendor, out var signer);
        if (!signed && !ConfirmDanger($"This file is not digitally signed by {gpu.VendorName} (signer: {signer ?? "none"}).\n\n" +
                                      "It will run with administrator rights. Only continue if you are sure it is a genuine driver installer. Continue anyway?"))
            return;

        if (!ConfirmDanger($"Clean install using {Path.GetFileName(dlg.FileName)}?\n\nEvery installed {gpu.VendorName} display driver is removed first, then the installer runs. The screen will flicker."))
            return;

        await _runner.RunAsync($"{gpu.VendorName} clean install", (log, _, ct) => DriverService.CleanInstallAsync(gpu.Vendor, dlg.FileName, requireVendorSignature: signed, log, ct));
        _gpus = DriverService.GetGpus();
        Render();
        AskReboot(this, "Restart now to finish the driver installation?");
    }
}
