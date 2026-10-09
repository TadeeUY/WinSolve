namespace WinSolve.UI.Pages;

/// <summary>Grid of small utilities, grouped by what they work on.</summary>
public sealed class ToolboxPage : Page
{
    public override string Key => "toolbox";

    public ToolboxPage() : base("Toolbox", "Handy utilities for disks, files, the network and Windows. Tip: press Ctrl+K and type a tool's name.")
    {
        var stack = new Stack(scroll: true);
        foreach (var group in Toolbox.All.GroupBy(t => t.Category))
        {
            var card = new StackCard();
            card.Add(Theme.SectionHeader(group.Key switch
            {
                "Disks & files" => "",
                "Network" => "",
                _ => "",
            }, group.Key, group.Key switch
            {
                "Disks & files" => "Speed, locked files and secure deletion.",
                "Network" => "Connection tests, Wi-Fi and site blocking.",
                _ => "Menus, restore points and a full spec sheet.",
            }));

            var grid = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 4, 0, 0) };
            for (int c = 0; c < 3; c++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            var tools = group.ToList();
            for (int i = 0; i < tools.Count; i++)
            {
                var tool = tools[i];
                var tile = new OptionCard(tool.Glyph, tool.Title, tool.Description)
                {
                    Dock = DockStyle.Fill, Height = 96, Cursor = Cursors.Hand,
                    Margin = new Padding(i % 3 == 0 ? 0 : 5, 0, i % 3 == 2 ? 0 : 5, 10),
                };
                tile.Click += (_, _) => tool.Open(FindForm()!);
                grid.Controls.Add(tile, i % 3, i / 3);
            }
            card.Add(grid);
            stack.Add(card);
        }
        AddRow(stack, fill: true);
    }
}
