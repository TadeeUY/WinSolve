using System.Media;
using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>Notification card in the bottom-right corner with Fix and Close buttons.</summary>
public sealed class AlertWindow : Form
{
    private static readonly List<AlertWindow> Open = [];
    private const int MaxVisible = 3;

    private readonly Alert _alert;
    private readonly Action<Alert> _fix;
    private int _targetTop;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            cp.ClassStyle |= 0x00020000;           // CS_DROPSHADOW
            return cp;
        }
    }

    private AlertWindow(Alert alert, Action<Alert> fix)
    {
        _alert = alert;
        _fix = fix;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Width = 400;

        var card = new StackCard { Padding = new Padding(16, 12, 14, 10), Margin = new Padding(0), Dock = DockStyle.Top };
        var severity = alert.Severity switch
        {
            IssueSeverity.Critical => "Critical error",
            IssueSeverity.Warning => "Problem detected",
            _ => "Notice",
        };
        card.Add(
            Theme.Status($"WinSolve  ·  {severity}", Theme.For(alert.Severity)),
            Theme.Label(alert.Title, Theme.BodyBold),
            Theme.Paragraph(alert.Detail));

        var row = Theme.Row();
        if (alert.CanFix)
            row.Controls.Add(Theme.Button("Fix", (_, _) => { _fix(_alert); CloseAnimated(); }, primary: true));
        row.Controls.Add(Theme.Button("Close", (_, _) => CloseAnimated()));
        var mute = new LinkLabel
        {
            Text = "Don't show again",
            AutoSize = true,
            LinkColor = Theme.Muted,
            ActiveLinkColor = Theme.Text,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Font = Theme.Small,
            Margin = new Padding(6, 13, 0, 0),
        };
        mute.LinkClicked += (_, _) =>
        {
            AppSettings.Current.MutedAlerts.Add(_alert.Kind);
            AppSettings.Current.Save();
            CloseAnimated();
        };
        row.Controls.Add(mute);
        card.Add(row);
        Controls.Add(card);

        Load += (_, _) => Height = card.PreferredSize.Height;
        HandleCreated += (_, _) => Theme.StyleWindow(this);
        FormClosed += (_, _) => { Open.Remove(this); Relayout(); };
    }

    public static void Show(Alert alert, Action<Alert> fix)
    {
        if (Open.Count >= MaxVisible) Open[0].Close();

        var w = new AlertWindow(alert, fix);
        Open.Add(w);
        w.Opacity = 0;
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        w.Show();
        w.Left = area.Right - w.Width - 12;
        w.Top = area.Bottom;
        Relayout();
        if (alert.Severity == IssueSeverity.Critical) SystemSounds.Hand.Play(); else SystemSounds.Asterisk.Play();
    }

    /// <summary>Stacks open alerts from the bottom and slides them into place.</summary>
    private static void Relayout()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var y = area.Bottom - 12;
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var w = Open[i];
            y -= w.Height;
            w._targetTop = y;
            _ = w.SlideAsync();
            y -= 8;
        }
    }

    private async Task SlideAsync()
    {
        var startTop = Top;
        var startOpacity = Opacity;
        const int steps = 14;
        for (int i = 1; i <= steps && !IsDisposed; i++)
        {
            var t = Ease.OutCubic(i / (double)steps);
            Top = (int)(startTop + (_targetTop - startTop) * t);
            Opacity = startOpacity + (1 - startOpacity) * t;
            await Task.Delay(12);
        }
    }

    private async void CloseAnimated()
    {
        const int steps = 10;
        var startLeft = Left;
        for (int i = 1; i <= steps && !IsDisposed; i++)
        {
            var t = Ease.InCubic(i / (double)steps);
            Left = startLeft + (int)(48 * t);
            Opacity = 1 - t;
            await Task.Delay(12);
        }
        if (!IsDisposed) Close();
    }
}

public static class Ease
{
    public static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    public static double InCubic(double t) => t * t * t;
}
