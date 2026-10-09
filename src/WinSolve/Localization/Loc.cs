using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WinSolve.Core;

namespace WinSolve.Localization;

/// <summary>
/// UI translation. English is the source language: every visible string is looked up in the
/// selected language's table, first by exact match and then against templates with
/// placeholders ("{0} of {1} tweaks on"), so text built at runtime is translated too.
/// Captured placeholder values are translated recursively when they are known strings.
/// </summary>
public static class Loc
{
    private sealed record Template(Regex Pattern, string Target, int LiteralLength);

    private static readonly Dictionary<string, Dictionary<string, string>> Tables = new()
    {
        ["es"] = Spanish.Table,
    };

    private static readonly ConcurrentDictionary<string, List<Template>> TemplateCache = new();
    private static readonly ConcurrentDictionary<string, string> Memo = new();
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    public static string Language => AppSettings.Current.Language;

    public static bool IsEnglish => Language is not ("es");

    /// <summary>Translates a UI string into the current language (returns it unchanged if unknown).</summary>
    public static string T(string? text)
    {
        if (string.IsNullOrEmpty(text) || IsEnglish || !Tables.TryGetValue(Language, out var table)) return text ?? "";
        var key = Language + "\u0001" + text;
        if (Memo.TryGetValue(key, out var cached)) return cached;
        var result = Translate(text, table, depth: 0);
        if (Memo.Count < 20000) Memo[key] = result;
        return result;
    }

    private static string Translate(string text, Dictionary<string, string> table, int depth)
    {
        // Keep leading indentation and trailing punctuation-free whitespace intact.
        var trimmed = text.TrimStart();
        var indent = text[..(text.Length - trimmed.Length)];
        if (table.TryGetValue(trimmed, out var exact)) return indent + exact;

        // Multi-line text: translate each line on its own.
        if (trimmed.Contains('\n'))
        {
            if (depth > 0) return text;
            return indent + string.Join("\n", trimmed.Split('\n').Select(l => Translate(l, table, depth + 1)));
        }

        if (depth > 2) return text;
        foreach (var t in Templates(table))
        {
            var m = t.Pattern.Match(trimmed);
            if (!m.Success) continue;
            var groups = m.Groups.Cast<Group>().Skip(1).Select(g => g.Value).ToArray();
            var args = groups.Select(g => Translate(g, table, depth + 1)).ToArray();

            // Short, generic templates ("{0} of {1}", "• {0}") would otherwise swallow whole
            // sentences: only accept them when every captured part is data (has a digit) or
            // is itself a known string.
            if (t.LiteralLength < 8 && groups.Where((g, i) => !g.Any(char.IsDigit) && args[i] == g).Any())
                continue;

            try { return indent + string.Format(t.Target, args.Cast<object>().ToArray()); }
            catch (FormatException) { return text; }
        }
        return text;
    }

    private static List<Template> Templates(Dictionary<string, string> table)
        => TemplateCache.GetOrAdd(Language, _ => table
            .Where(kv => Placeholder.IsMatch(kv.Key))
            .OrderByDescending(kv => Placeholder.Replace(kv.Key, "").Length) // most specific first
            .Select(kv => new Template(
                new Regex("^" + Placeholder.Replace(Regex.Escape(kv.Key).Replace(@"\{", "{"), "(.+?)") + "$",
                    RegexOptions.Singleline | RegexOptions.Compiled),
                kv.Value,
                Placeholder.Replace(kv.Key, "").Trim().Length))
            .ToList());

    /// <summary>Clears caches after the language changes.</summary>
    public static void Reset()
    {
        Memo.Clear();
        TemplateCache.Clear();
    }

    /// <summary>Translates grid headers and texts of controls that were not created through the theme.</summary>
    public static void Apply(Control root)
    {
        if (IsEnglish) return;
        foreach (Control c in root.Controls)
        {
            if (c is DataGridView g)
                foreach (DataGridViewColumn col in g.Columns) col.HeaderText = T(col.HeaderText);
            else if (c is LinkLabel or RadioButton) c.Text = T(c.Text);
            Apply(c);
        }
    }

    public static DialogResult Show(IWin32Window? owner, string text, string caption = "WinSolve",
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
        => owner is null
            ? MessageBox.Show(T(text), T(caption), buttons, icon, defaultButton)
            : MessageBox.Show(owner, T(text), T(caption), buttons, icon, defaultButton);
}

/// <summary>Label whose text is translated whenever it is set.</summary>
public class LocLabel : Label
{
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => base.Text;
        set => base.Text = Loc.T(value);
    }
}

/// <summary>
/// CheckBox whose text is translated whenever it is set, painted as a Windows 11 check box
/// or, with <see cref="Switch"/>, as an on/off toggle.
/// </summary>
public sealed class LocCheckBox : CheckBox
{
    private bool _hover;
    private bool _switch;

    public LocCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => base.Text;
        set { base.Text = Loc.T(value); AdjustSize(); }
    }

    /// <summary>Draw as a toggle switch instead of a check box.</summary>
    public bool Switch
    {
        get => _switch;
        set { _switch = value; AdjustSize(); Invalidate(); }
    }

    private int MarkWidth => _switch ? 40 : 20;

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix);
        return new Size(MarkWidth + 10 + text.Width, Math.Max(_switch ? 22 : 20, text.Height) + 4);
    }

    private void AdjustSize()
    {
        if (AutoSize) Size = GetPreferredSize(Size.Empty);
    }

    private double _knob = double.NaN;

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        if (!_switch || double.IsNaN(_knob) || !IsHandleCreated || !Visible) { _knob = Checked ? 1 : 0; return; }
        WinSolve.UI.Animator.Animate(this, () => _knob, Checked ? 1 : 0, v => { _knob = v; Invalidate(); }, 170);
    }

    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); AdjustSize(); }
    protected override void OnMouseEnter(EventArgs eventargs) { _hover = true; Invalidate(); base.OnMouseEnter(eventargs); }
    protected override void OnMouseLeave(EventArgs eventargs) { _hover = false; Invalidate(); base.OnMouseLeave(eventargs); }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.Clear(WinSolve.UI.Theme.SurfaceColor(this));
        var mid = Height / 2;
        if (_switch)
        {
            if (double.IsNaN(_knob)) _knob = Checked ? 1 : 0;
            WinSolve.UI.Theme.DrawSwitch(g, new Rectangle(0, mid - 10, 40, 20), _knob, _hover, Enabled);
        }
        else
            WinSolve.UI.Theme.DrawCheckBox(g, new Rectangle(0, mid - 9, 18, 18), Checked, _hover, Enabled);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(MarkWidth + 8, 0, Width - MarkWidth - 8, Height),
            Enabled ? ForeColor : Color.FromArgb(120, 120, 120),
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}
