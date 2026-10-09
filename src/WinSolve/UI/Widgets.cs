using System.Drawing.Drawing2D;
using WinSolve.Localization;

namespace WinSolve.UI;

/// <summary>Windows 11 style InfoBar: icon, title, message, actions and a close button.</summary>
public sealed class InfoBar : Card
{
    private readonly Label _title = Theme.Label("", Theme.BodyBold);
    private readonly Label _message = Theme.Label("", Theme.Body, Theme.Muted);
    private readonly FlowLayoutPanel _actions = new() { AutoSize = true, BackColor = Color.Transparent, WrapContents = false, Margin = new Padding(0) };
    private readonly ProgressLine _progress = new() { Dock = DockStyle.Bottom, Height = 3, Visible = false };

    public InfoBar()
    {
        Fill = Color.FromArgb(28, 45, 66);
        Padding = new Padding(48, 10, 10, 10);
        Margin = new Padding(0);
        Height = 56;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, BackColor = Color.Transparent, Margin = new Padding(0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _title.Anchor = _message.Anchor = AnchorStyles.Left;
        _title.Margin = new Padding(0, 0, 10, 0);
        _actions.Anchor = AnchorStyles.Right;

        var close = new Label
        {
            Text = "",
            Font = Theme.IconsSmall,
            ForeColor = Theme.Muted,
            AutoSize = true,
            Cursor = Cursors.Hand,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(8, 0, 4, 0),
        };
        close.Click += (_, _) => { SetShown(false); Closed?.Invoke(); };

        _actions.Controls.Add(close);
        layout.Controls.Add(_title, 0, 0);
        layout.Controls.Add(_message, 1, 0);
        layout.Controls.Add(_actions, 2, 0);
        Controls.Add(layout);
        Controls.Add(_progress);
    }

    public event Action? Closed;

    public void Show(string title, string message, params FlatBtn[] actions)
    {
        _title.Text = title;
        _message.Text = message;
        while (_actions.Controls.Count > 1) _actions.Controls.RemoveAt(0);
        for (int i = actions.Length - 1; i >= 0; i--)
        {
            actions[i].Margin = new Padding(6, 0, 0, 0);
            _actions.Controls.Add(actions[i]);
            _actions.Controls.SetChildIndex(actions[i], 0);
        }
        _progress.Visible = false;
        SetShown(true);
    }

    /// <summary>Shows or hides the bar together with its host panel.</summary>
    private void SetShown(bool shown)
    {
        Visible = shown;
        if (Parent is { } host) host.Visible = shown;
    }

    public void SetProgress(double? value)
    {
        _progress.Visible = value is not null;
        _progress.Indeterminate = value is < 0;
        if (value is >= 0) _progress.Value = value.Value;
    }

    public void SetMessage(string message) => _message.Text = message;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        // Info glyph in a filled circle, like Windows 11.
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new Rectangle(16, Height / 2 - 10, 20, 20);
        using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, r);
        TextRenderer.DrawText(g, "", Theme.IconsSmall, r, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>Circular gauge for the health score.</summary>
public sealed class ScoreRing : Control
{
    private int? _score;
    private double _shown;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };

    public ScoreRing()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(124, 124);
        BackColor = Theme.Card;
        _anim.Tick += (_, _) =>
        {
            var target = _score ?? 0;
            _shown += (target - _shown) * 0.15;
            if (Math.Abs(target - _shown) < 0.4) { _shown = target; _anim.Stop(); }
            Invalidate();
        };
    }

    /// <summary>0-100, or null while scanning.</summary>
    public int? Score
    {
        get => _score;
        set { _score = value; _anim.Start(); Invalidate(); }
    }

    private static Color ColorFor(double s) => s >= 85 ? Theme.Good : s >= 60 ? Theme.Warn : Theme.Bad;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const float thickness = 9f;
        var rect = new RectangleF(thickness / 2 + 1, thickness / 2 + 1, Width - thickness - 2, Height - thickness - 2);

        using (var track = new Pen(Theme.Border, thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(track, rect, 135, 270);

        if (_score is not null && _shown > 0.5)
        {
            using var pen = new Pen(ColorFor(_shown), thickness) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, rect, 135, (float)(270 * _shown / 100));
        }

        var text = _score is null ? "--" : ((int)Math.Round(_shown)).ToString();
        TextRenderer.DrawText(g, text, Theme.Big, new Rectangle(0, -6, Width, Height), Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, Loc.T("of 100"), Theme.Small, new Rectangle(0, Height / 2 + 14, Width, 18), Theme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anim.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Clickable card with a large icon, title and subtitle.</summary>
public sealed class ActionTile : Control
{
    private bool _hover;
    private bool _pressed;

    public string Glyph { get; }
    public string Subtitle { get; }

    public ActionTile(string glyph, string title, string subtitle)
    {
        Glyph = glyph;
        Text = title;
        Subtitle = subtitle;
        Height = 84;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.SurfaceColor(this));

        var fill = _pressed ? Color.FromArgb(40, 40, 40) : _hover ? Theme.CardHover : Theme.Card;
        using (var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 6))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using var pen = new Pen(_hover ? Color.FromArgb(72, 72, 72) : Theme.Border);
            g.DrawPath(pen, path);
        }

        // Icon in a tinted rounded square.
        var iconRect = new Rectangle(16, Height / 2 - 20, 40, 40);
        using (var bg = Theme.RoundedRect(iconRect, 8))
        using (var b = new SolidBrush(Color.FromArgb(46, Theme.Accent)))
            g.FillPath(b, bg);
        TextRenderer.DrawText(g, Glyph, Theme.IconsLarge, iconRect, ControlPaint.Light(Theme.Accent, 0.6f),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var textLeft = iconRect.Right + 14;
        TextRenderer.DrawText(g, Loc.T(Text), Theme.BodyBold, new Rectangle(textLeft, Height / 2 - 20, Width - textLeft - 28, 20), Theme.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, Loc.T(Subtitle), Theme.Small, new Rectangle(textLeft, Height / 2, Width - textLeft - 28, 20), Theme.Muted,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        // Chevron
        TextRenderer.DrawText(g, "", Theme.IconsSmall, new Rectangle(Width - 28, 0, 20, Height), _hover ? Theme.Text : Theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>Small uppercase section header for the sidebar.</summary>
public sealed class NavHeader : Control
{
    public NavHeader(string text)
    {
        Text = text;
        Height = 30;
        Margin = new Padding(0, 6, 0, 0);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Sidebar);
        TextRenderer.DrawText(e.Graphics, Loc.T(Text).ToUpperInvariant(), Theme.Small, new Rectangle(18, 8, Width - 20, Height - 8),
            Color.FromArgb(130, 130, 130), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Accent-tinted rounded square with a large icon, used in page headers.</summary>
public sealed class IconTile : Control
{
    public string Glyph { get; }
    public Color Tint { get; }

    public IconTile(string glyph, Color? tint = null)
    {
        Glyph = glyph;
        Tint = tint ?? Theme.Accent;
        Size = new Size(48, 48);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.SurfaceColor(this));
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedRect(r, 10))
        using (var brush = new LinearGradientBrush(r, Color.FromArgb(70, Tint), Color.FromArgb(30, Tint), LinearGradientMode.ForwardDiagonal))
            g.FillPath(brush, path);
        TextRenderer.DrawText(g, Glyph, Theme.IconsLarge, r, ControlPaint.Light(Tint, 0.7f),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }
}

/// <summary>Windows 11 segmented control (pill with mutually exclusive options).</summary>
public sealed class Segmented : FlowLayoutPanel
{
    private readonly List<(string Key, FlatBtn Button)> _items = [];

    public event Action<string>? SelectedChanged;

    public string? Selected { get; private set; }

    public Segmented()
    {
        AutoSize = true;
        WrapContents = false;
        BackColor = Color.Transparent;
        Margin = new Padding(0, 2, 0, 8);
    }

    public Segmented Add(string key, string text, string? glyph = null)
    {
        var b = Theme.Button(text, (_, _) => Select(key), glyph: glyph);
        b.Margin = new Padding(0, 0, 4, 0);
        _items.Add((key, b));
        Controls.Add(b);
        return this;
    }

    public void Select(string key, bool notify = true)
    {
        Selected = key;
        foreach (var (k, b) in _items) b.Primary = k == key;
        if (notify) SelectedChanged?.Invoke(key);
    }
}

/// <summary>Large selectable card (icon, title, description), used like a radio button.</summary>
public sealed class OptionCard : Control
{
    private bool _hover;
    private bool _selected;

    public string Glyph { get; }
    public string Description { get; set; }

    public OptionCard(string glyph, string title, string description)
    {
        Glyph = glyph;
        Text = title;
        Description = description;
        Height = 96;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.SurfaceColor(this));
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedRect(r, 8))
        {
            using (var b = new SolidBrush(_selected ? Color.FromArgb(36, 52, 72) : _hover ? Theme.CardHover : Color.FromArgb(38, 38, 38))) g.FillPath(b, path);
            using var pen = new Pen(_selected ? Theme.Accent : _hover ? Color.FromArgb(80, 80, 80) : Theme.Border, _selected ? 2f : 1f);
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(g, Glyph, Theme.IconsLarge, new Rectangle(14, 14, 32, 32), _selected ? ControlPaint.Light(Theme.Accent, 0.6f) : Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Loc.T(Text), Theme.BodyBold, new Rectangle(56, 14, Width - 70, 22), Theme.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, Loc.T(Description), Theme.Small, new Rectangle(56, 38, Width - 70, Height - 46), Theme.Muted,
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (_selected)
        {
            var c = new Rectangle(Width - 26, 10, 16, 16);
            using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, c);
            using var pen = new Pen(Color.White, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(pen, [new PointF(c.X + 4.5f, c.Y + 8.5f), new PointF(c.X + 7f, c.Y + 11f), new PointF(c.X + 11.5f, c.Y + 5.5f)]);
        }
    }
}

/// <summary>Small rounded tag ("Slow", "Restart", "Recommended").</summary>
public sealed class Pill : Control
{
    private readonly Color _color;

    public Pill(string text, Color color)
    {
        _color = color;
        Text = Loc.T(text);
        Font = Theme.Small;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        var size = TextRenderer.MeasureText(Text, Font);
        Size = new Size(size.Width + 14, size.Height + 4);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.SurfaceColor(this));
        using (var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), (Height - 1) / 2))
        using (var b = new SolidBrush(Color.FromArgb(38, _color)))
            g.FillPath(b, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, _color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}
