using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinSolve.Setup
{
    /// <summary>Installer colors: WinSolve's palette, light or dark like Windows.</summary>
    internal static class Ui
    {
        public static readonly bool Light = DetectLight();

        private static bool DetectLight()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
            }
            catch { return false; }
        }

        private static Color G(int dark, int light) { var v = Light ? light : dark; return Color.FromArgb(v, v, v); }

        public static readonly Color Background = G(32, 243);
        public static readonly Color Card = G(43, 251);
        public static readonly Color CardHover = G(50, 246);
        public static readonly Color Border = G(58, 222);
        public static readonly Color BorderHover = G(80, 190);
        public static readonly Color Text = G(242, 27);
        public static readonly Color Muted = G(160, 96);
        public static readonly Color Track = G(62, 220);
        public static readonly Color Control = G(55, 253);
        public static readonly Color ControlHover = G(62, 243);
        public static readonly Color Accent = Color.FromArgb(0, 103, 192);
        public static readonly Color AccentHover = Color.FromArgb(24, 121, 207);
        public static readonly Color SelectedFill = Light ? Color.FromArgb(222, 235, 250) : Color.FromArgb(36, 52, 72);
        public static readonly Color Good = Light ? Color.FromArgb(15, 123, 15) : Color.FromArgb(108, 203, 95);
        public static readonly Color Bad = Light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 99, 97);

        public static readonly Font Body = new Font("Segoe UI", 9.5f);
        public static readonly Font BodyBold = new Font("Segoe UI Semibold", 9.5f);
        public static readonly Font Small = new Font("Segoe UI", 8.5f);
        public static readonly Font Title = new Font("Segoe UI Semibold", 20f);
        public static readonly Font Icons = new Font(FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets", 16f);
        public static readonly Font IconsSmall = new Font(Icons.FontFamily, 10f);

        private static bool FontExists(string name)
        {
            using (var f = new Font(name, 9f)) return f.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Blend(Color a, Color b, float t) => Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

        public static void Clear(Graphics g, Control c)
        {
            var p = c.Parent;
            while (p != null && p.BackColor.A < 255) p = p.Parent;
            g.Clear(p?.BackColor ?? Background);
        }
    }

    /// <summary>Base for the painted controls: double buffered, with a smooth hover fade.</summary>
    internal abstract class PaintedControl : Control
    {
        private readonly Timer _timer = new Timer { Interval = 15 };
        protected float HoverT;
        private bool _hover;

        protected PaintedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            _timer.Tick += (s, e) =>
            {
                HoverT = Math.Max(0, Math.Min(1, HoverT + (_hover ? 0.15f : -0.15f)));
                if (HoverT <= 0 || HoverT >= 1) _timer.Stop();
                Invalidate();
            };
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; _timer.Start(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _timer.Start(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Rounded button; the primary one uses the accent color.</summary>
    internal sealed class FlatButton : PaintedControl
    {
        private readonly bool _primary;

        public FlatButton(bool primary)
        {
            _primary = primary;
            Size = new Size(116, 36);
            Font = Ui.BodyBold;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.Clear(g, this);
            var fill = _primary ? Ui.Blend(Ui.Accent, Ui.AccentHover, HoverT) : Ui.Blend(Ui.Control, Ui.ControlHover, HoverT);
            if (!Enabled) fill = Ui.Blend(fill, Ui.Background, 0.5f);
            using (var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 6))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (!_primary) using (var pen = new Pen(Ui.Border)) g.DrawPath(pen, path);
            }
            var color = _primary ? Color.White : Ui.Text;
            if (!Enabled) color = Ui.Blend(color, fill, 0.5f);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Selectable card with an icon, a title and a detail line (one of a group).</summary>
    internal sealed class ChoiceCard : PaintedControl
    {
        private bool _selected;
        public string Glyph { get; set; }
        public string Detail { get; set; } = "";
        public event EventHandler Selected;

        public bool Checked
        {
            get => _selected;
            set { _selected = value; Invalidate(); }
        }

        protected override void OnClick(EventArgs e)
        {
            if (Enabled && !_selected) { Checked = true; Selected?.Invoke(this, EventArgs.Empty); }
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.Clear(g, this);
            var fill = _selected ? Ui.SelectedFill : Ui.Blend(Ui.Card, Ui.CardHover, HoverT);
            using (var path = Ui.Round(new RectangleF(1, 1, Width - 2, Height - 2), 8))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var pen = new Pen(_selected ? Ui.Accent : Ui.Blend(Ui.Border, Ui.BorderHover, HoverT), _selected ? 2f : 1f)) g.DrawPath(pen, path);
            }
            var faded = !Enabled ? 0.45f : 0f;

            // Icon in a tinted circle.
            var circle = new Rectangle(16, 16, 36, 36);
            using (var b = new SolidBrush(Color.FromArgb(_selected ? 70 : 40, Ui.Accent))) g.FillEllipse(b, circle);
            TextRenderer.DrawText(g, Glyph, Ui.Icons, circle, Ui.Light ? Ui.Accent : Color.FromArgb(150, 200, 255),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Radio mark in the corner.
            var mark = new Rectangle(Width - 30, 16, 16, 16);
            if (_selected)
            {
                using (var b = new SolidBrush(Ui.Accent)) g.FillEllipse(b, mark);
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, Rectangle.Inflate(mark, -5, -5));
            }
            else using (var pen = new Pen(Ui.BorderHover, 1.5f)) g.DrawEllipse(pen, mark);

            TextRenderer.DrawText(g, Text, Ui.BodyBold, new Rectangle(16, 62, Width - 32, 20), Ui.Blend(Ui.Text, fill, faded),
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Detail, Ui.Small, new Rectangle(16, 84, Width - 28, Height - 90), Ui.Blend(Ui.Muted, fill, faded),
                TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.PathEllipsis);
        }
    }

    /// <summary>Windows 11 style switch with its label.</summary>
    internal sealed class Toggle : PaintedControl
    {
        private bool _checked;
        private float _pos;
        private readonly Timer _anim = new Timer { Interval = 15 };

        public Toggle()
        {
            Height = 36;
            Font = Ui.Body;
            _anim.Tick += (s, e) =>
            {
                var target = _checked ? 1f : 0f;
                _pos += Math.Sign(target - _pos) * 0.2f;
                if (Math.Abs(_pos - target) < 0.2f) { _pos = target; _anim.Stop(); }
                Invalidate();
            };
        }

        public bool Checked
        {
            get => _checked;
            set { _checked = value; if (IsHandleCreated) _anim.Start(); else _pos = value ? 1 : 0; Invalidate(); }
        }

        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.Clear(g, this);
            var sw = new RectangleF(Width - 46, Height / 2f - 10, 40, 20);
            var on = Ui.Blend(Ui.Accent, Ui.AccentHover, HoverT);
            var off = Ui.Blend(Ui.Card, Ui.CardHover, HoverT);
            using (var path = Ui.Round(sw, 10))
            {
                using (var b = new SolidBrush(Ui.Blend(off, on, _pos))) g.FillPath(b, path);
                using (var pen = new Pen(Color.FromArgb((int)(255 * (1 - _pos)), Ui.Muted))) g.DrawPath(pen, path);
            }
            var knob = 12 + 2 * _pos;
            var x = sw.X + 4 + _pos * (sw.Width - 8 - knob);
            using (var b = new SolidBrush(Ui.Blend(Ui.Muted, Color.White, _pos))) g.FillEllipse(b, x, sw.Y + (sw.Height - knob) / 2, knob, knob);
            var color = Enabled ? Ui.Text : Ui.Muted;
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width - 56, Height), color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Two-option pill (EN | ES).</summary>
    internal sealed class PillSwitch : PaintedControl
    {
        private readonly string[] _items;
        private int _index;
        public event EventHandler Changed;

        public PillSwitch(params string[] items)
        {
            _items = items;
            Size = new Size(96, 30);
            Font = Ui.BodyBold;
        }

        public int SelectedIndex
        {
            get => _index;
            set { _index = value; Invalidate(); }
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            var i = Math.Min(_items.Length - 1, e.X * _items.Length / Math.Max(1, Width));
            if (Enabled && i != _index) { SelectedIndex = i; Changed?.Invoke(this, EventArgs.Empty); }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.Clear(g, this);
            using (var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Height / 2f - 1))
            {
                using (var b = new SolidBrush(Ui.Blend(Ui.Card, Ui.CardHover, HoverT))) g.FillPath(b, path);
                using (var pen = new Pen(Ui.Border)) g.DrawPath(pen, path);
            }
            var w = (Width - 6f) / _items.Length;
            using (var path = Ui.Round(new RectangleF(3 + _index * w, 3, w, Height - 6), (Height - 6) / 2f - 1))
            using (var b = new SolidBrush(Ui.Accent)) g.FillPath(b, path);
            for (int i = 0; i < _items.Length; i++)
                TextRenderer.DrawText(g, _items[i], Font, new Rectangle((int)(3 + i * w), 0, (int)w, Height),
                    i == _index ? Color.White : Ui.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Thin rounded progress bar; a negative value shows a moving segment.</summary>
    internal sealed class ProgressLine : Control
    {
        private float _value = -1;
        private float _phase;
        private readonly Timer _timer = new Timer { Interval = 16 };

        public ProgressLine()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 4;
            _timer.Tick += (s, e) => { _phase = (_phase + 0.012f) % 1.4f; Invalidate(); };
        }

        /// <summary>0..1, or -1 while the length of the step is unknown.</summary>
        public float Value
        {
            get => _value;
            set { _value = value; if (value < 0 && Visible) _timer.Start(); else _timer.Stop(); Invalidate(); }
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (Visible && _value < 0) _timer.Start(); else _timer.Stop();
            base.OnVisibleChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Ui.Clear(g, this);
            using (var path = Ui.Round(new RectangleF(0, 0, Width, Height), Height / 2f))
            using (var b = new SolidBrush(Ui.Track)) g.FillPath(b, path);
            float x1, x2;
            if (_value < 0) { x1 = (_phase - 0.4f) * Width; x2 = x1 + 0.4f * Width; }
            else { x1 = 0; x2 = _value * Width; }
            x1 = Math.Max(0, x1);
            x2 = Math.Min(Width, x2);
            if (x2 - x1 < 1) return;
            using (var path = Ui.Round(new RectangleF(x1, 0, Math.Max(Height, x2 - x1), Height), Height / 2f))
            using (var b = new SolidBrush(Ui.Accent)) g.FillPath(b, path);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Rounded card used as a container.</summary>
    internal sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Ui.Card;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Ui.Background);
            using (var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8))
            {
                using (var b = new SolidBrush(Ui.Card)) g.FillPath(b, path);
                using (var pen = new Pen(Ui.Border)) g.DrawPath(pen, path);
            }
        }
    }
}
