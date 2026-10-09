using System.Drawing.Drawing2D;
using WinSolve.Core;
using WinSolve.Localization;

namespace WinSolve.UI;

/// <summary>
/// Vertical stack: lays children out top to bottom at full width.
/// With <c>scroll = true</c> it is used as a page's scrolling content area.
/// </summary>
public class Stack : FlowLayoutPanel
{
    private bool _fitting;

    public Stack(bool scroll = false)
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        BackColor = Color.Transparent;
        Margin = new Padding(0);
        Padding = new Padding(0);
        if (scroll)
        {
            AutoScroll = true;
            Dock = DockStyle.Fill;
        }
        else
        {
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
        }
        DoubleBuffered = true;
    }

    public Stack Add(params Control[] controls)
    {
        Controls.AddRange(controls);
        return this;
    }

    // Don't jump to the focused control when the content changes.
    protected override Point ScrollToControl(Control activeControl) => DisplayRectangle.Location;

    public static void PinWidth(Control c, int width)
    {
        if (width <= 0) return;
        if (c.AutoSize)
        {
            // Auto-sized controls grow to their content: pin the width through the size limits.
            c.MinimumSize = new Size(width, c.MinimumSize.Height);
            c.MaximumSize = new Size(width, 0);
        }
        c.Width = width;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        if (!_fitting)
        {
            _fitting = true;
            try
            {
                var inner = ClientSize.Width - Padding.Horizontal;
                foreach (Control c in Controls)
                {
                    if (c is Label { AutoSize: true } || c is StatusLabel || Equals(c.Tag, "nofill")) continue;
                    PinWidth(c, inner - c.Margin.Horizontal);
                }
            }
            finally
            {
                _fitting = false;
            }
        }
        base.OnLayout(e);
    }
}

/// <summary>Card whose height follows its content (an inner <see cref="Stack"/>).</summary>
public sealed class StackCard : Card
{
    public Stack Body { get; } = new();

    public StackCard()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Controls.Add(Body);
        Body.Location = new Point(Padding.Left, Padding.Top);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        Stack.PinWidth(Body, ClientSize.Width - Padding.Horizontal);
    }

    protected override void OnPaddingChanged(EventArgs e)
    {
        base.OnPaddingChanged(e);
        Body.Location = new Point(Padding.Left, Padding.Top);
    }

    public StackCard Add(params Control[] controls)
    {
        Body.Add(controls);
        return this;
    }
}

/// <summary>Thin horizontal divider line.</summary>
public sealed class Divider : Control
{
    public Divider()
    {
        Height = 1;
        BackColor = Theme.Divider;
        Margin = new Padding(0, 6, 0, 6);
    }
}

/// <summary>Read-only output console, safe to call from any thread.</summary>
public sealed class LogBox : RichTextBox
{
    public LogBox()
    {
        ReadOnly = true;
        BackColor = Color.FromArgb(24, 24, 24);
        ForeColor = Theme.Muted;
        BorderStyle = BorderStyle.None;
        Font = Theme.Mono;
        WordWrap = true;
        DetectUrls = false;
        ScrollBars = RichTextBoxScrollBars.Vertical;
    }

    public void AppendLine(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLine(line));
            return;
        }
        SelectionStart = TextLength;
        SelectionColor = line.StartsWith("ERROR", StringComparison.Ordinal) || line.Contains(" ERROR", StringComparison.Ordinal) ? Theme.Bad
            : line.StartsWith("Done", StringComparison.Ordinal) || line.StartsWith("OK", StringComparison.Ordinal) ? Theme.Good
            : line.StartsWith('>') ? Theme.Text
            : Theme.Muted;
        AppendText(Loc.T(line) + Environment.NewLine);
        ScrollToCaret();
        Logger.Write(line);
    }
}

/// <summary>Thin flat progress bar in the accent color.</summary>
public sealed class ProgressLine : Control
{
    private double _value;
    private bool _indeterminate;
    private int _phase;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };

    public ProgressLine()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Height = 4;
        _timer.Tick += (_, _) => { _phase = (_phase + 5) % Math.Max(1, Width + 160); Invalidate(); };
    }

    /// <summary>0..1</summary>
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); Invalidate(); }
    }

    public bool Indeterminate
    {
        get => _indeterminate;
        set { _indeterminate = value; _timer.Enabled = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Theme.Background);
        using (var bg = new SolidBrush(Theme.Border)) g.FillRectangle(bg, 0, Height / 2 - 1, Width, 2);

        using var fg = new SolidBrush(Theme.Accent);
        if (_indeterminate)
        {
            var r = Rectangle.Intersect(new Rectangle(_phase - 160, 0, 160, Height), ClientRectangle);
            if (r.Width > 0) g.FillRectangle(fg, r);
        }
        else if (_value > 0)
        {
            g.FillRectangle(fg, 0, 0, (int)(Width * _value), Height);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Sidebar navigation item (icon + text), Windows 11 Settings style.</summary>
public sealed class NavButton : Control
{
    private bool _selected;

    public string Glyph { get; }
    public string Key { get; }

    public NavButton(string key, string glyph, string text)
    {
        Key = key;
        Glyph = glyph;
        Text = text;
        Height = 36;
        Margin = new Padding(0, 1, 0, 1);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    private double _hoverT, _selectT;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            // The accent bar grows from the middle, like Windows 11.
            Animator.Animate((this, "sel"), () => _selectT, value ? 1 : 0, v => { _selectT = v; Invalidate(); }, value ? 260 : 120);
        }
    }

    protected override void OnMouseEnter(EventArgs e) { FadeHover(true); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { FadeHover(false); base.OnMouseLeave(e); }

    private void FadeHover(bool on) => Animator.Animate((this, "hover"), () => _hoverT, on ? 1 : 0, v => { _hoverT = v; Invalidate(); }, 120);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Sidebar);

        var rect = new Rectangle(6, 1, Width - 12, Height - 2);
        var bg = Math.Max(_hoverT * 0.75, _selectT);
        if (bg > 0.01)
        {
            using var path = Theme.RoundedRect(rect, 4);
            using var b = new SolidBrush(Animator.Blend(Theme.Sidebar, Color.FromArgb(45, 45, 45), bg));
            g.FillPath(b, path);
        }
        if (_selectT > 0.01)
        {
            var full = Height - 22;
            var h = Math.Max(2, (int)(full * _selectT));
            using var accent = new SolidBrush(Color.FromArgb((int)(255 * Math.Min(1, _selectT * 2)), Theme.Accent));
            using var bar = Theme.RoundedRect(new Rectangle(6, Height / 2 - h / 2, 3, h), 1);
            g.FillPath(accent, bar);
        }

        TextRenderer.DrawText(g, Glyph, Theme.Icons, new Rectangle(18, 0, 24, Height), Theme.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Loc.T(Text), Theme.Body, new Rectangle(52, 0, Width - 56, Height), _selected ? Theme.Text : Color.FromArgb(220, 220, 220),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Runs long work with live output, a progress bar and a Cancel button.</summary>
public sealed class TaskRunnerView : TableLayoutPanel
{
    private readonly LogBox _log = new() { Dock = DockStyle.Fill };
    private readonly ProgressLine _progress = new() { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 0, 6) };
    private readonly Label _status = Theme.Label("Ready", Theme.Small, Theme.Muted);
    private readonly FlatBtn _cancel;
    private CancellationTokenSource? _cts;

    public bool IsBusy => _cts is not null;

    public event Action<bool>? BusyChanged;

    public TaskRunnerView()
    {
        ColumnCount = 2;
        RowCount = 3;
        BackColor = Color.Transparent;
        Dock = DockStyle.Fill;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        RowStyles.Add(new RowStyle(SizeType.Absolute, 14));
        RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _cancel = Theme.Button("Cancel", (_, _) => _cts?.Cancel());
        _cancel.Enabled = false;
        _status.Anchor = AnchorStyles.Left;

        Controls.Add(_status, 0, 0);
        Controls.Add(_cancel, 1, 0);
        Controls.Add(_progress, 0, 1);
        SetColumnSpan(_progress, 2);
        var logCard = new Card { Dock = DockStyle.Fill, Fill = Color.FromArgb(24, 24, 24), Padding = new Padding(10, 8, 6, 8), Margin = new Padding(0) };
        logCard.Controls.Add(_log);
        Controls.Add(logCard, 0, 2);
        SetColumnSpan(logCard, 2);
    }

    public void Log(string line) => _log.AppendLine(line);

    public void Clear() => _log.Clear();

    /// <summary>Runs <paramref name="work"/> showing progress. Returns false if canceled or failed.</summary>
    public async Task<bool> RunAsync(string title, Func<Action<string>, Action<int, int>, CancellationToken, Task> work)
    {
        if (_cts is not null) return false;
        _cts = new CancellationTokenSource();
        _cancel.Enabled = true;
        _progress.Value = 0;
        _progress.Indeterminate = true;
        _status.Text = title + "...";
        _status.ForeColor = Theme.Text;
        BusyChanged?.Invoke(true);
        Log($"> {title} ({DateTime.Now:G})");

        void Progress(int done, int total)
        {
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                _progress.Indeterminate = false;
                _progress.Value = total == 0 ? 1 : (double)done / total;
                _status.Text = $"{title}... {done}/{total}";
            });
        }

        var ok = false;
        try
        {
            await Task.Run(() => work(Log, Progress, _cts.Token));
            ok = true;
            _status.Text = $"{title}: completed";
            _status.ForeColor = Theme.Good;
        }
        catch (OperationCanceledException)
        {
            Log("Canceled.");
            _status.Text = $"{title}: canceled";
            _status.ForeColor = Theme.Warn;
        }
        catch (Exception ex)
        {
            Log($"ERROR: {ex.Message}");
            _status.Text = $"{title}: failed";
            _status.ForeColor = Theme.Bad;
        }
        finally
        {
            _progress.Indeterminate = false;
            _progress.Value = ok ? 1 : _progress.Value;
            _cancel.Enabled = false;
            _cts.Dispose();
            _cts = null;
            BusyChanged?.Invoke(false);
        }
        return ok;
    }

    /// <summary>Runs catalog tasks in order.</summary>
    public Task<bool> RunTasksAsync(IReadOnlyList<SystemTask> tasks, Action<TaskContext>? done = null)
    {
        return RunAsync(tasks.Count == 1 ? tasks[0].Title : $"Running {tasks.Count} tasks", async (log, progress, ct) =>
        {
            var ctx = new TaskContext(log, ct);
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                log($"> {t.Title}");
                try
                {
                    await t.Run(ctx);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { log($"ERROR  {t.Title}: {ex.Message}"); }
                progress(i + 1, tasks.Count);
            }
            log($"Done. {(ctx.FreedBytes > 0 ? $"Freed {Format.Bytes(ctx.FreedBytes)}. " : "")}{(ctx.RebootRecommended ? "Restart the PC to finish." : "")}");
            done?.Invoke(ctx);
        });
    }
}

/// <summary>Base page: title, subtitle and a single-column row layout.</summary>
public abstract class Page : UserControl
{
    protected readonly TableLayoutPanel Root;

    public abstract string Key { get; }

    /// <summary>Icon of each page (Segoe Fluent Icons / MDL2), also used in the sidebar.</summary>
    public static readonly Dictionary<string, string> PageGlyphs = new()
    {
        ["home"] = "\uE80F", ["optimize"] = "\uE945", ["tools"] = "\uE90F", ["space"] = "\uEDA2",
        ["monitor"] = "\uE9D9", ["hardware"] = "\uE950", ["drivers"] = "\uE772", ["tweaks"] = "\uE9E9",
        ["startup"] = "\uE7E8", ["apps"] = "\uE71D", ["activation"] = "\uE8D7", ["settings"] = "\uE713",
    };

    // Not FindForm(): a page that isn't on screen (e.g. after navigating away mid-await) has no form.
    protected MainForm Main => (MainForm?)FindForm() ?? Application.OpenForms.OfType<MainForm>().First();

    /// <summary>True while one of this page's task runners is working.</summary>
    public bool IsBusy => Runners(this).Any(r => r.IsBusy);

    private static IEnumerable<TaskRunnerView> Runners(Control c)
    {
        foreach (Control child in c.Controls)
        {
            if (child is TaskRunnerView r) yield return r;
            foreach (var nested in Runners(child)) yield return nested;
        }
    }

    protected Page(string title, string subtitle)
    {
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Dock = DockStyle.Fill;
        Padding = new Padding(32, 20, 32, 16);
        DoubleBuffered = true;

        Root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        Root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(Root);

        // Header: icon tile + title and subtitle.
        var header = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 14),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var text = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        var sub = Theme.Label(subtitle, Theme.Body, Theme.Muted);
        sub.MaximumSize = new Size(1000, 0);
        text.Controls.Add(Theme.Label(title, Theme.H1));
        text.Controls.Add(sub);
        header.Controls.Add(new IconTile(PageGlyphs.GetValueOrDefault(Key, "\uE80F")) { Margin = new Padding(0, 2, 14, 0) }, 0, 0);
        header.Controls.Add(text, 1, 0);
        AddRow(header);
        HandleCreated += (_, _) => Loc.Apply(this);
    }

    /// <summary>Adds a row. <paramref name="fill"/> takes the remaining height.</summary>
    protected void AddRow(Control control, bool fill = false, int? height = null)
    {
        Root.RowCount++;
        Root.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100)
            : height is { } h ? new RowStyle(SizeType.Absolute, h)
            : new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Fill;
        Root.Controls.Add(control, 0, Root.RowCount - 1);
    }

    /// <summary>Called every time the page is shown.</summary>
    public virtual void OnShown() { }

    /// <summary>Called when another page is shown or the window goes to the notification area.</summary>
    public virtual void OnHidden() { }

    protected bool Confirm(string message, string title = "WinSolve")
    {
        if (!AppSettings.Current.ConfirmActions) return true;
        return Loc.Show(this, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    /// <summary>Always asks, regardless of settings (destructive actions).</summary>
    protected bool ConfirmDanger(string message, string title = "WinSolve")
        => Loc.Show(this, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    protected void Info(string message, string title = "WinSolve")
        => Loc.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    protected static void AskReboot(IWin32Window owner, string message = "Some changes need a restart. Restart now?")
    {
        if (Loc.Show(owner, message, "WinSolve", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            ProcessRunner.Reboot();
    }
}
