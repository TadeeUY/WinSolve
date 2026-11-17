using System.Reflection;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WinSolve.Core;
using WinSolve.Services;
using Loc = WinSolve.Localization.Loc;

namespace WinSolve.UI.Pages;

/// <summary>
/// The Home page drawn as a web page by the Edge WebView2 engine (Chromium), the way Discord draws its
/// whole window: one GPU surface instead of hundreds of child windows that paint one by one, so the page
/// never shows torn or half-painted frames when the window opens.
/// </summary>
/// <remarks>
/// Same data and actions as <see cref="DashboardPage"/>. The page (embedded in WinSolve.exe) is served
/// from memory under a private address; it can't reach the network, open windows or other pages, and it
/// can't name tasks to run: it asks for an item it was shown (by position) and this class decides.
/// </remarks>
public sealed class WebHomePage : Page
{
    private const string Origin = "https://winsolve.invalid";
    private static readonly string[] NavigationTargets = ["tools", "space", "drivers", "monitor", "optimize"];

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Theme.Background, AllowExternalDrop = false };
    private CoreWebView2Environment? _environment;
    private List<Issue> _issues = [];
    private bool _initStarted, _ready, _scanned, _scanning, _weeklyShown;
    private string _lastSummary = "", _lastStatus = "";
    private System.Windows.Forms.Timer? _watchdog;
    private bool _gaveUp;

    public override string Key => "home";

    public WebHomePage() : base("Home", "Overall health of this PC and the problems that need attention.")
    {
        // The web page draws its own header and content.
        Root.Visible = false;
        Padding = Padding.Empty;
        Controls.Add(_web);
        _web.BringToFront();
        HandleCreated += async (_, _) => await InitAsync();
    }

    // ───────────── Starting the browser ─────────────

    private async Task InitAsync()
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            _environment = await WebView2Support.GetEnvironmentAsync();
            await _web.EnsureCoreWebView2Async(_environment);
            var core = _web.CoreWebView2;
            Lock(core);
            core.WebMessageReceived += OnMessage;
            core.WebResourceRequested += OnResourceRequested;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.ProcessFailed += (_, e) =>
            {
                // The GPU and helper processes are restarted by WebView2 itself; the browser or page process dying isn't.
                if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited)
                    GiveUp($"the browser process failed ({e.ProcessFailedKind})");
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                    GiveUp($"the page did not load ({e.WebErrorStatus})");
            };
            // The page says "ready" as soon as it runs; if it never does, don't leave a blank Home page.
            _watchdog = new System.Windows.Forms.Timer { Interval = 10_000 };
            _watchdog.Tick += (_, _) =>
            {
                _watchdog?.Stop();
                if (!_ready) GiveUp("the page did not answer in 10 seconds");
            };
            _watchdog.Start();
            core.Navigate(Origin + "/index.html");
        }
        catch (Exception ex)
        {
            // Most often the browser process can't use the profile folder: try the account's own folder next time.
            WebView2Support.RememberProfileProblem();
            GiveUp($"it could not start (profile in {(WebView2Support.ProfileInUserFolder ? "the account's folder" : "the admin-only folder")}): 0x{ex.HResult:X8} {ex}");
        }
    }

    /// <summary>
    /// The engine didn't work on this PC: remember it for this version and show the classic Home page
    /// instead, right away.
    /// </summary>
    private void GiveUp(string reason)
    {
        if (_gaveUp || IsDisposed) return;
        _gaveUp = true;
        Logger.Write($"The Chromium Home page was dropped, the classic one is used: {reason}");
        WebView2Support.MarkFailed();
        BeginInvoke(() => Main.UseClassicHome(this));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _watchdog?.Dispose();
            _watchdog = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>The page is a part of the app, not a browser: no menus, shortcuts, pop-ups, downloads or permissions.</summary>
    private static void Lock(CoreWebView2 core)
    {
        var s = core.Settings;
        s.AreDevToolsEnabled = false;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDefaultScriptDialogsEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsWebMessageEnabled = true;
        s.IsScriptEnabled = true;
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(Origin + "/", StringComparison.Ordinal)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
    }

    // ───────────── The page's own files (from memory) ─────────────

    private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
    {
        ["index.html"] = "text/html; charset=utf-8",
        ["home.css"] = "text/css; charset=utf-8",
        ["home.js"] = "text/javascript; charset=utf-8",
    };

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_environment is null) return;
        var uri = e.Request.Uri;
        Stream? content = null;
        if (uri.StartsWith(Origin + "/", StringComparison.Ordinal))
        {
            var name = uri[(Origin.Length + 1)..].Split('?', '#')[0];
            if (name.Length == 0) name = "index.html";
            if (Files.TryGetValue(name, out var type))
            {
                content = typeof(WebHomePage).Assembly.GetManifestResourceStream("web/" + name);
                if (content is not null)
                {
                    e.Response = _environment.CreateWebResourceResponse(content, 200, "OK",
                        $"Content-Type: {type}\nCache-Control: no-store\nX-Content-Type-Options: nosniff");
                    return;
                }
            }
        }
        // Anything else (including any address outside the app) is refused: nothing goes to the network.
        e.Response = _environment.CreateWebResourceResponse(null, 403, "Forbidden", "Cache-Control: no-store");
    }

    // ───────────── Messages from the page ─────────────

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (e.Source != Origin + "/index.html") return;
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var typeElement)) return;

            switch (typeElement.GetString())
            {
                case "ready":
                    _ready = true;
                    _watchdog?.Stop();
                    SendInit();
                    SendWeeklyIfDue();
                    await StartIfNeededAsync();
                    break;
                case "scan":
                    await ScanAsync();
                    break;
                case "search":
                    Main.OpenPalette();
                    break;
                case "navigate" when root.TryGetProperty("key", out var key):
                    if (key.GetString() is { } target && NavigationTargets.Contains(target)) Main.Navigate(target);
                    break;
                case "fix" when root.TryGetProperty("index", out var index) && index.TryGetInt32(out var i):
                    await FixAsync(i);
                    break;
                case "weekly" when root.TryGetProperty("action", out var action):
                    WeeklyAction(action.GetString());
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Write($"Web Home message failed: {ex}");
        }
    }

    // ───────────── Page lifetime ─────────────

    public override async void OnShown()
    {
        if (!_ready) return; // the page asks for everything when it has loaded ("ready")
        SendInit(); // language or theme may have changed
        if (_scanned) return;
        await StartIfNeededAsync();
    }

    private async Task StartIfNeededAsync()
    {
        if (_scanned || _scanning) return;
        if (AppSettings.Current.ScanOnStartup) await ScanAsync();
        else SendState(Loc.T("Select Scan to check this PC."), busy: false);
    }

    // ───────────── Talking to the page ─────────────

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private void Post(object message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Post(message));
            return;
        }
        if (!_ready || _web.CoreWebView2 is null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void SendInit()
    {
        Post(new
        {
            type = "init",
            theme = new
            {
                light = Theme.IsLight,
                bg = Hex(Theme.Background), card = Hex(Theme.Card), cardHover = Hex(Theme.CardHover),
                control = Hex(Theme.Control), controlHover = Hex(Theme.ControlHover), border = Hex(Theme.Border),
                divider = Hex(Theme.Divider), text = Hex(Theme.Text), muted = Hex(Theme.Muted), good = Hex(Theme.Good),
                warn = Hex(Theme.Warn), bad = Hex(Theme.Bad), info = Hex(Theme.Info), accent = Hex(Theme.Accent),
                iconTile = Hex(Theme.IconTileFill), track = Hex(Theme.Track), inset = Hex(Theme.Inset),
            },
            text = new
            {
                lang = AppSettings.Current.Language == "es" ? "es" : "en",
                title = Loc.T("Home"),
                subtitle = Loc.T("Overall health of this PC and the problems that need attention."),
                search = Loc.T("Search"),
                optimize = Loc.T("Optimize now"),
                scan = _scanned ? Loc.T("Scan again") : Loc.T("Scan"),
                thisPc = Loc.T("This PC"),
                of = Loc.T("of 100"),
                quick = new[]
                {
                    new { glyph = "", title = Loc.T("Clean junk files"), subtitle = Loc.T("Temp files, caches and logs"), target = "tools" },
                    new { glyph = "", title = Loc.T("Disk space"), subtitle = Loc.T("See what takes up space"), target = "space" },
                    new { glyph = "", title = Loc.T("Drivers"), subtitle = Loc.T("Update or clean install"), target = "drivers" },
                    new { glyph = "", title = Loc.T("Monitor"), subtitle = Loc.T("Live usage and temperatures"), target = "monitor" },
                },
            },
        });
        if (_lastSummary.Length > 0 || _lastStatus.Length > 0) SendState(_lastStatus, busy: _scanning, _lastSummary);
    }

    private void SendState(string status, bool busy, string? summary = null)
    {
        _lastStatus = status;
        if (summary is not null) _lastSummary = summary;
        Post(new
        {
            type = "state",
            summary = _lastSummary,
            status,
            busy,
            scanLabel = _scanned ? Loc.T("Scan again") : Loc.T("Scan"),
        });
    }

    // ───────────── Scan ─────────────

    private async Task ScanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        SendState("", busy: true, Loc.T("Scanning..."));
        try
        {
            var result = await HealthScanner.ScanAsync(s => SendState(Loc.T(s), busy: true));
            _scanned = true;
            SendResult(result);
        }
        catch (Exception ex)
        {
            SendState(string.Format(Loc.T("Scan failed: {0}"), ex.Message), busy: false);
            Logger.Write($"Scan error: {ex}");
        }
        finally
        {
            _scanning = false;
        }
    }

    private void SendResult(ScanResult r)
    {
        var score = r.Score;
        var s = r.System;
        var sysDrive = s.Drives.FirstOrDefault(d => Environment.SystemDirectory.StartsWith(d.Name, StringComparison.OrdinalIgnoreCase)) ?? s.Drives.FirstOrDefault();
        _issues = r.Issues.OrderByDescending(i => i.Severity).ToList();

        var tiles = new List<object>
        {
            new { caption = Loc.T("Windows"), value = s.OsName, detail = s.OsVersion, color = (string?)null },
            new { caption = Loc.T("Processor"), value = s.Threads > 0 ? string.Format(Loc.T("{0} cores / {1} threads"), s.Cores, s.Threads) : "—", detail = s.Cpu, color = (string?)null },
            new { caption = Loc.T("Memory"), value = Format.Bytes(s.RamTotal), detail = string.Format(Loc.T("{0} available"), Format.Bytes(s.RamFree)), color = (string?)null },
            sysDrive is null
                ? new { caption = Loc.T("System drive"), value = "—", detail = "", color = (string?)null }
                : new { caption = string.Format(Loc.T("System drive ({0})"), sysDrive.Name), value = string.Format(Loc.T("{0} free"), Format.Bytes(sysDrive.Free)),
                        detail = string.Format(Loc.T("of {0}"), Format.Bytes(sysDrive.Total)), color = sysDrive.FreePercent < 10 ? Hex(Theme.Warn) : null },
        };

        var components = new List<object>();
        foreach (var d in r.Disks)
            components.Add(new { name = Loc.T("Drive"), value = Loc.T($"{d.Model}  ·  {d.MediaType}  ·  {d.HealthText}" + (d.TemperatureC is { } t ? $"  ·  {t} °C" : "")), color = Hex(Theme.For(d.Health)) });
        components.Add(new { name = Loc.T("Activation"), value = Loc.T(r.Activation.StatusText), color = Hex(r.Activation.IsActivated ? Theme.Good : Theme.Warn) });
        components.Add(new { name = Loc.T("Graphics"), value = s.Gpus.Count > 0 ? string.Join(", ", s.Gpus) : "—", color = Hex(Theme.Muted) });

        var summary = score >= 85 ? "Your PC is in good shape" : score >= 60 ? "A few things could be improved" : "Your PC needs attention";
        _lastSummary = Loc.T(summary);
        _lastStatus = string.Format(Loc.T("Last scan {0}  ·  {1} item(s)  ·  {2} of junk files"), $"{DateTime.Now:t}", r.Issues.Count, Format.Bytes(r.JunkBytes));
        Post(new
        {
            type = "result",
            score,
            ringColor = Hex(score >= 85 ? Theme.Good : score >= 60 ? Theme.Warn : Theme.Bad),
            summary = _lastSummary,
            status = _lastStatus,
            scanLabel = Loc.T("Scan again"),
            tiles,
            issuesTitle = r.Issues.Count == 0 ? Loc.T("No problems found") : string.Format(Loc.T("Recommendations ({0})"), r.Issues.Count),
            emptyText = Loc.T("Everything checked out. Run a scan again any time."),
            componentsTitle = Loc.T("Components"),
            issues = _issues.Select(i => new
            {
                title = Loc.T(i.Title),
                detail = Loc.T(i.Detail),
                color = Hex(Theme.For(i.Severity)),
                fixLabel = i.FixTaskIds.Length > 0 || i.NavigateTo is not null ? Loc.T(i.FixLabel) : null,
            }).ToList(),
            components,
        });
    }

    private async Task FixAsync(int index)
    {
        if (index < 0 || index >= _issues.Count) return;
        var issue = _issues[index];
        if (issue.FixTaskIds.Length > 0)
        {
            var tasks = issue.FixTaskIds.Select(TaskCatalog.Find).OfType<SystemTask>().ToList();
            if (!Confirm($"The following will run:\n\n- {string.Join("\n- ", tasks.Select(t => Loc.T(t.Title)))}\n\nContinue?")) return;
            RunDialog.RunTasks(this, issue.Title, tasks);
            await ScanAsync();
        }
        else if (issue.NavigateTo is { } target)
        {
            Main.Navigate(target);
        }
    }

    // ───────────── Weekly summary ─────────────

    private void SendWeeklyIfDue()
    {
        var s = AppSettings.Current;
        if (_weeklyShown || !s.WeeklySummary || s.InstalledOn is not { } installed) return;
        var due = ActivityLog.NextDue(installed, s.LastWeeklySummary);
        if (DateTime.Now < due) return;

        var week = ActivityLog.Summary(due.AddDays(-7), DateTime.Now);
        HealthLevel worst;
        lock (AppSettings.SaveLock) worst = s.DiskBaselines.Values.Select(b => b.Health).DefaultIfEmpty(HealthLevel.Unknown).Max();
        _weeklyShown = true;
        Post(new
        {
            type = "weekly",
            data = new
            {
                title = Loc.T("Your week with WinSolve"),
                range = string.Format(Loc.T("{0} to {1}"), week.From.ToString("d"), week.To.ToString("d")),
                gotIt = Loc.T("Got it"),
                hide = Loc.T("Don't show weekly summaries"),
                stats = new[]
                {
                    new
                    {
                        title = Loc.T("Problems detected"),
                        value = week.Alerts.ToString(),
                        detail = week.Alerts == 0 ? Loc.T("Windows ran without errors") : string.Format(Loc.T("{0} serious"), week.CriticalAlerts),
                        color = Hex(week.CriticalAlerts > 0 ? Theme.Bad : week.Alerts > 0 ? Theme.Warn : Theme.Good),
                    },
                    new
                    {
                        title = Loc.T("Space freed"),
                        value = Format.Bytes(week.BytesFreed),
                        detail = string.Format(Loc.T("{0} cleanup(s), {1} automatic"), week.Cleanups, week.MaintenanceRuns),
                        color = Hex(Theme.Text),
                    },
                    new
                    {
                        title = Loc.T("Drives"),
                        value = Loc.T(worst switch { HealthLevel.Bad => "Needs attention", HealthLevel.Caution => "Some wear", HealthLevel.Good => "Healthy", _ => "Not checked yet" }),
                        detail = Loc.T("From the drives' S.M.A.R.T. data"),
                        color = Hex(worst switch { HealthLevel.Bad => Theme.Bad, HealthLevel.Caution => Theme.Warn, HealthLevel.Good => Theme.Good, _ => Theme.Muted }),
                    },
                },
            },
        });
    }

    private void WeeklyAction(string? action)
    {
        if (action == "off") AppSettings.Current.WeeklySummary = false;
        else if (action != "dismiss") return;
        AppSettings.Current.LastWeeklySummary = DateTime.Now;
        AppSettings.Current.Save();
        Post(new { type = "weekly", data = (object?)null });
    }
}
