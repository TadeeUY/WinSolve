using System.Drawing.Drawing2D;
using WinSolve.Core;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>File type colors, WizTree style.</summary>
public static class FileColors
{
    private static readonly Dictionary<string, Color> Known = new(StringComparer.OrdinalIgnoreCase);

    static FileColors()
    {
        void Set(Color c, params string[] exts) { foreach (var e in exts) Known[e] = c; }
        Set(Color.FromArgb(143, 82, 230), ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v", ".flv", ".ts");
        Set(Color.FromArgb(47, 128, 237), ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".raw", ".psd", ".tif", ".tiff");
        Set(Color.FromArgb(0, 184, 212), ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".wma", ".opus");
        Set(Color.FromArgb(224, 72, 72), ".zip", ".rar", ".7z", ".tar", ".gz", ".iso", ".cab", ".msi", ".img", ".vhd", ".vhdx");
        Set(Color.FromArgb(52, 168, 83), ".exe", ".dll", ".sys", ".msix", ".appx", ".so", ".jar");
        Set(Color.FromArgb(234, 120, 20), ".pak", ".vpk", ".ucas", ".utoc", ".bundle", ".assets", ".big", ".forge", ".bin", ".dat", ".arc", ".wad", ".bsa", ".ba2", ".rpf");
        Set(Color.FromArgb(220, 190, 40), ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".odt", ".epub");
        Set(Color.FromArgb(200, 60, 130), ".sys_pagefile");
        Set(Color.FromArgb(120, 120, 130), ".log", ".tmp", ".etl", ".dmp", ".evtx", ".bak", ".old");
        Set(Color.FromArgb(90, 160, 140), ".db", ".sqlite", ".ldb", ".edb", ".mdf", ".vdi", ".vmdk");
    }

    public static Color For(SpaceNode n)
    {
        if (n.IsGroup) return Color.FromArgb(85, 88, 98);
        var name = n.Name;
        if (name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)
            || name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))
            return Known[".sys_pagefile"];
        return For(n.Extension);
    }

    public static Color For(string ext)
    {
        if (Known.TryGetValue(ext, out var c)) return c;
        // Stable color derived from the extension for everything else.
        var h = 0;
        foreach (var ch in ext) h = h * 31 + ch;
        return FromHsl(Math.Abs(h % 360), 0.55, 0.5);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }
}

/// <summary>
/// Squarified treemap with folder headers and cushion shading, like WizTree.
/// Click selects, double-click zooms into the folder.
/// </summary>
public sealed class TreemapView : Control
{
    private const int HeaderHeight = 15;
    private const int MinHeaderWidth = 60;

    private readonly List<(RectangleF Rect, SpaceNode Node)> _hits = [];
    private Bitmap? _cache;
    private SpaceNode? _root;
    private SpaceNode? _hover;
    private SpaceNode? _selected;
    private readonly Font _headerFont = new("Segoe UI", 8f);

    public event Action<SpaceNode?>? HoverChanged;
    public event Action<SpaceNode>? NodeSelected;
    public event Action<SpaceNode>? NodeActivated;

    public TreemapView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Sidebar;
    }

    public SpaceNode? Root
    {
        get => _root;
        set
        {
            _root = value;
            _hover = null;
            // Old hit areas would otherwise make invisible nodes from a previous scan clickable.
            _hits.Clear();
            if (value is null) _selected = null;
            Repaint(true);
        }
    }

    public SpaceNode? Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    private void Repaint(bool rebuild)
    {
        if (rebuild)
        {
            _cache?.Dispose();
            _cache = null;
        }
        Invalidate();
    }

    public void Rebuild() => Repaint(true);

    private System.Windows.Forms.Timer? _resizeTimer;

    // Redrawing a whole drive on every resize step made dragging stutter: show the old picture
    // stretched while resizing and rebuild once it stops.
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_cache is null || _root is null)
        {
            Repaint(true);
            return;
        }
        if (_resizeTimer is null)
        {
            _resizeTimer = new System.Windows.Forms.Timer { Interval = 150 };
            _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); Repaint(true); };
        }
        _resizeTimer.Stop();
        _resizeTimer.Start();
        Invalidate();
    }



    protected override void OnPaint(PaintEventArgs e)
    {
        if (_root is null || Width < 4 || Height < 4)
        {
            e.Graphics.Clear(BackColor);
            TextRenderer.DrawText(e.Graphics, Localization.Loc.T("Choose a drive and select Scan."), Theme.Body, ClientRectangle, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        if (_cache is null || (_resizeTimer is not { Enabled: true } && (_cache.Width != Width || _cache.Height != Height)))
        {
            _cache?.Dispose();
            _cache = new Bitmap(Width, Height);
            _hits.Clear();
            using var g = Graphics.FromImage(_cache);
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.None;
            DrawNode(g, _root, new RectangleF(0, 0, Width, Height), 0);
        }
        if (_cache.Width == Width && _cache.Height == Height) e.Graphics.DrawImageUnscaled(_cache, 0, 0);
        else e.Graphics.DrawImage(_cache, ClientRectangle); // resizing: rebuilt when it stops

        if (_selected != _root) Outline(e.Graphics, _selected, Color.White, 2);
        if (_hover != _selected) Outline(e.Graphics, _hover, Color.FromArgb(200, 255, 255, 255), 1);
    }

    private void Outline(Graphics g, SpaceNode? node, Color color, int width)
    {
        if (node is null) return;
        foreach (var (rect, n) in _hits)
        {
            if (n != node) continue;
            using var pen = new Pen(color, width);
            g.DrawRectangle(pen, rect.X, rect.Y, Math.Max(1, rect.Width - 1), Math.Max(1, rect.Height - 1));
            return;
        }
    }

    private void DrawNode(Graphics g, SpaceNode node, RectangleF rect, int depth)
    {
        if (rect.Width < 1 || rect.Height < 1) return;
        _hits.Add((rect, node));
        // Too small to show anything inside: one flat block instead of recursing to 1-px tiles.
        if (!node.IsFile && node.Children.Count > 0 && (rect.Width < 4 || rect.Height < 4))
        {
            using var flat = new SolidBrush(Color.FromArgb(60, 64, 72));
            g.FillRectangle(flat, rect);
            return;
        }

        if (node.IsFile || node.Children.Count == 0)
        {
            DrawCushion(g, rect, node.IsFile ? FileColors.For(node) : Color.FromArgb(60, 64, 72));
            return;
        }

        // Folder frame.
        using (var frame = new SolidBrush(Color.FromArgb(Math.Max(18, 40 - depth * 3), Math.Max(20, 44 - depth * 3), Math.Max(24, 52 - depth * 3))))
            g.FillRectangle(frame, rect);

        var inner = rect;
        if (rect.Width >= MinHeaderWidth && rect.Height >= HeaderHeight * 2.2f)
        {
            var text = $"{node.DisplayName} ({Format.Bytes(node.Size)})";
            TextRenderer.DrawText(g, text, _headerFont,
                new Rectangle((int)rect.X + 2, (int)rect.Y, (int)rect.Width - 4, HeaderHeight), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            inner = new RectangleF(rect.X + 2, rect.Y + HeaderHeight, rect.Width - 4, rect.Height - HeaderHeight - 2);
        }
        else if (rect.Width > 6 && rect.Height > 6)
        {
            inner = RectangleF.Inflate(rect, -1, -1);
        }

        if (inner.Width < 2 || inner.Height < 2) return;

        var children = node.Children.Where(c => c.Size > 0).ToList();
        if (children.Count == 0) return;
        foreach (var (child, r) in Squarify(children, inner))
            DrawNode(g, child, r, depth + 1);
    }

    /// <summary>Diagonal gradient fill imitating WizTree's cushion shading.</summary>
    private static void DrawCushion(Graphics g, RectangleF r, Color c)
    {
        if (r.Width < 3 || r.Height < 3)
        {
            using var b = new SolidBrush(c);
            g.FillRectangle(b, r);
            return;
        }
        using var brush = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2),
            ControlPaint.Light(c, 0.35f), ControlPaint.Dark(c, 0.35f), LinearGradientMode.ForwardDiagonal);
        g.FillRectangle(brush, r);
        using var edge = new Pen(Color.FromArgb(70, 0, 0, 0));
        g.DrawRectangle(edge, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    /// <summary>Squarified layout (Bruls, Huizing, van Wijk): rectangles as square as possible.</summary>
    private static IEnumerable<(SpaceNode, RectangleF)> Squarify(List<SpaceNode> items, RectangleF rect)
    {
        double total = items.Sum(i => (double)i.Size);
        if (total <= 0) yield break;
        var scale = rect.Width * rect.Height / total;
        var remaining = rect;
        int i = 0;

        while (i < items.Count)
        {
            var shortSide = Math.Min(remaining.Width, remaining.Height);
            if (shortSide <= 0) yield break;

            var row = new List<SpaceNode>();
            double rowArea = 0, worst = double.MaxValue, rowMin = double.MaxValue, rowMax = 0;

            while (i < items.Count)
            {
                var area = items[i].Size * scale;
                var newArea = rowArea + area;
                var minA = Math.Min(rowMin, area);
                var maxA = Math.Max(rowMax, area);
                var s2 = shortSide * shortSide;
                var newWorst = Math.Max(s2 * maxA / (newArea * newArea), newArea * newArea / (s2 * minA));
                if (row.Count > 0 && newWorst > worst) break;
                row.Add(items[i]);
                rowArea = newArea;
                rowMin = minA;
                rowMax = maxA;
                worst = newWorst;
                i++;
            }

            var horizontal = remaining.Width >= remaining.Height; // the row becomes a column on the left
            var thickness = (float)(rowArea / shortSide);
            float offset = 0;
            foreach (var n in row)
            {
                var len = (float)(n.Size * scale / thickness);
                yield return horizontal
                    ? (n, new RectangleF(remaining.X, remaining.Y + offset, thickness, len))
                    : (n, new RectangleF(remaining.X + offset, remaining.Y, len, thickness));
                offset += len;
            }

            remaining = horizontal
                ? new RectangleF(remaining.X + thickness, remaining.Y, remaining.Width - thickness, remaining.Height)
                : new RectangleF(remaining.X, remaining.Y + thickness, remaining.Width, remaining.Height - thickness);

            // The remaining tiny items would not be visible: stop early.
            if (remaining.Width < 1 || remaining.Height < 1) yield break;
        }
    }

    /// <summary>Deepest item under the cursor.</summary>
    private SpaceNode? HitTest(Point p)
    {
        for (int i = _hits.Count - 1; i >= 0; i--)
            if (_hits[i].Rect.Contains(p)) return _hits[i].Node;
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var n = HitTest(e.Location);
        if (n == _hover) return;
        _hover = n;
        Invalidate();
        HoverChanged?.Invoke(n);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        Invalidate();
        HoverChanged?.Invoke(null);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var n = HitTest(e.Location);
        if (n is null) return;
        _selected = n;
        Invalidate();
        NodeSelected?.Invoke(n);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        var n = HitTest(e.Location);
        // Double-click: zoom into the folder that contains the block.
        while (n is not null && n.IsFile) n = n.Parent;
        if (n is not null && n != _root) NodeActivated?.Invoke(n);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cache?.Dispose();
            _resizeTimer?.Dispose();
            _headerFont.Dispose();
        }
        base.Dispose(disposing);
    }
}
