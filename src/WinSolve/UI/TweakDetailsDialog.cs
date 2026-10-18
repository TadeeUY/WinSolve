using System.Text;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>"What does this change?": the exact registry values, keys, services and commands of a tweak.</summary>
public static class TweakDetails
{
    public static string KindName(TweakChangeKind kind) => Loc.T(kind switch
    {
        TweakChangeKind.RegistryValue => "Registry value",
        TweakChangeKind.RegistryKey => "Registry key",
        TweakChangeKind.Service => "Service",
        _ => "Command",
    });

    /// <summary>Short form for a tooltip: the first few changes, then how many more there are.</summary>
    public static string Summary(Tweak tweak, int max = 4)
    {
        if (tweak.Changes.Count == 0) return "";
        var sb = new StringBuilder(Loc.T("What it changes")).Append(':');
        foreach (var c in tweak.Changes.Take(max))
            sb.Append("\n• ").Append(Loc.T(c.Target)).Append(" → ").Append(Loc.T(c.NewValue));
        if (tweak.Changes.Count > max) sb.Append('\n').Append(Loc.T($"...and {tweak.Changes.Count - max} more"));
        sb.Append('\n').Append(Loc.T("Click the (i) for the full list."));
        return sb.ToString();
    }

    /// <summary>Plain text of every change, for copying.</summary>
    public static string FullText(Tweak tweak)
    {
        var sb = new StringBuilder(Loc.T(tweak.Title)).Append(" (").Append(tweak.Id).Append(")\n");
        foreach (var c in tweak.Changes)
            sb.Append('\n').Append(KindName(c.Kind)).Append(": ").Append(Loc.T(c.Target))
              .Append("\n  → ").Append(Loc.T(c.NewValue))
              .Append('\n').Append("  ").Append(Loc.T("Undo")).Append(": ").Append(Loc.T(c.Undo)).Append('\n');
        return sb.ToString();
    }
}

internal sealed class TweakDetailsDialog : ToolDialog
{
    public TweakDetailsDialog(Tweak tweak) : base("", "What does this change?", tweak.Title, new Size(980, 460))
    {
        var grid = Grid(("kind", "Type", 70), ("target", "What", 300), ("value", "Becomes", 170), ("undo", "Undo", 170));
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        grid.Columns["target"]!.DefaultCellStyle.Font = Theme.Mono;
        foreach (var c in tweak.Changes)
            grid.Rows.Add(TweakDetails.KindName(c.Kind), Loc.T(c.Target), Loc.T(c.NewValue), Loc.T(c.Undo));
        AddRow(Theme.InCard(grid), fill: true);
        Buttons.Controls.Add(Theme.Button("Copy", (_, _) =>
        {
            try
            {
                Clipboard.SetText(TweakDetails.FullText(tweak));
                Status.Text = Loc.T("Copied.");
            }
            catch (Exception ex) { Status.Text = ex.Message; }
        }, glyph: ""));
        Status.Text = Loc.T(tweak.AllUsers ? "Applies to all users of this PC." : "Applies to your account only.");
    }
}
