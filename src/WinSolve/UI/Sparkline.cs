using System.Drawing.Drawing2D;

namespace WinSolve.UI;

/// <summary>Small line chart of the last N values, filled below the line.</summary>
public sealed class Sparkline : Control
{
    private readonly Queue<double> _values = new();

    public int Capacity { get; set; } = 60;

    /// <summary>Upper bound of the Y axis; 0 = scale to the data.</summary>
    public double Max { get; set; } = 100;

    public Sparkline()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 48;
        BackColor = Theme.Card;
    }

    public void Add(double value)
    {
        _values.Enqueue(value);
        while (_values.Count > Capacity) _values.Dequeue();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var grid = new Pen(Theme.Divider)) g.DrawLine(grid, 0, Height - 1, Width, Height - 1);
        if (_values.Count < 2) return;

        var values = _values.ToArray();
        var max = Max > 0 ? Max : Math.Max(1, values.Max() * 1.15);
        var step = (Width - 1) / (float)(Capacity - 1);
        var x0 = (Width - 1) - step * (values.Length - 1);
        var points = values.Select((v, i) => new PointF(x0 + i * step, (float)(Height - 2 - Math.Clamp(v / max, 0, 1) * (Height - 4)))).ToArray();

        using var area = new GraphicsPath();
        area.AddLines(points);
        area.AddLine(points[^1], new PointF(points[^1].X, Height));
        area.AddLine(new PointF(points[^1].X, Height), new PointF(points[0].X, Height));
        area.CloseFigure();
        using (var fill = new SolidBrush(Color.FromArgb(40, Theme.Accent))) g.FillPath(fill, area);
        using var pen = new Pen(Theme.Accent, 1.5f);
        g.DrawLines(pen, points);
    }
}
