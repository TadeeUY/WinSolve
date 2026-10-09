using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using WinSolve.Localization;

namespace WinSolve.UI;

/// <summary>Something the command palette can find and run.</summary>
public sealed record PaletteItem(string Title, string Subtitle, string Glyph, string Kind, Action Run, string Keywords = "");

/// <summary>
/// Ctrl+K search: type a few letters and jump to any page, task, tweak or tool. Matches the
/// English and the translated names, ignoring accents.
/// </summary>
public sealed class CommandPalette : Form
{
    private const int MaxResults = 9;
    private readonly IReadOnlyList<PaletteItem> _items;
    private readonly TextBox _search = new();
    private readonly ResultList _list;
    private List<PaletteItem> _shown = [];

    public CommandPalette(Form owner, IReadOnlyList<PaletteItem> items)
    {
        _items = items;
        Owner = owner;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(40, 40, 40);
        ForeColor = Theme.Text;
        Font = Theme.Body;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        Size = new Size(640, 64 + MaxResults * 52 + 12);
        Padding = new Padding(1);

        var searchHost = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = BackColor };
        searchHost.Paint += (_, e) =>
        {
            TextRenderer.DrawText(e.Graphics, "", Theme.Icons, new Rectangle(14, 0, 28, searchHost.Height), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(e.Graphics, "Esc", Theme.Small, new Rectangle(searchHost.Width - 50, 0, 40, searchHost.Height), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, searchHost.Height - 1, searchHost.Width, searchHost.Height - 1);
        };
        _search.BorderStyle = BorderStyle.None;
        _search.BackColor = BackColor;
        _search.ForeColor = Theme.Text;
        _search.Font = new Font("Segoe UI", 13f);
        _search.PlaceholderText = Loc.T("Search pages, tasks, tweaks and tools...");
        _search.SetBounds(52, 15, Width - 120, 28);
        _search.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        _search.TextChanged += (_, _) => Filter();
        searchHost.Controls.Add(_search);

        _list = new ResultList { Dock = DockStyle.Fill };
        _list.Activated += Run;

        Controls.Add(_list);
        Controls.Add(searchHost);

        Deactivate += (_, _) => Close();
        HandleCreated += (_, _) => Theme.StyleWindow(this);
        Filter();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    public static void ShowFor(Form owner, IReadOnlyList<PaletteItem> items)
    {
        var p = new CommandPalette(owner, items);
        var area = owner.RectangleToScreen(owner.ClientRectangle);
        p.Location = new Point(area.X + (area.Width - p.Width) / 2, area.Y + Math.Max(40, area.Height / 8));
        p.FormClosed += (_, _) => p.Dispose();
        p.Show(owner);
        p._search.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Escape: Close(); break;
            case Keys.Down: _list.MoveSelection(1); break;
            case Keys.Up: _list.MoveSelection(-1); break;
            case Keys.Enter: Run(_list.Current); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    private void Run(PaletteItem? item)
    {
        if (item is null) return;
        Close();
        // After the palette is gone, so dialogs open over the main window.
        Owner?.BeginInvoke(item.Run);
    }

    // ───────────── Matching ─────────────

    private void Filter()
    {
        var query = Fold(_search.Text);
        if (query.Length == 0)
        {
            _shown = _items.Where(i => i.Kind == "Page").Take(MaxResults).ToList();
        }
        else
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _shown = _items
                .Select(i => (Item: i, Score: Score(i, words)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Item.Title.Length)
                .Take(MaxResults)
                .Select(x => x.Item)
                .ToList();
        }
        _list.SetItems(_shown);
        // Only as tall as the results.
        Height = (int)((64 + Math.Max(1, _shown.Count) * 52 + 14) * DeviceDpi / 96f);
    }

    private static int Score(PaletteItem item, string[] words)
    {
        var title = Fold(item.Title) + " " + Fold(Loc.T(item.Title));
        var rest = Fold(item.Subtitle) + " " + Fold(Loc.T(item.Subtitle)) + " " + Fold(item.Keywords) + " " + Fold(Loc.T(item.Kind));
        var score = 0;
        foreach (var w in words)
        {
            if (title.Split(' ').Any(t => t.StartsWith(w, StringComparison.Ordinal))) score += 10;
            else if (title.Contains(w, StringComparison.Ordinal)) score += 6;
            else if (rest.Contains(w, StringComparison.Ordinal)) score += 2;
            else return 0; // every word must match somewhere
        }
        if (item.Kind == "Page") score += 3;
        return score;
    }

    /// <summary>Lowercase without accents, so "configuracion" finds "Configuración".</summary>
    private static string Fold(string s)
    {
        var normalized = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    // ───────────── Result list ─────────────

    private sealed class ResultList : Control
    {
        private List<PaletteItem> _items = [];
        private int _index;
        private int _hover = -1;

        public event Action<PaletteItem?>? Activated;

        public ResultList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(40, 40, 40);
        }

        public PaletteItem? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;

        public void SetItems(List<PaletteItem> items)
        {
            _items = items;
            _index = items.Count > 0 ? 0 : -1;
            Invalidate();
        }

        public void MoveSelection(int delta)
        {
            if (_items.Count == 0) return;
            _index = (_index + delta + _items.Count) % _items.Count;
            Invalidate();
        }

        private const int RowHeight = 52;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var i = (e.Y - 6) / RowHeight;
            var h = i >= 0 && i < _items.Count ? i : -1;
            if (h != _hover) { _hover = h; if (h >= 0) _index = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (_hover >= 0) Activated?.Invoke(_items[_hover]);
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);
            if (_items.Count == 0)
            {
                TextRenderer.DrawText(g, Loc.T("Nothing found. Try another word."), Theme.Body, new Rectangle(0, 20, Width, 30), Theme.Muted,
                    TextFormatFlags.HorizontalCenter);
                return;
            }
            for (int i = 0; i < _items.Count; i++)
            {
                var it = _items[i];
                var row = new Rectangle(8, 6 + i * RowHeight, Width - 16, RowHeight - 4);
                if (i == _index)
                {
                    using var path = Theme.RoundedRect(row, 6);
                    using var b = new SolidBrush(Color.FromArgb(60, Theme.Accent));
                    g.FillPath(b, path);
                }
                var tile = new Rectangle(row.X + 8, row.Y + 8, 32, 32);
                using (var tp = Theme.RoundedRect(tile, 6))
                using (var tb = new SolidBrush(Color.FromArgb(55, 55, 55)))
                    g.FillPath(tb, tp);
                TextRenderer.DrawText(g, it.Glyph, Theme.Icons, tile, ControlPaint.Light(Theme.Accent, 0.6f),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                var kind = Loc.T(it.Kind);
                var kindSize = TextRenderer.MeasureText(kind, Theme.Small);
                var textW = row.Width - 60 - kindSize.Width - 20;
                TextRenderer.DrawText(g, Loc.T(it.Title), Theme.BodyBold, new Rectangle(row.X + 52, row.Y + 6, textW, 20), Theme.Text,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, Loc.T(it.Subtitle), Theme.Small, new Rectangle(row.X + 52, row.Y + 26, textW, 18), Theme.Muted,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, kind, Theme.Small, new Rectangle(row.Right - kindSize.Width - 12, row.Y, kindSize.Width + 4, row.Height), Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }
}
