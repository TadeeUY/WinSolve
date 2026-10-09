namespace WinSolve.UI;

/// <summary>
/// Drives small UI animations (hover fades, switch knobs, indicators) from a single UI-thread
/// timer, so dozens of controls don't each need their own timer.
/// </summary>
public static class Animator
{
    private static readonly System.Windows.Forms.Timer Timer = new() { Interval = 15 };
    private static readonly Dictionary<object, Func<bool>> Running = [];

    static Animator()
    {
        Timer.Tick += (_, _) =>
        {
            foreach (var (key, step) in Running.ToList())
            {
                bool done;
                try { done = step(); }
                catch { done = true; }
                if (done) Running.Remove(key);
            }
            if (Running.Count == 0) Timer.Stop();
        };
    }

    public static bool Enabled => Core.AppSettings.Current.Animations;

    /// <summary>
    /// Moves <paramref name="value"/> toward <paramref name="target"/> over roughly
    /// <paramref name="ms"/> milliseconds, calling <paramref name="apply"/> each frame.
    /// Starting a new animation for the same owner replaces the previous one.
    /// </summary>
    public static void Animate(object owner, Func<double> value, double target, Action<double> apply, int ms = 150)
    {
        if (!Enabled || ms <= 0)
        {
            Running.Remove(owner);
            apply(target);
            return;
        }
        var start = value();
        if (Math.Abs(start - target) < 0.001)
        {
            apply(target);
            return;
        }
        var began = Environment.TickCount64;
        Running[owner] = () =>
        {
            var t = Math.Clamp((Environment.TickCount64 - began) / (double)ms, 0, 1);
            apply(start + (target - start) * Ease.OutCubic(t));
            return t >= 1;
        };
        if (!Timer.Enabled) Timer.Start();
    }

    public static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }
}
