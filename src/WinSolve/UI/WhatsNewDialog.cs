using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>"What's new" after an update, in the app's language.</summary>
public sealed class WhatsNewDialog : Form
{
    public WhatsNewDialog(IReadOnlyList<ChangelogEntry> entries)
    {
        Text = "WinSolve";
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        Size = new Size(640, 520);
        Padding = new Padding(28, 22, 28, 18);
        AccessibleName = Loc.T("What's new");

        var spanish = !Loc.IsEnglish;
        var header = Theme.SectionHeader("", string.Format(Loc.T("What's new in WinSolve {0}"), entries[0].Version.ToString(3)),
            Loc.T("Thanks for keeping WinSolve up to date."));
        header.Dock = DockStyle.Top;

        var list = new Stack(scroll: true) { Padding = new Padding(0, 10, 0, 0) };
        foreach (var entry in entries)
        {
            if (entries.Count > 1)
                list.Add(Theme.Label(string.Format(Loc.T("Version {0}"), entry.Version.ToString(3)), Theme.BodyBold, Theme.Muted));
            foreach (var item in entry.Items)
            {
                var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 6) };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 44));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                row.Controls.Add(new IconTile(item.Glyph) { Size = new Size(32, 32), Margin = new Padding(0, 2, 10, 0) }, 0, 0);
                // A plain auto-sized label wraps at its own maximum width; Theme.Paragraph fits itself
                // to the parent's whole width, which here is the row (icon included), cutting lines.
                var text = new LocLabel
                {
                    Text = spanish ? item.Spanish : item.English,
                    AutoSize = true,
                    MaximumSize = new Size(480, 0),
                    UseMnemonic = false,
                    Font = Theme.Body,
                    ForeColor = Theme.Muted,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0, 0, 0, 6),
                };
                row.Controls.Add(text, 1, 0);
                list.Add(row);
                Stack.PinWidth(row, 560);
            }
        }

        // Room below the last item so its second line isn't cut off by the scroll area.
        list.Add(new Panel { Height = 24, Width = 10, BackColor = Color.Transparent });

        var ok = Theme.Button("Got it", (_, _) => Close(), primary: true);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, BackColor = Color.Transparent };
        bar.Controls.Add(ok);
        AcceptButton = ok;

        Controls.Add(list);
        Controls.Add(bar);
        Controls.Add(header);
        HandleCreated += (_, _) => Theme.StyleWindow(this);
    }
}
