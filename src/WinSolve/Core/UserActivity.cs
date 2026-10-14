using System.Runtime.InteropServices;

namespace WinSolve.Core;

/// <summary>Whether the person at the PC is using it right now (for background work like updates).</summary>
public static class UserActivity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>Time since the last keyboard or mouse input in this session.</summary>
    public static TimeSpan Idle
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
        }
    }

    /// <summary>A full-screen game or video, a presentation, or "do not disturb" / quiet time is on.</summary>
    public static bool InFullScreenOrQuiet
    {
        get
        {
            try
            {
                // 2 busy (full-screen app), 3 Direct3D full screen, 4 presentation mode, 6 quiet time.
                return SHQueryUserNotificationState(out var s) == 0 && s is 2 or 3 or 4 or 6;
            }
            catch { return false; }
        }
    }
}
