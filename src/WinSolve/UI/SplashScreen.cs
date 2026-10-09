using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WinSolve.UI;

/// <summary>
/// Startup screen. It runs on its own UI thread, so it keeps animating while the main window
/// is being built and the first page loads; the main window only appears once it is fully drawn.
/// </summary>
public static class SplashScreen
{
    private const int MinimumVisibleMs = 900;

    private static SplashForm? _form;
    private static long _shownAt;

    /// <summary>Shows the splash screen. Call on the main thread before creating the main window.</summary>
    public static void Show(string status)
    {
        if (_form is not null) return;
        var accent = Theme.Accent;
        var version = Localization.Loc.T($"Version {Application.ProductVersion.Split('+')[0]}");
        var subtitle = Localization.Loc.T("PC health & maintenance");
        status = Localization.Loc.T(status);
        Icon? icon = null;
        try
        {
            using var stream = typeof(SplashScreen).Assembly.GetManifestResourceStream("WinSolve.ico");
            if (stream is not null) icon = new Icon(stream, 256, 256);
        }
        catch { }

        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _form = new SplashForm(accent, icon, subtitle, version, status);
            _form.Shown += (_, _) => ready.Set();
            Application.Run(_form);
        })
        {
            IsBackground = true,
            Name = "Splash",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(3000);
        _shownAt = Environment.TickCount64;
    }

    public static void SetStatus(string status)
    {
        var form = _form;
        if (form is null) return;
        var text = Localization.Loc.T(status);
        try { form.BeginInvoke(() => form.Status = text); } catch { }
    }

    /// <summary>
    /// Waits until the splash has been visible for a moment (so it doesn't just flicker), then
    /// fades it out on its own thread while the caller fades the main window in.
    /// </summary>
    public static async Task CloseAsync()
    {
        var form = _form;
        if (form is null) return;
        _form = null;
        var wait = MinimumVisibleMs - (int)(Environment.TickCount64 - _shownAt);
        if (wait > 0) await Task.Delay(wait);
        try { form.BeginInvoke(form.FadeOutAndClose); } catch { }
    }

    private sealed class SplashForm : Form
    {
        private readonly Color _accent;
        private readonly Bitmap? _logo;
        private readonly string _subtitle;
        private readonly string _version;
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
        private readonly long _start = Environment.TickCount64;
        private readonly Font _titleFont = new("Segoe UI Semibold", 24f);
        private readonly Font _bodyFont = new("Segoe UI", 10f);
        private readonly Font _smallFont = new("Segoe UI", 8.5f);
        private string _status;
        private string _previousStatus = "";
        private long _statusChangedAt;
        private bool _closing;
        private long _closeStart;

        private static readonly Color Background = Color.FromArgb(28, 28, 28);
        private static readonly Color Foreground = Color.FromArgb(240, 240, 240);
        private static readonly Color Muted = Color.FromArgb(160, 160, 160);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public SplashForm(Color accent, Icon? icon, string subtitle, string version, string status)
        {
            _accent = accent;
            _logo = icon?.ToBitmap();
            _subtitle = subtitle;
            _version = version;
            _status = status;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            TopMost = true;
            Text = "WinSolve";
            Icon = icon;
            BackColor = Background;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            ClientSize = new Size(520, 320);
            Opacity = 0;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            _timer.Tick += (_, _) => Tick();
            HandleCreated += (_, _) =>
            {
                try
                {
                    int round = 2;
                    DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int)); // rounded corners (Windows 11)
                }
                catch { }
            };
            Shown += (_, _) => _timer.Start();
        }

        public string Status
        {
            get => _status;
            set
            {
                if (value == _status) return;
                _previousStatus = _status;
                _status = value;
                _statusChangedAt = Environment.TickCount64;
            }
        }

        // A shadow for the borderless window.
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        public void FadeOutAndClose()
        {
            if (_closing) return;
            _closing = true;
            _closeStart = Environment.TickCount64;
        }

        private void Tick()
        {
            var now = Environment.TickCount64;
            if (_closing)
            {
                var t = Math.Clamp((now - _closeStart) / 220.0, 0, 1);
                Opacity = 1 - Ease.InCubic(t);
                if (t >= 1)
                {
                    _timer.Stop();
                    Close();
                    return;
                }
            }
            else if (Opacity < 1)
            {
                Opacity = Ease.OutCubic(Math.Clamp((now - _start) / 200.0, 0, 1));
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Background);

            var w = ClientSize.Width;
            var h = ClientSize.Height;
            var elapsed = (Environment.TickCount64 - _start) / 1000.0;

            // Soft accent glow behind the logo that slowly breathes.
            var glow = 0.5 + 0.5 * Math.Sin(elapsed * 2.2);
            var glowRect = new RectangleF(w / 2f - 150, 6, 300, 220);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(glowRect);
                using var brush = new PathGradientBrush(path)
                {
                    CenterColor = Color.FromArgb((int)(40 + 26 * glow), _accent),
                    SurroundColors = [Color.FromArgb(0, _accent)],
                };
                g.FillPath(brush, path);
            }

            // Logo rises into place as the window appears.
            var intro = Ease.OutCubic(Math.Clamp(elapsed / 0.5, 0, 1));
            var logoSize = 76;
            var logoY = 52 + (int)(14 * (1 - intro));
            if (_logo is not null)
                g.DrawImage(_logo, new Rectangle(w / 2 - logoSize / 2, logoY, logoSize, logoSize));

            var center = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, "WinSolve", _titleFont, new Rectangle(0, 140, w, 44), Blend(Background, Foreground, intro), center);
            TextRenderer.DrawText(g, _subtitle, _bodyFont, new Rectangle(0, 184, w, 22), Blend(Background, Muted, intro), center);

            // Windows 11 style indeterminate progress bar.
            var barW = 240;
            var bar = new Rectangle(w / 2 - barW / 2, 236, barW, 3);
            using (var track = new SolidBrush(Color.FromArgb(50, 50, 50))) g.FillRectangle(track, bar);
            const double segLen = 0.38;
            var p = Ease.InOut(elapsed % 1.5 / 1.5) * (1 + segLen) - segLen;
            var x1 = bar.X + (int)(barW * Math.Clamp(p, 0, 1));
            var x2 = bar.X + (int)(barW * Math.Clamp(p + segLen, 0, 1));
            if (x2 > x1)
            {
                using var seg = new SolidBrush(_accent);
                using var segPath = Theme.RoundedRect(new Rectangle(x1, bar.Y, x2 - x1, bar.Height), 1);
                g.FillPath(seg, segPath);
            }

            // Status text cross-fades when it changes.
            var st = Math.Clamp((Environment.TickCount64 - _statusChangedAt) / 200.0, 0, 1);
            var statusRect = new Rectangle(0, 252, w, 20);
            if (st < 1 && _previousStatus.Length > 0)
                TextRenderer.DrawText(g, _previousStatus, _smallFont, statusRect, Blend(Background, Muted, 1 - st), center);
            TextRenderer.DrawText(g, _status, _smallFont, statusRect, Blend(Background, Muted, st * intro), center);

            TextRenderer.DrawText(g, _version, _smallFont, new Rectangle(16, h - 28, w / 2, 18), Color.FromArgb(110, 110, 110),
                TextFormatFlags.NoPrefix);

            using var border = new Pen(Color.FromArgb(56, 56, 56));
            g.DrawRectangle(border, 0, 0, w - 1, h - 1);
        }

        private static Color Blend(Color a, Color b, double t) => Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _logo?.Dispose();
                _titleFont.Dispose();
                _bodyFont.Dispose();
                _smallFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
