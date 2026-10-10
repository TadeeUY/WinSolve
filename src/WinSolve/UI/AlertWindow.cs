using System.Media;
using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>What a notification shows and the actions it offers.</summary>
public sealed class ToastOptions
{
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public IssueSeverity Severity { get; init; } = IssueSeverity.Info;
    /// <summary>Icon (Segoe Fluent Icons); defaults to one matching the severity.</summary>
    public string? Glyph { get; init; }
    public string? PrimaryText { get; init; }
    public Action? Primary { get; init; }
    public string SecondaryText { get; init; } = "Dismiss";
    /// <summary>Clicking the text (or "Open WinSolve" in the menu) runs this.</summary>
    public Action? Open { get; init; }
    /// <summary>Alert kind that "Don't show alerts like this" mutes; null hides that option.</summary>
    public string? MuteKind { get; init; }
    /// <summary>Closes by itself after this many seconds (0 = stays until closed). Paused while hovered.</summary>
    public int AutoCloseSeconds { get; init; } = 12;
    public bool Sound { get; init; } = true;
}

/// <summary>Windows 11 style notification in the bottom-right corner.</summary>
public sealed class Toast : Form
{
    private static readonly List<Toast> Open = [];
    private const int MaxVisible = 3;
    private const int ToastWidth = 380;

    private readonly ToastOptions _o;
    private readonly Color _tint;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private int _targetTop, _targetLeft;
    private long _remainingMs;
    private long _lastTick;
    private bool _closing;
    private Task? _slide;

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

    private static readonly Color Surface = Theme.IsLight ? Color.FromArgb(252, 252, 252) : Color.FromArgb(44, 44, 44);
    private static readonly Color Edge = Theme.IsLight ? Color.FromArgb(214, 214, 214) : Color.FromArgb(70, 70, 70);

    private Toast(ToastOptions o)
    {
        _o = o;
        _tint = o.Severity switch
        {
            IssueSeverity.Critical => Theme.Bad,
            IssueSeverity.Warning => Theme.Warn,
            _ => Theme.Accent,
        };
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Surface;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;

        var scale = DeviceDpi / 96f;
        int S(int v) => (int)Math.Round(v * scale);
        Width = S(ToastWidth);

        // ── Header: app icon, name, menu and close buttons ──
        var appIcon = new PictureBox
        {
            Image = Theme.AppIcon(16).ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(S(14), S(12), S(16), S(16)),
            BackColor = Surface,
        };
        var appName = new Label
        {
            Text = "WinSolve",
            Font = Theme.Small,
            ForeColor = Theme.Muted,
            AutoSize = true,
            UseMnemonic = false,
            BackColor = Surface,
            Location = new Point(S(36), S(12)),
        };
        var close = new ToastIconButton("\uE711", Surface) { Bounds = new Rectangle(Width - S(40), S(6), S(30), S(28)) };
        close.Click += (_, _) => CloseAnimated();
        var more = new ToastIconButton("\uE712", Surface) { Bounds = new Rectangle(Width - S(72), S(6), S(30), S(28)) };
        more.Click += (_, _) => ShowMenu(more);
        Controls.AddRange([appIcon, appName, more, close]);

        // ── Body: severity icon, title and text ──
        var textLeft = S(70);
        var textWidth = Width - textLeft - S(16);
        var icon = new SeverityIcon(o.Glyph ?? DefaultGlyph(o.Severity), _tint, Surface) { Bounds = new Rectangle(S(16), S(44), S(40), S(40)) };
        var title = new Label
        {
            Text = Localization.Loc.T(o.Title),
            Font = Theme.BodyBold,
            ForeColor = Theme.Text,
            UseMnemonic = false,
            BackColor = Surface,
            AutoSize = false,
        };
        var detail = new Label
        {
            Text = Localization.Loc.T(o.Detail),
            Font = Theme.Body,
            ForeColor = Theme.IsLight ? Color.FromArgb(60, 60, 60) : Color.FromArgb(200, 200, 200),
            UseMnemonic = false,
            BackColor = Surface,
            AutoSize = false,
            AutoEllipsis = true,
        };
        const TextFormatFlags wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        var titleH = TextRenderer.MeasureText(title.Text, title.Font, new Size(textWidth, int.MaxValue), wrap).Height;
        var lineH = TextRenderer.MeasureText("Ag", detail.Font).Height;
        var detailH = Math.Min(TextRenderer.MeasureText(detail.Text, detail.Font, new Size(textWidth, int.MaxValue), wrap).Height, lineH * 4);
        title.Bounds = new Rectangle(textLeft, S(44), textWidth, titleH);
        detail.Bounds = new Rectangle(textLeft, title.Bottom + S(3), textWidth, detailH);
        Controls.AddRange([icon, title, detail]);
        if (o.Open is not null)
        {
            foreach (var c in new Control[] { icon, title, detail })
            {
                c.Cursor = Cursors.Hand;
                c.Click += (_, _) => { o.Open(); CloseAnimated(); };
            }
        }

        // ── Actions: equal-width buttons, like Windows notifications ──
        var y = Math.Max(icon.Bottom, detail.Bottom) + S(14);
        var buttons = new List<FlatBtn>();
        if (o.PrimaryText is not null && o.Primary is not null)
            buttons.Add(Theme.Button(o.PrimaryText, (_, _) => { o.Primary(); CloseAnimated(); }, primary: true));
        buttons.Add(Theme.Button(o.SecondaryText, (_, _) => CloseAnimated()));
        var gap = S(8);
        var bw = (Width - S(32) - gap * (buttons.Count - 1)) / buttons.Count;
        for (int i = 0; i < buttons.Count; i++)
        {
            var b = buttons[i];
            b.AutoSize = false;
            b.MinimumSize = Size.Empty;
            b.Margin = Padding.Empty;
            b.Bounds = new Rectangle(S(16) + i * (bw + gap), y, bw, S(34));
            Controls.Add(b);
        }
        Height = y + S(34) + S(16);

        _remainingMs = o.AutoCloseSeconds * 1000L;
        _timer.Tick += (_, _) => Tick();
        HandleCreated += (_, _) => Theme.StyleWindow(this);
        Shown += (_, _) => { _lastTick = Environment.TickCount64; if (_remainingMs > 0) _timer.Start(); };
        FormClosed += (_, _) => { _timer.Dispose(); Open.Remove(this); Relayout(); };
    }

    private static string DefaultGlyph(IssueSeverity s) => s switch
    {
        IssueSeverity.Critical => "\uEA39",
        IssueSeverity.Warning => "\uE7BA",
        _ => "\uE946",
    };

    private void ShowMenu(Control anchor)
    {
        var menu = Menus.Create();
        if (_o.Open is not null)
            menu.Items.Add(Localization.Loc.T("Open WinSolve"), null, (_, _) => { _o.Open(); CloseAnimated(); });
        if (_o.MuteKind is { } kind)
            menu.Items.Add(Localization.Loc.T("Don't show alerts like this"), null, (_, _) =>
            {
                AppSettings.Current.MutedAlerts.Add(kind);
                AppSettings.Current.Save();
                CloseAnimated();
            });
        menu.Items.Add(Localization.Loc.T("Dismiss"), null, (_, _) => CloseAnimated());
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void Tick()
    {
        var now = Environment.TickCount64;
        var elapsed = now - _lastTick;
        _lastTick = now;
        // Pause while the pointer is over the notification so it doesn't vanish while being read.
        if (Bounds.Contains(Cursor.Position)) return;
        _remainingMs -= elapsed;
        Invalidate(new Rectangle(0, Height - 4, Width, 4));
        if (_remainingMs <= 0)
        {
            _timer.Stop();
            CloseAnimated();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using (var pen = new Pen(Edge)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        // Severity accent on the left edge.
        using (var b = new SolidBrush(_tint)) g.FillRectangle(b, 0, 0, 3, Height);
        // Time left before it closes by itself.
        if (_o.AutoCloseSeconds > 0 && !_closing)
        {
            var frac = Math.Clamp(_remainingMs / (_o.AutoCloseSeconds * 1000.0), 0, 1);
            using var p = new SolidBrush(Color.FromArgb(150, _tint));
            g.FillRectangle(p, 3, Height - 3, (int)((Width - 4) * frac), 2);
        }
    }

    public static void Show(ToastOptions options)
    {
        if (Open.Count >= MaxVisible) Open[0].CloseAnimated();

        var w = new Toast(options);
        Open.Add(w);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        w.Opacity = 0;
        w.Left = area.Right;
        w.Top = area.Bottom - w.Height - 12;
        w.Show();
        Relayout();
        if (options.Sound)
        {
            if (options.Severity == IssueSeverity.Critical) SystemSounds.Hand.Play();
            else SystemSounds.Asterisk.Play();
        }
    }

    /// <summary>Stacks open notifications from the bottom; new ones slide in from the right.</summary>
    private static void Relayout()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var y = area.Bottom - 12;
        for (int i = Open.Count - 1; i >= 0; i--)
        {
            var w = Open[i];
            if (w._closing) continue;
            y -= w.Height;
            w._targetTop = y;
            w._targetLeft = area.Right - w.Width - 12;
            w._slide = w.SlideAsync();
            y -= 10;
        }
    }

    private async Task SlideAsync()
    {
        var startTop = Top;
        var startLeft = Left;
        var startOpacity = Opacity;
        var animate = AppSettings.Current.Animations;
        var began = Environment.TickCount64;
        const double ms = 280;
        while (!IsDisposed && !_closing)
        {
            var t = animate ? Math.Clamp((Environment.TickCount64 - began) / ms, 0, 1) : 1;
            var e = Ease.OutCubic(t);
            Top = (int)(startTop + (_targetTop - startTop) * e);
            Left = (int)(startLeft + (_targetLeft - startLeft) * e);
            Opacity = startOpacity + (1 - startOpacity) * e;
            if (t >= 1) break;
            await Task.Delay(10);
        }
    }

    private async void CloseAnimated()
    {
        if (_closing || IsDisposed) return;
        _closing = true;
        _timer.Stop();
        Open.Remove(this);
        Relayout();
        if (AppSettings.Current.Animations)
        {
            var startLeft = Left;
            var startOpacity = Opacity;
            var began = Environment.TickCount64;
            while (!IsDisposed)
            {
                var t = Math.Clamp((Environment.TickCount64 - began) / 200.0, 0, 1);
                var e = Ease.InCubic(t);
                Left = startLeft + (int)(60 * e);
                Opacity = startOpacity * (1 - e);
                if (t >= 1) break;
                await Task.Delay(10);
            }
        }
        if (!IsDisposed) Close();
    }

    /// <summary>Small borderless icon button for the header (menu, close).</summary>
    private sealed class ToastIconButton : Control
    {
        private readonly string _glyph;
        private readonly Color _surface;
        private double _hoverT;

        public ToastIconButton(string glyph, Color surface)
        {
            _glyph = glyph;
            _surface = surface;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnMouseEnter(EventArgs e) { Animator.Animate(this, () => _hoverT, 1, v => { _hoverT = v; Invalidate(); }, 100); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { Animator.Animate(this, () => _hoverT, 0, v => { _hoverT = v; Invalidate(); }, 100); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(_surface);
            if (_hoverT > 0.01)
            {
                using var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 4);
                using var b = new SolidBrush(Animator.Blend(_surface, Theme.IsLight ? Color.FromArgb(234, 234, 234) : Color.FromArgb(62, 62, 62), _hoverT));
                g.FillPath(b, path);
            }
            TextRenderer.DrawText(g, _glyph, Theme.IconsSmall, ClientRectangle, Animator.Blend(Theme.Muted, Theme.Text, _hoverT),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Round tinted badge with the severity glyph.</summary>
    private sealed class SeverityIcon : Control
    {
        private readonly string _glyph;
        private readonly Color _tint, _surface;

        public SeverityIcon(string glyph, Color tint, Color surface)
        {
            _glyph = glyph;
            _tint = tint;
            _surface = surface;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(_surface);
            using (var b = new SolidBrush(Color.FromArgb(50, _tint))) g.FillEllipse(b, 0, 0, Width - 1, Height - 1);
            TextRenderer.DrawText(g, _glyph, Theme.Icons, ClientRectangle, _tint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>Error alerts from <see cref="ErrorMonitor"/>, shown as notifications.</summary>
public static class AlertWindow
{
    public static void Show(Alert alert, Action<Alert> fix, Action open) => Toast.Show(new ToastOptions
    {
        Title = alert.Title,
        Detail = alert.Detail,
        Severity = alert.Severity,
        PrimaryText = alert.CanFix ? "Fix" : null,
        Primary = alert.CanFix ? () => fix(alert) : null,
        SecondaryText = "Dismiss",
        Open = open,
        MuteKind = alert.Kind,
        // Critical problems stay on screen until the user decides.
        AutoCloseSeconds = alert.Severity == IssueSeverity.Critical ? 0 : 15,
    });
}

public static class Ease
{
    public static double OutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    public static double InCubic(double t) => t * t * t;
    public static double InOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
}
