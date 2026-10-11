using System.Drawing.Drawing2D;
using System.Reflection;
using WinSolve.Localization;

namespace WinSolve.UI;

/// <summary>
/// Keyboard and screen-reader support for WinSolve's painted controls: they can be reached
/// with Tab, activated with Enter or Space, show a focus ring and tell Narrator what they are.
/// </summary>
internal static class A11y
{
    private static readonly MethodInfo SetStyle = typeof(Control).GetMethod("SetStyle", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo OnClick = typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly PropertyInfo ShowFocusCues = typeof(Control).GetProperty("ShowFocusCues", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>Makes a painted control behave like a button for the keyboard and Narrator.</summary>
    public static void Clickable(Control c, string name, string? description = null, AccessibleRole role = AccessibleRole.PushButton)
    {
        SetStyle.Invoke(c, [ControlStyles.Selectable, true]);
        c.TabStop = true;
        c.AccessibleRole = role;
        c.AccessibleName = Loc.T(name);
        if (!string.IsNullOrEmpty(description)) c.AccessibleDescription = Loc.T(description);
        c.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.Space)
            {
                OnClick.Invoke(c, [EventArgs.Empty]);
                e.Handled = e.SuppressKeyPress = true;
            }
        };
        c.GotFocus += (_, _) => c.Invalidate();
        c.LostFocus += (_, _) => c.Invalidate();
    }

    /// <summary>True when the control has the focus and Windows shows keyboard focus cues.</summary>
    public static bool ShowsFocus(Control c) => c.Focused && (bool)ShowFocusCues.GetValue(c)!;

    /// <summary>Draws the keyboard focus ring (only when the user navigates with the keyboard).</summary>
    public static void DrawFocus(Control c, Graphics g, int radius = 6)
    {
        if (!ShowsFocus(c)) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(Theme.IsHighContrast ? SystemColors.Highlight : Theme.Text, 2f);
        using var path = Theme.RoundedRect(new Rectangle(1, 1, c.Width - 3, c.Height - 3), radius);
        g.DrawPath(pen, path);
    }
}
