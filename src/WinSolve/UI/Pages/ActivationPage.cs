using WinSolve.Services;

namespace WinSolve.UI.Pages;

public sealed class ActivationPage : Page
{
    private readonly StackCard _status = new();
    private readonly StackCard _actions = new();
    private readonly TaskRunnerView _runner = new();
    private readonly TextBox _key = Theme.TextBox("XXXXX-XXXXX-XXXXX-XXXXX-XXXXX");

    public override string Key => "activation";

    public ActivationPage() : base("Activation",
        "Check the Windows license and activate through official channels.")
    {
        _key.Width = 320;
        _key.CharacterCasing = CharacterCasing.Upper;
        _key.MaxLength = 29;

        AddRow(new Stack().Add(_status, _actions));
        AddRow(_runner, fill: true);
    }

    public override async void OnShown() => Render(await Task.Run(ActivationService.GetStatus));

    private void Render(ActivationStatus s)
    {
        _status.Body.Controls.Clear();
        _status.Add(
            StatusHeader(s),
            new Divider(),
            KeyValue("Status", s.StatusText),
            KeyValue("Edition", s.Product.Length > 0 ? s.Product : "—"),
            KeyValue("License channel", s.Channel.Length > 0 ? s.Channel : "—"),
            KeyValue("Installed key", $"XXXXX-XXXXX-XXXXX-XXXXX-{(s.PartialKey.Length > 0 ? s.PartialKey : "?????")}"));
        if (s.FirmwareKey is not null)
            _status.Add(KeyValue("Firmware (OEM) key", $"{s.FirmwareKeyEdition}, ending in {s.FirmwareKey[^5..]}"));

        _actions.Body.Controls.Clear();
        _actions.Add(Theme.Label("Activate Windows", Theme.H2));

        if (s.FirmwareKey is not null && !s.IsActivated)
        {
            _actions.Add(
                Theme.Paragraph("This PC shipped with a Windows license stored in its firmware. Activating with it is the quickest option."),
                Theme.Row(Theme.Button("Activate with firmware key", async (_, _) =>
                    await RunActivation(log => ActivationService.ActivateWithKeyAsync(s.FirmwareKey, log)), primary: true)),
                new Divider());
        }

        _actions.Add(
            Theme.Paragraph("Have a product key (from the box, a purchase email or your organization)? Enter it here:"),
            Theme.Row(_key, Theme.Button("Activate with this key", async (_, _) =>
                await RunActivation(log => ActivationService.ActivateWithKeyAsync(_key.Text, log)))),
            new Divider(),
            Theme.Paragraph("If Windows was activated before and you changed hardware (motherboard, drive), open the Activation troubleshooter in Settings and sign in with your Microsoft account to recover the digital license."),
            Theme.Row(
                Theme.Button("Retry online activation", async (_, _) => await RunActivation(log => ActivationService.ActivateOnlineAsync(log))),
                Theme.Button("Open Settings > Activation", (_, _) => ActivationService.OpenActivationSettings()),
                Theme.Button("Refresh", (_, _) => OnShown())));
    }

    private static Control StatusHeader(ActivationStatus s)
    {
        var color = s.IsActivated ? Theme.Good : Theme.Warn;
        var head = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 4) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var icon = new IconTile(s.IsActivated ? "\uE930" : "\uE7BA", color) { Margin = new Padding(0, 0, 14, 0) };
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 0) };
        text.Controls.Add(Theme.Label(s.IsActivated ? "Windows is activated" : "Windows is not activated", Theme.H2));
        text.Controls.Add(Theme.Label(s.Product.Length > 0 ? s.Product : "License status", Theme.Body, Theme.Muted));
        head.Controls.Add(icon, 0, 0);
        head.Controls.Add(text, 1, 0);
        return head;
    }

    private static Control KeyValue(string key, string value)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(Theme.Label(key, Theme.Body, Theme.Muted), 0, 0);
        row.Controls.Add(Theme.Label(value, Theme.Body), 1, 0);
        return row;
    }

    private async Task RunActivation(Func<Action<string>, Task<bool>> action)
    {
        await _runner.RunAsync("Activating Windows", async (log, _, _) => await action(log));
        OnShown();
    }
}
