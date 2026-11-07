using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinSolve.UI;

/// <summary>
/// Drives small UI animations (hover fades, switch knobs, the side menu...) from one clock, so dozens
/// of controls don't each need their own timer.
/// </summary>
/// <remarks>
/// The clock is tied to the screen refresh: a helper thread waits for the next frame of the Windows
/// compositor (DwmFlush) and asks the UI thread to move every animation once. A WinForms Timer is
/// the last message Windows delivers (after input and painting), so under load its ticks bunch up
/// and the motion stutters; and <see cref="Environment.TickCount64"/> only changes every ~15 ms,
/// so progress was measured in steps. Progress now comes from the high-resolution clock.
/// </remarks>
public static class Animator
{
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();

    private static readonly Dictionary<object, Func<bool>> Running = [];
    private static readonly object PumpLock = new();
    private static Thread? _pump;
    private static bool _active;
    private static int _queued;   // a step is already waiting in the UI queue: don't pile up more
    private static SynchronizationContext? _ui;

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
        var began = Stopwatch.GetTimestamp();
        Running[owner] = () =>
        {
            var t = Math.Clamp(Stopwatch.GetElapsedTime(began).TotalMilliseconds / ms, 0, 1);
            apply(start + (target - start) * Ease.OutCubic(t));
            return t >= 1;
        };
        Wake();
    }

    /// <summary>Starts the frame clock if it is not running (UI thread).</summary>
    private static void Wake()
    {
        _ui ??= SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        lock (PumpLock)
        {
            _active = true;
            if (_pump is not null) return;
            _pump = new Thread(Pump) { IsBackground = true, Name = "WinSolve animation clock", Priority = ThreadPriority.AboveNormal };
            _pump.Start();
        }
    }

    private static void Pump()
    {
        while (true)
        {
            lock (PumpLock)
            {
                if (!_active)
                {
                    _pump = null;
                    return;
                }
            }
            // Blocks until the next screen frame (about every 16 ms); if the compositor is not there, a plain wait.
            int hr;
            try { hr = DwmFlush(); }
            catch { hr = -1; }
            if (hr != 0) Thread.Sleep(8);
            if (Interlocked.Exchange(ref _queued, 1) == 0)
                _ui!.Post(_ => { Interlocked.Exchange(ref _queued, 0); Step(); }, null);
        }
    }

    /// <summary>One frame, on the UI thread.</summary>
    private static void Step()
    {
        foreach (var (key, step) in Running.ToList())
        {
            bool done;
            try { done = step(); }
            catch { done = true; }
            if (done) Running.Remove(key);
        }
        if (Running.Count == 0)
        {
            lock (PumpLock) _active = false;
        }
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
