using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WinSolve.Setup
{
    /// <summary>Installer window: language, install scope and options, in WinSolve's style.</summary>
    public sealed class SetupForm : Form
    {
        private readonly ChoiceCard _me, _all;
        private readonly Toggle _clean, _desktop, _launch;
        private readonly FlatButton _install, _cancel;
        private readonly Label _status;
        private readonly ProgressLine _progress;
        private readonly CardPanel _options;
        private readonly Label _title, _subtitle, _scopeLabel;
        private readonly PillSwitch _language;
        private readonly bool _autoStart;
        private CancellationTokenSource _cts;
        private bool _done;

        private readonly InstallOptions _preset;

        public SetupForm(InstallOptions preset, bool autoStart)
        {
            _preset = preset;
            _autoStart = autoStart;
            if (preset.Language != null) Lang.Spanish = preset.Language == "es";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            ClientSize = new Size(560, 530);
            BackColor = Ui.Background;
            ForeColor = Ui.Text;
            Font = Ui.Body;
            DoubleBuffered = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var logo = new PictureBox { Size = new Size(48, 48), Location = new Point(32, 28), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
            try { logo.Image = new Icon(Icon.ExtractAssociatedIcon(Application.ExecutablePath), 48, 48).ToBitmap(); } catch { }
            _title = new Label { Font = Ui.Title, AutoSize = true, Location = new Point(92, 22), BackColor = Color.Transparent };
            _subtitle = new Label { ForeColor = Ui.Muted, AutoSize = true, Location = new Point(95, 64), BackColor = Color.Transparent };

            // Language of the installer, and of WinSolve when it opens.
            _language = new PillSwitch("EN", "ES") { Location = new Point(432, 34), SelectedIndex = Lang.Spanish ? 1 : 0 };
            _language.Changed += (s, e) => { Lang.Spanish = _language.SelectedIndex == 1; ApplyTexts(); };

            _scopeLabel = new Label { Font = Ui.BodyBold, AutoSize = true, Location = new Point(32, 110), BackColor = Color.Transparent };
            _all = new ChoiceCard { Glyph = "\uE716", Location = new Point(32, 136), Size = new Size(242, 146), Checked = preset.AllUsers };
            _me = new ChoiceCard { Glyph = "\uE77B", Location = new Point(286, 136), Size = new Size(242, 146), Checked = !preset.AllUsers };
            _all.Selected += (s, e) => _me.Checked = false;
            _me.Selected += (s, e) => _all.Checked = false;

            _options = new CardPanel { Location = new Point(32, 294), Size = new Size(496, 124), Padding = new Padding(16, 6, 12, 6) };
            _clean = new Toggle { Checked = preset.Clean };
            _desktop = new Toggle { Checked = preset.DesktopShortcut };
            _launch = new Toggle { Checked = preset.Launch };
            var y = 8;
            foreach (var t in new[] { _desktop, _launch, _clean })
            {
                t.SetBounds(16, y, 468, 36);
                _options.Controls.Add(t);
                y += 36;
            }

            _status = new Label { ForeColor = Ui.Muted, AutoSize = false, Location = new Point(32, 428), Size = new Size(496, 34), BackColor = Color.Transparent };
            _progress = new ProgressLine { Location = new Point(32, 462), Size = new Size(496, 4), Visible = false };

            _install = new FlatButton(primary: true) { Location = new Point(412, 478) };
            _install.Click += async (s, e) => { if (_done) Close(); else await RunInstall(); };
            _cancel = new FlatButton(primary: false) { Location = new Point(288, 478) };
            _cancel.Click += (s, e) => { if (_cts != null) _cts.Cancel(); else Close(); };

            Controls.AddRange(new Control[] { logo, _title, _subtitle, _language, _scopeLabel, _all, _me, _options, _status, _progress, _install, _cancel });
            // While updating, the window just shows progress: no language choice.
            _language.Visible = !autoStart;
            ApplyTexts();

            HandleCreated += (s, e) => { if (!Ui.Light) DarkTitleBar(Handle); };
            Shown += async (s, e) => { if (_autoStart) await RunInstall(); };
            KeyPreview = true;
            KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter && _install.Enabled) { e.Handled = true; if (_done) Close(); else await RunInstall(); } };
        }

        private void ApplyTexts()
        {
            Text = Lang.T("WinSolve Setup");
            _title.Text = Lang.T(_done ? "WinSolve is ready" : _autoStart && _preset.Launch ? "Updating WinSolve" : "Install WinSolve");
            _subtitle.Text = Lang.F("Version {0}  ·  The multitool for Windows", Installer.Version);
            _scopeLabel.Text = Lang.T("Install for");
            _all.Text = Lang.T("All users (recommended)");
            _all.Detail = Lang.F("Protected from changes. Needs administrator.\n{0}", Installer.AllUsersDir);
            _me.Text = Lang.T("Only me");
            _me.Detail = Lang.F("No administrator needed. Can't start with Windows.\n{0}", Shorten(Installer.PerUserDir));
            _clean.Text = Lang.T("Clean install: remove the previous version and its settings");
            _desktop.Text = Lang.T("Create a desktop shortcut");
            _launch.Text = Lang.T("Open WinSolve when setup finishes");
            if (_cts == null && !_done)
                _status.Text = Lang.T(Installer.IsRuntimeInstalled() ? "Ready to install." : "The .NET 8 Desktop Runtime will be downloaded from Microsoft (about 55 MB).");
            _install.Text = Lang.T(_done ? "Finish" : "Install");
            _cancel.Text = Lang.T("Cancel");
            _all.Invalidate();
            _me.Invalidate();
        }

        private static string Shorten(string path)
            => path.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");

        private void SetEditable(bool editable)
        {
            foreach (var c in new Control[] { _all, _me, _clean, _desktop, _launch, _language }) c.Enabled = editable;
        }

        private InstallOptions Options() => new InstallOptions
        {
            AllUsers = _all.Checked,
            Clean = _clean.Checked,
            DesktopShortcut = _desktop.Checked,
            Launch = _launch.Checked,
            UpdateDir = _preset.UpdateDir,
            WaitPid = _preset.WaitPid,
            // An automatic update keeps WinSolve's own language setting.
            Language = _autoStart ? _preset.Language : Lang.Code,
        };

        private async Task RunInstall()
        {
            var o = Options();

            // All-users installs (and installing the runtime) need elevation: relaunch elevated.
            if (o.AllUsers && !Installer.IsElevated)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Application.ExecutablePath, o.ToArgs() + " --auto") { UseShellExecute = true, Verb = "runas" });
                    Close();
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    _status.Text = Lang.T("Administrator permission is required to install for all users.");
                }
                return;
            }

            SetEditable(false);
            _install.Enabled = false;
            _status.ForeColor = Ui.Muted;
            _progress.Value = -1;
            _progress.Visible = true;
            _cts = new CancellationTokenSource();
            var ok = false;
            try
            {
                await Task.Run(() => Installer.Install(o,
                    s => BeginInvoke(new Action(() => { _status.Text = s; _progress.Value = -1; })),
                    p => BeginInvoke(new Action(() => _progress.Value = Math.Max(0, Math.Min(100, p)) / 100f)),
                    _cts.Token));
                ok = true;
            }
            catch (Exception ex)
            {
                _status.Text = Lang.F("Setup failed: {0}", ex.Message);
                _status.ForeColor = Ui.Bad;
                _progress.Visible = false;
                // An automatic update that failed: the previous version is still intact, reopen it.
                if (_autoStart && o.Launch && o.UpdateDir != null) Installer.LaunchApp(o);
                SetEditable(true);
                _install.Enabled = true;
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
            }

            if (ok)
            {
                _status.ForeColor = Ui.Good;
                _progress.Value = 1;
                _done = true;
                ApplyTexts();
                _install.Enabled = true;
                _cancel.Visible = false;
                if (o.Launch)
                {
                    Installer.LaunchApp(o);
                    Close();
                }
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        internal static void DarkTitleBar(IntPtr hwnd)
        {
            try { int on = 1; DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); } catch { }
        }
    }
}
