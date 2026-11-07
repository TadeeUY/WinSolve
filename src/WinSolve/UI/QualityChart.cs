using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>Bars of how long the Internet takes to answer over time; red where it did not answer.</summary>
public sealed class QualityChart : Control
{
    private IReadOnlyList<QualityBucket> _buckets = [];
    private DateTime _from, _to;

    public QualityChart()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Theme.Card;
        Height = 200;
        AccessibleRole = AccessibleRole.Chart;
        AccessibleName = Loc.T("Connection history");
    }

    public void SetData(IReadOnlyList<QualityBucket> buckets, DateTime from, DateTime to)
    {
        _buckets = buckets;
        _from = from;
        _to = to;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.Clear(BackColor);
        if (_buckets.Count == 0 || _buckets.All(b => b.Samples == 0))
        {
            TextRenderer.DrawText(g, Loc.T("No data yet. Turn on the recording and leave WinSolve running."), Theme.Body, ClientRectangle, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            return;
        }

        const int top = 16, bottom = 26, left = 8, right = 8;
        var plot = new Rectangle(left, top, Width - left - right, Math.Max(20, Height - top - bottom));
        var max = Math.Max(100, _buckets.Where(b => b.AverageMs is not null).Select(b => b.AverageMs!.Value).DefaultIfEmpty(0).Max() * 1.1);
        var slot = plot.Width / (double)_buckets.Count;
        var barWidth = (int)Math.Max(1, slot - 1);

        using (var line = new Pen(Theme.Divider)) g.DrawLine(line, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        for (var i = 0; i < _buckets.Count; i++)
        {
            var b = _buckets[i];
            if (b.Samples == 0) continue;
            var x = (int)(plot.Left + slot * i);
            var failedMostly = b.Failures * 2 >= b.Samples;
            if (failedMostly)
            {
                using var red = new SolidBrush(Theme.Bad);
                g.FillRectangle(red, x, plot.Top, barWidth, plot.Height);
                continue;
            }
            var h = Math.Max(2, (int)(plot.Height * Math.Min(1, (b.AverageMs ?? 0) / max)));
            var color = b.AverageMs < 60 ? Theme.Good : b.AverageMs < 150 ? Theme.Warn : Theme.Bad;
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, x, plot.Bottom - h, barWidth, h);
            if (b.Failures > 0)
            {
                // Some checks failed in this slice: a red cap on top.
                using var cap = new SolidBrush(Theme.Bad);
                g.FillRectangle(cap, x, plot.Top, barWidth, 3);
            }
        }

        var fmt = (_to - _from).TotalHours > 30 ? "d MMM HH:mm" : "HH:mm";
        TextRenderer.DrawText(g, _from.ToString(fmt, Loc.Culture), Theme.Small, new Rectangle(plot.Left, plot.Bottom + 4, 140, 18), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, _to.ToString(fmt, Loc.Culture), Theme.Small, new Rectangle(plot.Right - 140, plot.Bottom + 4, 140, 18), Theme.Muted, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, $"{max:0} ms", Theme.Small, new Rectangle(plot.Left, 0, 140, 16), Theme.Muted, TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }
}
