using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>Bars of how crowded each Wi-Fi channel is: green is free, red is busy. The best channel is outlined.</summary>
public sealed class ChannelBars : Control
{
    private IReadOnlyList<ChannelLoad> _loads = [];

    public ChannelBars()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Card;
        AccessibleRole = AccessibleRole.Chart;
    }

    public void SetLoads(IReadOnlyList<ChannelLoad> loads)
    {
        _loads = loads;
        var best = loads.FirstOrDefault(l => l.IsBest);
        AccessibleName = best is null ? Loc.T("Wi-Fi channels") : Loc.T($"Wi-Fi channels. Least crowded: {best.Channel}");
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_loads.Count == 0)
        {
            TextRenderer.DrawText(g, Loc.T("Press 'Analyze Wi-Fi channels' to see how crowded each channel is."), Theme.Body,
                ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        const int top = 22, bottom = 44, side = 8;
        var plotHeight = Math.Max(20, Height - top - bottom);
        var slot = (Width - side * 2) / (double)_loads.Count;
        var barWidth = (int)Math.Clamp(slot * 0.7, 6, 46);
        var max = Math.Max(1.5, _loads.Max(l => l.Load));

        for (var i = 0; i < _loads.Count; i++)
        {
            var l = _loads[i];
            var x = (int)(side + slot * i + (slot - barWidth) / 2);
            var h = Math.Max(3, (int)(plotHeight * Math.Min(1, l.Load / max)));
            var bar = new Rectangle(x, top + plotHeight - h, barWidth, h);
            var color = l.Load < 0.6 ? Theme.Good : l.Load < 1.6 ? Theme.Warn : Theme.Bad;
            using (var path = Theme.RoundedRect(bar, 4))
            using (var brush = new SolidBrush(color))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.FillPath(brush, path);
                if (l.IsBest)
                {
                    using var pen = new Pen(Theme.Accent, 2);
                    g.DrawPath(pen, path);
                }
            }

            var labelBox = new Rectangle((int)(side + slot * i), top + plotHeight + 4, (int)slot, 18);
            TextRenderer.DrawText(g, l.Channel.ToString(), l.IsBest || l.IsCurrent ? Theme.BodyBold : Theme.Body, labelBox,
                l.IsBest ? Theme.Accent : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, l.Networks == 0 ? "" : l.Networks.ToString(), Theme.Small, new Rectangle(labelBox.X, labelBox.Y + 18, labelBox.Width, 16),
                Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);

            var tag = l.IsBest && l.IsCurrent ? Loc.T("You · Best") : l.IsBest ? Loc.T("Best") : l.IsCurrent ? Loc.T("You") : "";
            if (tag.Length > 0)
                TextRenderer.DrawText(g, tag, Theme.Small, new Rectangle((int)(side + slot * i) - 12, 2, (int)slot + 24, 18),
                    l.IsBest ? Theme.Accent : Theme.Info, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
    }
}
