using System.Runtime.InteropServices;
using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>
/// Counts screen time while WinSolve runs (also from the notification area) and applies the lock
/// schedule. Screen time is time with the session unlocked and the display on, while the user is
/// at the PC (input in the last 5 minutes) or an app keeps the display on (a video, a game).
/// </summary>
public sealed class ScreenTimeWatcher : NativeWindow, IDisposable
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SaveEvery = TimeSpan.FromMinutes(5);

    public static ScreenTimeWatcher? Instance { get; private set; }

    private readonly string _path = ScreenTimeStore.CurrentPath;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15_000 };
    private readonly LockEnforcer _enforcer = new();
    private IntPtr _displayNotification;
    private DateTime _lastTick = DateTime.Now, _lastSave = DateTime.Now;
    private bool _locked, _displayOn = true, _dirty;

    public ScreenTimeData Data { get; }

    /// <summary>When the PC locks next during the lock hours (null otherwise).</summary>
    public DateTime? LockAt => _enforcer.LockAt;

    private ScreenTimeWatcher()
    {
        Data = ScreenTimeStore.Load(_path);
        _enforcer.Relock = TimeSpan.FromMinutes(Data.Schedule.RelockMinutes);
        CreateHandle(new CreateParams { Caption = "WinSolve screen time" });
        try
        {
            WTSRegisterSessionNotification(Handle, NOTIFY_FOR_THIS_SESSION);
            var display = GUID_CONSOLE_DISPLAY_STATE;
            _displayNotification = RegisterPowerSettingNotification(Handle, ref display, DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        catch (Exception ex) { Logger.Write($"[screen time] Session notifications unavailable: {ex.Message}"); }
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>Starts watching (once, on the UI thread).</summary>
    public static void Start()
    {
        if (Instance is not null) return;
        try { Instance = new ScreenTimeWatcher(); }
        catch (Exception ex) { Logger.Write($"[screen time] Could not start: {ex.Message}"); }
    }

    public static void Stop()
    {
        Instance?.Dispose();
        Instance = null;
    }

    /// <summary>Saves a changed schedule and applies it right away.</summary>
    public void UpdateSchedule(LockSchedule schedule)
    {
        schedule.Normalize();
        Data.Schedule = schedule;
        _enforcer.Relock = TimeSpan.FromMinutes(schedule.RelockMinutes);
        if (!schedule.IsActive(DateTime.Now)) _enforcer.OnLocked();
        Logger.Write(schedule.Enabled
            ? $"[screen time] Lock schedule on: {LockSchedule.Clock(schedule.StartMinute)}-{LockSchedule.Clock(schedule.EndMinute)}, days {string.Join(",", schedule.Days)}, locks again every {schedule.RelockMinutes} min."
            : "[screen time] Lock schedule off.");
        Save();
        Tick();
    }

    public static void LockNow()
    {
        Logger.Write("[screen time] Locking the PC.");
        if (!LockWorkStation()) Logger.Write($"[screen time] LockWorkStation failed ({Marshal.GetLastWin32Error()}).");
    }

    private bool InUse => !_locked && _displayOn && (IdleTime() < Idle || DisplayRequired());

    internal void Tick()
    {
        var now = DateTime.Now;
        var elapsed = now - _lastTick;
        _lastTick = now;
        var lockHours = Data.Schedule.IsActive(now);
        // A gap (sleep, clock change) isn't screen time.
        if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(2) && InUse)
        {
            Data.Add(now, (int)Math.Round(elapsed.TotalSeconds), lockHours);
            _dirty = true;
        }

        switch (_enforcer.Tick(now, lockHours, unlocked: !_locked))
        {
            case LockEnforcer.Action.Warn:
                Warn(now);
                break;
            case LockEnforcer.Action.Lock:
                Logger.Write("[screen time] Lock hours: locking the PC.");
                LockNow();
                break;
        }

        if (_dirty && now - _lastSave >= SaveEvery) Save();
    }

    private void Warn(DateTime now)
    {
        var until = Data.Schedule.EndsAt(now) is { } end ? end.ToString("HH:mm") : "";
        Toast.Show(new ToastOptions
        {
            Title = "The PC locks in 1 minute",
            Detail = string.Format(Loc.T("Lock hours until {0}. You can unlock it, but it locks again every {1} minutes."), until, Data.Schedule.RelockMinutes),
            Glyph = "",
            Severity = IssueSeverity.Warning,
            PrimaryText = "Lock now",
            Primary = LockNow,
            SecondaryText = "OK",
            AutoCloseSeconds = 50,
            Sound = true,
        });
    }

    private void OnSessionLocked()
    {
        if (_locked) return;
        CountUntilNow();
        _locked = true;
        _enforcer.OnLocked();
        Save();
    }

    private void OnSessionUnlocked()
    {
        if (!_locked) return;
        _locked = false;
        _lastTick = DateTime.Now;
        var now = DateTime.Now;
        if (_enforcer.OnUnlocked(now, Data.Schedule.IsActive(now)))
        {
            Data.CountUnlock(now);
            Logger.Write($"[screen time] Unlocked during the lock hours; locks again at {_enforcer.LockAt:HH:mm}.");
            Save();
            Toast.Show(new ToastOptions
            {
                Title = "Unlocked during lock hours",
                Detail = string.Format(Loc.T("It counts in Screen time. The PC locks again at {0}."), $"{_enforcer.LockAt:HH:mm}"),
                Glyph = "",
                SecondaryText = "OK",
                AutoCloseSeconds = 10,
                Sound = false,
            });
        }
    }

    /// <summary>Adds the time since the last tick before the state changes.</summary>
    private void CountUntilNow()
    {
        var now = DateTime.Now;
        var elapsed = now - _lastTick;
        _lastTick = now;
        if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromMinutes(2) && InUse)
        {
            Data.Add(now, (int)Math.Round(elapsed.TotalSeconds), Data.Schedule.IsActive(now));
            _dirty = true;
        }
    }

    private void Save()
    {
        if (ScreenTimeStore.Save(Data, _path))
        {
            _dirty = false;
            _lastSave = DateTime.Now;
        }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_WTSSESSION_CHANGE:
                switch ((int)m.WParam)
                {
                    case WTS_SESSION_LOCK or WTS_CONSOLE_DISCONNECT or WTS_REMOTE_DISCONNECT: OnSessionLocked(); break;
                    case WTS_SESSION_UNLOCK: OnSessionUnlocked(); break;
                }
                break;
            case WM_POWERBROADCAST:
                switch ((int)m.WParam)
                {
                    case PBT_APMSUSPEND:
                        CountUntilNow();
                        _displayOn = false;
                        Save();
                        break;
                    case PBT_POWERSETTINGCHANGE when m.LParam != IntPtr.Zero:
                        var setting = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(m.LParam);
                        if (setting.PowerSetting == GUID_CONSOLE_DISPLAY_STATE)
                        {
                            CountUntilNow();
                            // 0 off, 1 on, 2 dimmed (still in use).
                            _displayOn = setting.Data != 0;
                        }
                        break;
                }
                break;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        _timer.Dispose();
        CountUntilNow();
        Save();
        try
        {
            if (Handle != IntPtr.Zero)
            {
                WTSUnRegisterSessionNotification(Handle);
                if (_displayNotification != IntPtr.Zero) UnregisterPowerSettingNotification(_displayNotification);
                DestroyHandle();
            }
        }
        catch { }
    }

    /// <summary>Time since the last keyboard or mouse input.</summary>
    private static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    /// <summary>An app is keeping the display on (video, game, presentation).</summary>
    private static bool DisplayRequired()
    {
        try
        {
            return CallNtPowerInformation(SystemExecutionState, IntPtr.Zero, 0, out var state, sizeof(uint)) == 0
                   && (state & ES_DISPLAY_REQUIRED) != 0;
        }
        catch { return false; }
    }

    private const int WM_WTSSESSION_CHANGE = 0x02B1, WM_POWERBROADCAST = 0x0218;
    private const int WTS_CONSOLE_DISCONNECT = 2, WTS_REMOTE_DISCONNECT = 4, WTS_SESSION_LOCK = 7, WTS_SESSION_UNLOCK = 8;
    private const int PBT_APMSUSPEND = 4, PBT_POWERSETTINGCHANGE = 0x8013;
    private const int NOTIFY_FOR_THIS_SESSION = 0, DEVICE_NOTIFY_WINDOW_HANDLE = 0, SystemExecutionState = 16;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;
    private static Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public uint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);

    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSetting, int flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int level, IntPtr input, int inputLength, out uint output, int outputLength);
}
