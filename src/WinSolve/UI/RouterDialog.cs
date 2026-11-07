using System.Text.Json;
using Microsoft.Win32;
using WinSolve.Core;
using WinSolve.Localization;
using WinSolve.Services;

namespace WinSolve.UI;

/// <summary>
/// The router's own admin page (the one at 192.168.1.1) inside WinSolve, signed in with the saved
/// password. Every setting the router has (Wi-Fi name and password, connected devices, MAC filter,
/// port forwarding, parental control, restart...) is available because it is the router's real page.
/// </summary>
internal sealed class RouterDialog : ToolDialog
{
    private readonly WebBrowser _browser = new()
    {
        Dock = DockStyle.Fill,
        ScriptErrorsSuppressed = true,
        AllowWebBrowserDrop = false,
    };
    private readonly TextBox _address = Theme.TextBox();
    private readonly RouterLogin _login;
    private readonly Uri _home;
    private bool _autoSignedIn;

    public RouterDialog(RouterLogin login, Uri home, string? macToBlock = null)
        : base("", "Router", "Your router's own settings page. WinSolve signs in for you.", new Size(1180, 800))
    {
        _login = login;
        _home = home;
        MinimumSize = new Size(900, 600);
        _address.Text = home.ToString();
        _address.Width = 360;
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            GoTo(_address.Text);
        };

        AddRow(Theme.Row(
            Theme.Button("Back", (_, _) => { if (_browser.CanGoBack) _browser.GoBack(); }, glyph: ""),
            Theme.Button("Reload", (_, _) => _browser.Refresh(), glyph: ""),
            Theme.Button("Sign in again", (_, _) => { _autoSignedIn = false; _browser.Refresh(); }, glyph: ""),
            _address,
            Theme.Button("Go", (_, _) => GoTo(_address.Text), primary: true)));
        if (macToBlock is not null)
        {
            Clipboard.SetText(macToBlock);
            AddRow(Theme.Paragraph(Loc.T($"The device address {macToBlock} is copied. Look for 'MAC filter', 'Access control' or 'Parental control' in the router and paste it there to block the device."), Theme.Info));
        }
        AddRow(Theme.InCard(_browser, 2), fill: true);

        _browser.DocumentCompleted += (_, _) => OnLoaded();
        _browser.Navigated += (_, e) => { if (e.Url is not null) _address.Text = e.Url.ToString(); };
        Shown += (_, _) => _browser.Navigate(_home);
    }

    /// <summary>Old router pages only work with the IE 11 engine; without this the control pretends to be IE 7.</summary>
    internal static void UseModernEngine()
    {
        try
        {
            var exe = Path.GetFileName(Environment.ProcessPath ?? "WinSolve.exe");
            Reg.Set(RegistryHive.CurrentUser, @"Software\Microsoft\Internet Explorer\Main\FeatureControl\FEATURE_BROWSER_EMULATION", exe, 11001, RegistryValueKind.DWord);
        }
        catch { /* the page still loads, just with the old engine */ }
    }

    private void GoTo(string text)
    {
        // Only the router itself: this window is not a general browser.
        if (!RouterService.TryParseAddress(text, out var uri))
        {
            Status.Text = Loc.T("Only addresses on your own network (like 192.168.1.1) can be opened here.");
            return;
        }
        _browser.Navigate(uri);
    }

    private void OnLoaded()
    {
        if (_browser.Document is not { } doc || _browser.Url is not { } url) return;
        Status.Text = "";
        // The password is only typed into a page of the router it was saved for, never into a redirect elsewhere.
        if (!string.Equals(url.Host, _home.Host, StringComparison.OrdinalIgnoreCase)) return;
        if (_autoSignedIn || _login.Password.Length == 0) return;
        try
        {
            var result = doc.InvokeScript("eval", new object[] { FillScript(_login.User, _login.Password) }) as string;
            // Only one automatic try per window: a wrong password must not be sent again and again (routers lock you out).
            if (result == "submitted")
            {
                _autoSignedIn = true;
                Status.Text = Loc.T("Signed in with the saved password. If the router says it is wrong, update it in WinSolve.");
            }
        }
        catch { /* a frame-based login page: sign in by hand */ }
    }

    /// <summary>Script that finds the password box, fills the user box before it and presses the sign-in button (IE 11 compatible).</summary>
    internal static string FillScript(string user, string password)
    {
        var u = JsonSerializer.Serialize(user);
        var p = JsonSerializer.Serialize(password);
        return "(function(u,p){try{"
            + "var inputs=document.getElementsByTagName('input'),pw=null,usr=null,i;"
            + "for(i=0;i<inputs.length;i++){var t=(inputs[i].type||'text').toLowerCase();"
            + "if(t==='password'&&inputs[i].offsetWidth>0){pw=inputs[i];break;}"
            + "if((t==='text'||t==='email')&&inputs[i].offsetWidth>0)usr=inputs[i];}"
            + "if(!pw)return 'nopassword';"
            + "function put(el,v){el.focus();el.value=v;var ev=document.createEvent('Event');ev.initEvent('input',true,true);el.dispatchEvent(ev);"
            + "ev=document.createEvent('Event');ev.initEvent('change',true,true);el.dispatchEvent(ev);}"
            + "if(usr&&u)put(usr,u);put(pw,p);"
            + "var scope=pw.form||document,btns=scope.querySelectorAll('input[type=submit],button,input[type=button],a.button,a[onclick]');"
            + "for(i=0;i<btns.length;i++){if(btns[i].offsetWidth>0){btns[i].click();return 'submitted';}}"
            + "if(pw.form){pw.form.submit();return 'submitted';}"
            + "return 'nobutton';}catch(e){return 'error';}})(" + u + "," + p + ")";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _browser.Dispose();
        base.Dispose(disposing);
    }
}
