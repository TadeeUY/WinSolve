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
    /// <summary>Small installer window: install scope, clean install and shortcuts.</summary>
    public sealed class SetupForm : Form
    {
        private static readonly Color Bg = Color.FromArgb(32, 32, 32);
        private static readonly Color Card = Color.FromArgb(43, 43, 43);
        private static readonly Color Border = Color.FromArgb(58, 58, 58);
        private static readonly Color TextColor = Color.FromArgb(242, 242, 242);
        private static readonly Color Muted = Color.FromArgb(160, 160, 160);
        private static readonly Color Accent = Color.FromArgb(0, 103, 192);

        private readonly RadioButton _me, _all;
        private readonly CheckBox _clean, _desktop, _launch;
        private readonly Button _install, _cancel;
        private readonly Label _status;
        private readonly ProgressBar _progress;
        private readonly Panel _options;
        private readonly Label _title, _subtitle, _scopeLabel, _meDetail, _allDetail, _languageLabel;
        private readonly ComboBox _language;
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
            ClientSize = new Size(520, 420);
            BackColor = Bg;
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 9.5f);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var logo = new PictureBox
            {
                Size = new Size(40, 40),
                Location = new Point(28, 24),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
            };
            try { logo.Image = new Icon(Icon.ExtractAssociatedIcon(Application.ExecutablePath), 48, 48).ToBitmap(); } catch { }
            _title = new Label { Font = new Font("Segoe UI", 18f, FontStyle.Bold), AutoSize = true, Location = new Point(76, 20) };
            _subtitle = new Label { ForeColor = Muted, AutoSize = true, Location = new Point(78, 60) };

            // Language of the installer, and of WinSolve when it opens.
            _languageLabel = new Label { ForeColor = Muted, AutoSize = true, Location = new Point(392, 10), Font = new Font("Segoe UI", 8.5f) };
            _language = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(392, 28), Width = 100,
                FlatStyle = FlatStyle.Flat, BackColor = Card, ForeColor = TextColor,
            };
            _language.Items.AddRange(new object[] { "English", "Español" });
            _language.SelectedIndex = Lang.Spanish ? 1 : 0;
            _language.SelectedIndexChanged += (s, e) => { Lang.Spanish = _language.SelectedIndex == 1; ApplyTexts(); };

            _options = new Panel { Location = new Point(28, 96), Size = new Size(464, 228), BackColor = Card };
            _options.Paint += (s, e) => PaintCard(e.Graphics, _options.ClientRectangle);

            _scopeLabel = new Label { Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), AutoSize = true, Location = new Point(16, 12), BackColor = Card };
            _me = Radio(36, out _meDetail);
            _all = Radio(84, out _allDetail);
            _me.Checked = !preset.AllUsers;
            _all.Checked = preset.AllUsers;

            _clean = Check(preset.Clean, 140);
            _desktop = Check(preset.DesktopShortcut, 166);
            _launch = Check(preset.Launch, 192);

            _options.Controls.AddRange(new Control[] { _scopeLabel, _me, _all, _clean, _desktop, _launch });

            _status = new Label { ForeColor = Muted, AutoSize = false, Location = new Point(28, 330), Size = new Size(464, 36) };
            _progress = new ProgressBar { Location = new Point(28, 364), Size = new Size(464, 4), Style = ProgressBarStyle.Continuous, Visible = false };

            _install = FlatButton("Install", primary: true);
            _install.Location = new Point(392, 372);
            _install.Click += async (s, e) => { if (_done) Close(); else await RunInstall(); };
            _cancel = FlatButton("Cancel", primary: false);
            _cancel.Location = new Point(284, 372);
            _cancel.Click += (s, e) => { if (_cts != null) _cts.Cancel(); else Close(); };

            Controls.AddRange(new Control[] { logo, _title, _subtitle, _languageLabel, _language, _options, _status, _progress, _install, _cancel });
            // While updating, the window just shows progress: no language choice.
            _language.Visible = _languageLabel.Visible = !autoStart;
            ApplyTexts();
            AcceptButton = _install;

            HandleCreated += (s, e) => DarkTitleBar(Handle);
            Shown += async (s, e) => { if (_autoStart) await RunInstall(); };
        }

        private void ApplyTexts()
        {
            Text = Lang.T("WinSolve Setup");
            _title.Text = Lang.T(_autoStart && _preset.Launch ? "Updating WinSolve" : "Install WinSolve");
            _subtitle.Text = Lang.F("Version {0}  ·  The multitool for Windows", Installer.Version);
            _languageLabel.Text = Lang.T("Language");
            _scopeLabel.Text = Lang.T("Install for");
            _me.Text = Lang.T("Only me");
            _meDetail.Text = Lang.F("Installs in your user folder  ·  {0}", Shorten(Installer.PerUserDir));
            _all.Text = Lang.T("All users of this PC (recommended)");
            _allDetail.Text = Lang.F("Protected from tampering, requires administrator  ·  {0}", Installer.AllUsersDir);
            _clean.Text = Lang.T("Clean install (remove any previous version, settings and logs)");
            _desktop.Text = Lang.T("Create a desktop shortcut");
            _launch.Text = Lang.T("Open WinSolve when setup finishes");
            if (_cts == null && !_done)
                _status.Text = Lang.T(Installer.IsRuntimeInstalled() ? "Ready to install." : "The .NET 8 Desktop Runtime will be downloaded from Microsoft (about 55 MB).");
            _install.Text = Lang.T(_done ? "Finish" : "Install");
            _cancel.Text = Lang.T("Cancel");
        }

        private static string Shorten(string path)
            => path.Replace(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");

        private RadioButton Radio(int top, out Label detail)
        {
            var rb = new RadioButton { AutoSize = true, Location = new Point(18, top), BackColor = Card, ForeColor = TextColor };
            detail = new Label { AutoSize = true, ForeColor = Muted, Location = new Point(36, top + 22), BackColor = Card, Font = new Font("Segoe UI", 8.5f) };
            _options.Controls.Add(detail);
            return rb;
        }

        private static CheckBox Check(bool value, int top)
            => new CheckBox { Checked = value, AutoSize = true, Location = new Point(18, top), BackColor = Card, ForeColor = TextColor };

        private static Button FlatButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text,
                Size = new Size(100, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Color.FromArgb(55, 55, 55),
                ForeColor = Color.White,
            };
            b.FlatAppearance.BorderColor = primary ? Accent : Border;
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(24, 121, 207) : Color.FromArgb(62, 62, 62);
            return b;
        }

        private static void PaintCard(Graphics g, Rectangle r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Border)) g.DrawRectangle(pen, 0, 0, r.Width - 1, r.Height - 1);
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

            _options.Enabled = false;
            _language.Enabled = false;
            _install.Enabled = false;
            _progress.Visible = true;
            _cts = new CancellationTokenSource();
            var ok = false;
            try
            {
                await Task.Run(() => Installer.Install(o,
                    s => BeginInvoke(new Action(() => _status.Text = s)),
                    p => BeginInvoke(new Action(() => _progress.Value = Math.Max(0, Math.Min(100, p)))),
                    _cts.Token));
                ok = true;
            }
            catch (Exception ex)
            {
                _status.Text = Lang.F("Setup failed: {0}", ex.Message);
                _status.ForeColor = Color.FromArgb(255, 99, 97);
                // An automatic update that failed: the previous version is still intact, reopen it.
                if (_autoStart && o.Launch && o.UpdateDir != null) Installer.LaunchApp(o);
                _options.Enabled = true;
                _language.Enabled = true;
                _install.Enabled = true;
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
            }

            if (ok)
            {
                _status.ForeColor = Color.FromArgb(108, 203, 95);
                _install.Text = Lang.T("Finish");
                _install.Enabled = true;
                _done = true;
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
