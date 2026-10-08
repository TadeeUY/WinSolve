using WinSolve.Core;

namespace WinSolve.UI;

/// <summary>Modal window that runs work and shows its output.</summary>
public sealed class RunDialog : Form
{
    private readonly TaskRunnerView _runner = new();
    private readonly FlatBtn _close;

    private RunDialog(string title)
    {
        Text = Localization.Loc.T(title);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(820, 520);
        MinimizeBox = false;
        ShowInTaskbar = false;
        Padding = new Padding(18);

        _close = Theme.Button("Close", (_, _) => Close(), primary: true);
        _close.Enabled = false;

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, BackColor = Color.Transparent };
        bottom.Controls.Add(_close);
        Controls.Add(_runner);
        Controls.Add(bottom);

        FormClosing += (_, e) => { if (_runner.IsBusy) e.Cancel = true; };
        HandleCreated += (_, _) => Theme.StyleWindow(this);
    }

    public static void RunTasks(IWin32Window owner, string title, IReadOnlyList<SystemTask> tasks)
    {
        using var dlg = new RunDialog(title);
        dlg.Shown += async (_, _) =>
        {
            await dlg._runner.RunTasksAsync(tasks);
            dlg._close.Enabled = true;
        };
        dlg.ShowDialog(owner);
    }

    public static void Run(IWin32Window owner, string title, Func<Action<string>, Action<int, int>, CancellationToken, Task> work)
    {
        using var dlg = new RunDialog(title);
        dlg.Shown += async (_, _) =>
        {
            await dlg._runner.RunAsync(title, work);
            dlg._close.Enabled = true;
        };
        dlg.ShowDialog(owner);
    }
}
