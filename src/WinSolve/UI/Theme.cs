using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>Colors and fonts. Neutral dark palette modeled on Windows 11.</summary>
public static class Theme
{
    public static readonly Color Background = Color.FromArgb(32, 32, 32);
    public static readonly Color Sidebar = Color.FromArgb(28, 28, 28);
    public static readonly Color Card = Color.FromArgb(43, 43, 43);
    public static readonly Color CardHover = Color.FromArgb(50, 50, 50);
    public static readonly Color Control = Color.FromArgb(55, 55, 55);
    public static readonly Color ControlHover = Color.FromArgb(62, 62, 62);
    public static readonly Color Border = Color.FromArgb(58, 58, 58);
    public static readonly Color Divider = Color.FromArgb(50, 50, 50);
    public static readonly Color Text = Color.FromArgb(242, 242, 242);
    public static readonly Color Muted = Color.FromArgb(160, 160, 160);
    public static readonly Color Good = Color.FromArgb(108, 203, 95);
    public static readonly Color Warn = Color.FromArgb(252, 225, 0);
    public static readonly Color Bad = Color.FromArgb(255, 99, 97);
    public static readonly Color Info = Color.FromArgb(96, 205, 255);

    public static Color Accent
    {
        get
        {
            try { return ColorTranslator.FromHtml(AppSettings.Current.AccentColor); }
            catch { return Color.FromArgb(0, 103, 192); }
        }
    }

    private static readonly string UiFamily = FontExists("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    private static readonly string DisplayFamily = FontExists("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";

    public static readonly Font Body = new(UiFamily, 9.5f);
    public static readonly Font BodyBold = new(UiFamily, 9.5f, FontStyle.Bold);
    public static readonly Font Small = new(UiFamily, 8.5f);
    public static readonly Font H1 = new(DisplayFamily, 19f, FontStyle.Bold);
    public static readonly Font H2 = new(DisplayFamily, 12f, FontStyle.Bold);
    public static readonly Font Big = new(DisplayFamily, 26f, FontStyle.Bold);
    public static readonly Font Mono = new("Consolas", 9f);
    private static readonly string IconFamily = FontExists("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    public static readonly Font Icons = new(IconFamily, 12f);
    public static readonly Font IconsSmall = new(IconFamily, 10f);
    public static readonly Font IconsLarge = new(IconFamily, 18f);

    private static bool FontExists(string name)
    {
        using var f = new Font(name, 9f);
        return f.Name.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    public static Color For(HealthLevel level) => level switch
    {
        HealthLevel.Good => Good,
        HealthLevel.Caution => Warn,
        HealthLevel.Bad => Bad,
        _ => Muted,
    };

    public static Color For(IssueSeverity s) => s switch
    {
        IssueSeverity.Critical => Bad,
        IssueSeverity.Warning => Warn,
        _ => Info,
    };

    public static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>First opaque background behind a control (transparent containers are skipped).</summary>
    public static Color SurfaceColor(Control? c)
    {
        for (var p = c?.Parent; p is not null; p = p.Parent)
            if (p.BackColor.A == 255) return p.BackColor;
        return Background;
    }

    private static readonly Dictionary<int, Icon> IconCache = [];

    /// <summary>The WinSolve icon at the requested size (embedded resource).</summary>
    public static Icon AppIcon(int size = 32)
    {
        if (IconCache.TryGetValue(size, out var cached)) return cached;
        using var stream = typeof(Theme).Assembly.GetManifestResourceStream("WinSolve.ico");
        var icon = stream is null ? SystemIcons.Application : new Icon(stream, size, size);
        IconCache[size] = icon;
        return icon;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark title bar and, on Windows 11, Mica backdrop and rounded corners.</summary>
    public static void StyleWindow(Form form)
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(form.Handle, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            int round = 2;
            DwmSetWindowAttribute(form.Handle, 33, ref round, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE = round
            int caption = ColorTranslator.ToWin32(Sidebar);
            DwmSetWindowAttribute(form.Handle, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
        }
        catch { /* older Windows: ignore */ }
    }

    // ───────────── Control factories ─────────────

    public static Label Label(string text, Font? font = null, Color? color = null) => new LocLabel
    {
        Text = text,
        Font = font ?? Body,
        ForeColor = color ?? Text,
        AutoSize = true,
        UseMnemonic = false,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 0, 0, 4),
    };

    /// <summary>Muted text that wraps to the available width.</summary>
    public static Label Paragraph(string text, Color? color = null, Font? font = null) => new WrapLabel
    {
        Text = text,
        UseMnemonic = false,
        Font = font ?? Body,
        ForeColor = color ?? Muted,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 0, 0, 6),
    };

    public static FlatBtn Button(string text, EventHandler? onClick = null, bool primary = false, string? glyph = null)
    {
        var b = new FlatBtn(primary) { Text = text, Glyph = glyph };
        if (onClick is not null) b.Click += onClick;
        return b;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 2),
            Dock = DockStyle.Top,
        };
        row.Controls.AddRange(controls);
        return row;
    }

    public static void StyleGrid(DataGridView g)
    {
        g.BackgroundColor = Card;
        g.BorderStyle = BorderStyle.None;
        g.GridColor = Divider;
        g.EnableHeadersVisualStyles = false;
        g.RowHeadersVisible = false;
        g.AllowUserToAddRows = false;
        g.AllowUserToDeleteRows = false;
        g.AllowUserToResizeRows = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.MultiSelect = false;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 34;
        g.RowTemplate.Height = 34;
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Card, ForeColor = Muted, Font = Small,
            SelectionBackColor = Card, SelectionForeColor = Muted,
            Padding = new Padding(8, 0, 8, 0),
        };
        g.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Card, ForeColor = Text, Font = Body,
            SelectionBackColor = CardHover, SelectionForeColor = Text,
            Padding = new Padding(8, 2, 8, 2),
            WrapMode = DataGridViewTriState.False,
        };
        g.AlternatingRowsDefaultCellStyle = g.DefaultCellStyle;
        // Windows 11 style check boxes inside grids.
        g.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.Graphics is null || g.Columns[e.ColumnIndex] is not DataGridViewCheckBoxColumn) return;
            e.PaintBackground(e.CellBounds, (e.State & DataGridViewElementStates.Selected) != 0);
            var size = 18;
            var box = new Rectangle(e.CellBounds.X + (e.CellBounds.Width - size) / 2, e.CellBounds.Y + (e.CellBounds.Height - size) / 2, size, size);
            DrawCheckBox(e.Graphics, box, e.Value is true, false);
            e.Handled = true;
        };

        // Subtle row highlight under the mouse.
        g.CellMouseEnter += (_, e) => { if (e.RowIndex >= 0 && !g.Rows[e.RowIndex].Selected) g.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.FromArgb(48, 48, 48); };
        g.CellMouseLeave += (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < g.Rows.Count) g.Rows[e.RowIndex].DefaultCellStyle.BackColor = Color.Empty; };
        g.CellFormatting += (_, e) => { if (e.Value is string s && s.Length > 0) { e.Value = Loc.T(s); e.FormattingApplied = true; } };
        typeof(DataGridView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(g, true);
    }

    public static TextBox TextBox(string placeholder = "") => new()
    {
        BackColor = Control,
        ForeColor = Text,
        BorderStyle = BorderStyle.FixedSingle,
        Font = Body,
        PlaceholderText = Loc.T(placeholder),
        Width = 260,
        Margin = new Padding(0, 5, 8, 5),
    };

    public static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox
        {
            FormattingEnabled = true,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Control,
            ForeColor = Text,
            Font = Body,
            Width = 200,
            Margin = new Padding(0, 5, 8, 5),
        };
        c.Format += (_, e) => e.Value = Loc.T(e.ListItem?.ToString());
        // Dark drop-down list.
        c.DrawMode = DrawMode.OwnerDrawFixed;
        c.ItemHeight = 22;
        c.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var selected = (e.State & DrawItemState.Selected) != 0;
            using (var bg = new SolidBrush(selected ? ControlHover : Control)) e.Graphics.FillRectangle(bg, e.Bounds);
            TextRenderer.DrawText(e.Graphics, Loc.T(c.Items[e.Index]?.ToString()), Body,
                new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height), Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        c.Items.AddRange(items);
        if (items.Length > 0) c.SelectedIndex = 0;
        return c;
    }

    public static CheckBox Check(string text, bool value) => new LocCheckBox
    {
        Text = text,
        Checked = value,
        ForeColor = Text,
        Font = Body,
        AutoSize = true,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 3, 0, 3),
    };

    /// <summary>Windows 11 style on/off switch (a CheckBox, so Checked/CheckedChanged work as usual).</summary>
    public static CheckBox Toggle(string text, bool value) => new LocCheckBox
    {
        Text = text,
        Checked = value,
        Switch = true,
        ForeColor = Text,
        Font = Body,
        AutoSize = true,
        BackColor = Color.Transparent,
        Margin = new Padding(0, 3, 0, 3),
    };

    /// <summary>Draws a Windows 11 check box (rounded, accent filled when checked).</summary>
    public static void DrawCheckBox(Graphics g, Rectangle box, bool isChecked, bool hover, bool enabled = true)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(box, 4);
        if (isChecked)
        {
            using (var b = new SolidBrush(enabled ? (hover ? ControlPaint.Light(Accent, 0.1f) : Accent) : Color.FromArgb(80, 80, 80))) g.FillPath(b, path);
            using var pen = new Pen(Color.White, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            var x = box.X; var y = box.Y; var w = box.Width;
            g.DrawLines(pen, [new PointF(x + w * 0.24f, y + w * 0.52f), new PointF(x + w * 0.43f, y + w * 0.70f), new PointF(x + w * 0.76f, y + w * 0.32f)]);
        }
        else
        {
            using (var b = new SolidBrush(hover ? Color.FromArgb(52, 52, 52) : Color.FromArgb(40, 40, 40))) g.FillPath(b, path);
            using var pen = new Pen(Color.FromArgb(enabled ? 150 : 90, 150, 150, 150));
            g.DrawPath(pen, path);
        }
    }

    /// <summary>Draws a Windows 11 toggle switch.</summary>
    public static void DrawSwitch(Graphics g, Rectangle r, bool on, bool hover, bool enabled = true)
        => DrawSwitch(g, r, on ? 1 : 0, hover, enabled);

    /// <summary>Draws a switch; <paramref name="position"/> goes from 0 (off) to 1 (on) so the knob can slide.</summary>
    public static void DrawSwitch(Graphics g, Rectangle r, double position, bool hover, bool enabled = true)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(r, r.Height / 2);
        var t = Math.Clamp(position, 0, 1);
        var onFill = enabled ? (hover ? ControlPaint.Light(Accent, 0.1f) : Accent) : Color.FromArgb(80, 80, 80);
        var offFill = hover ? Color.FromArgb(52, 52, 52) : Color.FromArgb(40, 40, 40);
        using (var b = new SolidBrush(Animator.Blend(offFill, onFill, t))) g.FillPath(b, path);
        if (t < 1)
        {
            using var pen = new Pen(Color.FromArgb((int)(255 * (1 - t)), 160, 160, 160));
            g.DrawPath(pen, path);
        }
        // The knob grows a little and slides from left to right.
        var size = r.Height - 10 + 2 * t + (hover ? 1 : 0);
        var left = r.X + 5 + (r.Width - 10 - size) * t;
        using var k = new SolidBrush(Animator.Blend(Color.FromArgb(200, 200, 200), Color.White, t));
        g.FillEllipse(k, (float)left, (float)(r.Y + (r.Height - size) / 2), (float)size, (float)size);
    }

    /// <summary>
    /// Windows 11 Settings row: title and description on the left, a control (switch, list,
    /// button) on the right.
    /// </summary>
    public static Control SettingRow(string title, string? description, Control control, Label? descriptionLabel = null)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 4) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0), Dock = DockStyle.Fill };
        text.Controls.Add(Label(title, Body));
        var desc = descriptionLabel ?? (description is null ? null : Label(description, Small, Muted));
        if (desc is not null)
        {
            desc.MaximumSize = new Size(620, 0);
            text.Controls.Add(desc);
        }
        control.Anchor = AnchorStyles.Right;
        control.Margin = new Padding(12, 2, 0, 2);
        row.Controls.Add(text, 0, 0);
        row.Controls.Add(control, 1, 0);
        return row;
    }

    /// <summary>Card section header: small icon tile, title and a one-line description.</summary>
    public static Control SectionHeader(string glyph, string title, string? subtitle = null)
    {
        var head = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) };
        head.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0) };
        titles.Controls.Add(Label(title, H2));
        if (subtitle is not null) titles.Controls.Add(Label(subtitle, Small, Muted));
        head.Controls.Add(new IconTile(glyph) { Size = new Size(36, 36), Margin = new Padding(0, 2, 10, 0) }, 0, 0);
        head.Controls.Add(titles, 1, 0);
        return head;
    }

    /// <summary>Puts a control (usually a grid) inside a card with a thin border.</summary>
    public static Card InCard(Control content, int padding = 6)
    {
        var card = new Card { Padding = new Padding(padding), Margin = new Padding(0), Dock = DockStyle.Fill };
        content.Dock = DockStyle.Fill;
        card.Controls.Add(content);
        return card;
    }

    /// <summary>Shows a centered message while a grid has no rows.</summary>
    public static void EmptyState(DataGridView grid, string message)
    {
        grid.Paint += (_, e) =>
        {
            if (grid.Rows.Count > 0) return;
            var area = new Rectangle(0, grid.ColumnHeadersHeight, grid.Width, Math.Max(60, grid.Height - grid.ColumnHeadersHeight));
            TextRenderer.DrawText(e.Graphics, "\uE946", IconsLarge, new Rectangle(area.X, area.Y + area.Height / 2 - 40, area.Width, 30), Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, Loc.T(message), Body, new Rectangle(area.X + 20, area.Y + area.Height / 2 - 6, area.Width - 40, 40), Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
        };
        grid.RowsAdded += (_, _) => grid.Invalidate();
        grid.RowsRemoved += (_, _) => grid.Invalidate();
    }

    /// <summary>Small colored dot followed by text, used for status values.</summary>
    public static Control Status(string text, Color color) => new StatusLabel(text, color);
}

/// <summary>Flat button with slightly rounded corners (Windows 11 style).</summary>
public sealed class FlatBtn : Button
{
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => base.Text;
        set => base.Text = Loc.T(value);
    }

    private bool _primary;
    private bool _pressed;

    public bool Primary
    {
        get => _primary;
        set { _primary = value; ForeColor = value ? Color.White : Theme.Text; Invalidate(); }
    }

    private string? _glyph;

    /// <summary>Optional icon (Segoe Fluent Icons / MDL2 code point) drawn before the text.</summary>
    public string? Glyph
    {
        get => _glyph;
        set { _glyph = value; Padding = value is null ? new Padding(12, 3, 12, 3) : new Padding(34, 3, 12, 3); Invalidate(); }
    }

    public FlatBtn(bool primary)
    {
        _primary = primary;
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Font = Theme.Body;
        ForeColor = primary ? Color.White : Theme.Text;
        Cursor = Cursors.Default;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12, 3, 12, 3);
        Margin = new Padding(0, 4, 8, 4);
        MinimumSize = new Size(88, 32);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    private double _hoverT;

    private void FadeHover(bool on) => Animator.Animate(this, () => _hoverT, on ? 1 : 0, v => { _hoverT = v; Invalidate(); }, 120);

    protected override void OnMouseEnter(EventArgs e) { FadeHover(true); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _pressed = false; FadeHover(false); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.SurfaceColor(this));

        Color fill = _primary
            ? Animator.Blend(Theme.Accent, ControlPaint.Light(Theme.Accent, 0.12f), _hoverT)
            : Animator.Blend(Theme.Control, Theme.ControlHover, _hoverT);
        if (_pressed) fill = ControlPaint.Dark(fill, 0.08f);
        if (!Enabled) fill = Color.FromArgb(48, 48, 48);

        using var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 4);
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        if (!_primary)
        {
            using var pen = new Pen(Theme.Border);
            g.DrawPath(pen, path);
        }

        var color = Enabled ? ForeColor : Color.FromArgb(120, 120, 120);
        var textRect = ClientRectangle;
        if (_glyph is not null)
        {
            TextRenderer.DrawText(g, _glyph, Theme.IconsSmall, new Rectangle(10, 0, 20, Height), color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            textRect = new Rectangle(28, 0, Width - 34, Height);
        }
        TextRenderer.DrawText(g, Text, Font, textRect, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Card surface: subtle fill, 1px border, small radius.</summary>
public class Card : Panel
{
    public Color Fill { get; set; } = Theme.Card;
    public bool Bordered { get; set; } = true;

    public Card()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Padding = new Padding(16, 14, 16, 14);
        Margin = new Padding(0, 0, 0, 8);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        using var path = Theme.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 6);
        using (var brush = new SolidBrush(Fill)) g.FillPath(brush, path);
        if (Bordered)
        {
            using var pen = new Pen(Theme.Border);
            g.DrawPath(pen, path);
        }
    }
}

/// <summary>Label that grows in height to fit wrapped text at its container's width.</summary>
public sealed class WrapLabel : LocLabel
{
    public WrapLabel() => AutoSize = false;

    protected override void OnParentChanged(EventArgs e)
    {
        base.OnParentChanged(e);
        if (Parent is not null) Parent.Resize += (_, _) => Fit();
        Fit();
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); }

    private void Fit()
    {
        if (Parent is null) return;
        var width = Parent.ClientSize.Width - Parent.Padding.Horizontal - Margin.Horizontal;
        if (Parent is FlowLayoutPanel { FlowDirection: FlowDirection.LeftToRight }) width = Math.Min(width, 900);
        if (width <= 0) return;
        var size = TextRenderer.MeasureText(Text, Font, new Size(width, int.MaxValue), TextFormatFlags.WordBreak);
        Size = new Size(width, size.Height + 2);
    }
}

/// <summary>"● Text" status indicator.</summary>
public sealed class StatusLabel : Control
{
    private readonly Color _dot;

    public StatusLabel(string text, Color dot)
    {
        _dot = dot;
        Text = Loc.T(text);
        Font = Theme.Body;
        ForeColor = Theme.Text;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 0, 4);
        var sz = TextRenderer.MeasureText(Text, Font);
        Size = new Size(sz.Width + 18, Math.Max(sz.Height, 18));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(_dot)) g.FillEllipse(b, 1, Height / 2 - 4, 8, 8);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(16, 0, Width - 16, Height), ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Dark context menus.</summary>
public static class Menus
{
    public static ContextMenuStrip Create()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Card,
            ForeColor = Theme.Text,
            ShowImageMargin = false,
            Font = Theme.Body,
            Renderer = new ToolStripProfessionalRenderer(new DarkColors()),
        };
        menu.ItemAdded += (_, e) => { if (e.Item is { } item) item.Text = Loc.T(item.Text); };
        return menu;
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Theme.ControlHover;
        public override Color MenuItemBorder => Theme.ControlHover;
        public override Color MenuBorder => Theme.Border;
        public override Color ToolStripDropDownBackground => Theme.Card;
        public override Color ImageMarginGradientBegin => Theme.Card;
        public override Color ImageMarginGradientMiddle => Theme.Card;
        public override Color ImageMarginGradientEnd => Theme.Card;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }
}
