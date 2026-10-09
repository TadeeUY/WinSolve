using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>Selectable drive (or folder) with its usage bar, for the Disk space page.</summary>
public sealed class DriveCard : Control
{
    private double _hoverT;
    private bool _selected;

    public string Path { get; set; }
    public string Glyph { get; }
    public string Detail { get; set; }
    /// <summary>0..1, or negative for "no bar" (a folder).</summary>
    public double Used { get; set; }

    public DriveCard(string path, string title, string detail, double used, string glyph = "")
    {
        Path = path;
        Text = title;
        Detail = detail;
        Used = used;
        Glyph = glyph;
        Size = new Size(260, 78);
        Margin = new Padding(0, 0, 10, 10);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e) { Animator.Animate(this, () => _hoverT, 1, v => { _hoverT = v; Invalidate(); }, 120); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Animator.Animate(this, () => _hoverT, 0, v => { _hoverT = v; Invalidate(); }, 120); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.SurfaceColor(this));
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Theme.RoundedRect(r, 8))
        {
            var fill = _selected ? Color.FromArgb(36, 52, 72) : Animator.Blend(Theme.Card, Theme.CardHover, _hoverT);
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using var pen = new Pen(_selected ? Theme.Accent : Animator.Blend(Theme.Border, Color.FromArgb(80, 80, 80), _hoverT), _selected ? 2f : 1f);
            g.DrawPath(pen, path);
        }

        // Icon tile.
        var tile = new Rectangle(14, 16, 38, 38);
        using (var tp = Theme.RoundedRect(tile, 8))
        using (var tb = new SolidBrush(Color.FromArgb(_selected ? 80 : 45, Theme.Accent)))
            g.FillPath(tb, tp);
        TextRenderer.DrawText(g, Glyph, Theme.Icons, tile, ControlPaint.Light(Theme.Accent, 0.7f),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var x = 64;
        var w = Width - x - 14;
        TextRenderer.DrawText(g, Loc.T(Text), Theme.BodyBold, new Rectangle(x, 12, w, 20), Theme.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        if (Used >= 0)
        {
            var bar = new Rectangle(x, 37, w, 6);
            using (var bp = Theme.RoundedRect(bar, 3))
            using (var bb = new SolidBrush(Color.FromArgb(58, 58, 58)))
                g.FillPath(bb, bp);
            var fw = (int)(w * Math.Clamp(Used, 0, 1));
            if (fw > 3)
            {
                using var fp = Theme.RoundedRect(new Rectangle(x, 37, fw, 6), 3);
                using var fb = new SolidBrush(Used >= 0.9 ? Theme.Bad : Used >= 0.75 ? Theme.Warn : Theme.Accent);
                g.FillPath(fb, fp);
            }
        }
        TextRenderer.DrawText(g, Loc.T(Detail), Theme.Small, new Rectangle(x, Used >= 0 ? 49 : 36, w, 18), Theme.Muted,
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.PathEllipsis);
    }
}

/// <summary>Folder tree drawn WizTree-style: chevron, icon, name, share bar and size.</summary>
public sealed class SpaceTree : TreeView
{
    private const int SizeColumn = 78;
    private const int BarWidth = 46;

    public SpaceTree()
    {
        DrawMode = TreeViewDrawMode.OwnerDrawAll;
        FullRowSelect = true;
        ShowLines = false;
        ShowRootLines = true;
        ShowPlusMinus = true;
        HideSelection = false;
        ItemHeight = 26;
        Indent = 18;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Font = Theme.Body;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Native double buffering: no flicker while expanding or scrolling.
        SendMessage(Handle, TVM_SETEXTENDEDSTYLE, (IntPtr)TVS_EX_DOUBLEBUFFER, (IntPtr)TVS_EX_DOUBLEBUFFER);
    }

    protected override void OnDrawNode(DrawTreeNodeEventArgs e)
    {
        if (e.Node is null || e.Bounds.Height <= 0) return;
        var g = e.Graphics;
        var row = new Rectangle(0, e.Bounds.Y, ClientSize.Width, e.Bounds.Height);
        var selected = (e.State & TreeNodeStates.Selected) != 0;
        using (var bg = new SolidBrush(Theme.Card)) g.FillRectangle(bg, row);
        if (selected)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var sp = Theme.RoundedRect(new Rectangle(row.X + 2, row.Y + 1, row.Width - 5, row.Height - 3), 4);
            using var sb = new SolidBrush(Color.FromArgb(70, Theme.Accent));
            g.FillPath(sb, sp);
        }

        // The native expand button sits one indent left of the text: draw the chevron there,
        // so clicking it keeps working.
        var textX = e.Node.Bounds.X;
        var chevron = new Rectangle(textX - Indent, row.Y, Indent, row.Height);
        var node = e.Node.Tag as SpaceNode;
        if (e.Node.Nodes.Count > 0)
            TextRenderer.DrawText(g, e.Node.IsExpanded ? "" : "", Theme.IconsSmall, chevron, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var right = row.Right - 8;
        var nameRight = right - SizeColumn - BarWidth - 12;
        var x = textX + 2;
        if (node is not null)
        {
            var glyph = node.IsGroup ? "" : node.IsFile ? "" : "";
            var color = node.IsFile && !node.IsGroup ? FileColors.For(node) : ControlPaint.Light(Theme.Accent, 0.5f);
            TextRenderer.DrawText(g, glyph, Theme.IconsSmall, new Rectangle(x, row.Y, 18, row.Height), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x += 24;
        }

        TextRenderer.DrawText(g, node?.DisplayName ?? e.Node.Text, Theme.Body, new Rectangle(x, row.Y, Math.Max(10, nameRight - x), row.Height),
            node?.IsGroup == true ? Theme.Muted : Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        if (node is null) return;

        // Share of the parent folder.
        var share = node.Parent is { Size: > 0 } p ? (double)node.Size / p.Size : 1;
        var bar = new Rectangle(right - SizeColumn - BarWidth - 6, row.Y + row.Height / 2 - 3, BarWidth, 6);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bp = Theme.RoundedRect(bar, 3))
        using (var bb = new SolidBrush(Color.FromArgb(58, 58, 58)))
            g.FillPath(bb, bp);
        var fw = (int)Math.Round(BarWidth * Math.Clamp(share, 0, 1));
        if (fw >= 2)
        {
            using var fp = Theme.RoundedRect(new Rectangle(bar.X, bar.Y, fw, bar.Height), 3);
            using var fb = new SolidBrush(share >= 0.5 ? Theme.Warn : Theme.Accent);
            g.FillPath(fb, fp);
        }
        TextRenderer.DrawText(g, Format.Bytes(node.Size), Theme.Body, new Rectangle(right - SizeColumn, row.Y, SizeColumn, row.Height),
            share >= 0.1 ? Theme.Text : Theme.Muted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }

    private const int TVM_SETEXTENDEDSTYLE = 0x112C, TVS_EX_DOUBLEBUFFER = 0x0004;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>What the Disk space page shows before and during a scan.</summary>
public sealed class ScanPlaceholder : Control
{
    private static readonly Font BigIcon = new(Theme.IconsLarge.FontFamily, 22f);
    private readonly ProgressLine _progress = new() { Indeterminate = false, Visible = false };
    private bool _scanning;

    public FlatBtn Action { get; }
    public string Title { get; set; } = "Analyze a drive";
    public string Subtitle { get; set; } = "Pick a drive above and select Scan to see which folders and files take the most space.";
    public string Counters { get; set; } = "";
    public string Current { get; set; } = "";

    public ScanPlaceholder(FlatBtn action)
    {
        Action = action;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Card;
        Controls.Add(Action);
        Controls.Add(_progress);
    }

    public bool Scanning
    {
        get => _scanning;
        set
        {
            _scanning = value;
            _progress.Visible = value;
            _progress.Indeterminate = value;
            Action.Visible = !value;
            Layout2();
            Invalidate();
        }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Layout2(); }

    private void Layout2()
    {
        var cy = Height / 2;
        Action.Location = new Point((Width - Action.Width) / 2, cy + 46);
        _progress.SetBounds(Width / 2 - 160, cy + 54, 320, 4);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        var cx = Width / 2;
        var cy = Height / 2;

        var circle = new Rectangle(cx - 34, cy - 110, 68, 68);
        using (var b = new SolidBrush(Color.FromArgb(40, Theme.Accent))) g.FillEllipse(b, circle);
        TextRenderer.DrawText(g, _scanning ? "" : "", BigIcon, circle,
            ControlPaint.Light(Theme.Accent, 0.6f), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak;
        TextRenderer.DrawText(g, Loc.T(Title), Theme.H2, new Rectangle(20, cy - 32, Width - 40, 30), Theme.Text, flags);
        if (_scanning)
        {
            TextRenderer.DrawText(g, Counters, Theme.BodyBold, new Rectangle(20, cy + 2, Width - 40, 22), Theme.Text, flags);
            TextRenderer.DrawText(g, Current, Theme.Small, new Rectangle(40, cy + 24, Width - 80, 20), Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
        }
        else
        {
            TextRenderer.DrawText(g, Loc.T(Subtitle), Theme.Body, new Rectangle(Math.Max(20, cx - 260), cy + 2, Math.Min(Width - 40, 520), 40), Theme.Muted, flags);
        }
    }
}
