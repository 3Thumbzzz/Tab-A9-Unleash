// Galaxy Tab A9 (SM-X110) LineageOS installer wizard
// Walks through: tablet checks, PC tools, bootloader unlock, building a fastbootd
// recovery from stock firmware (via WSL + fastboot-patcher), flashing it with Odin,
// connecting to fastbootd, and flashing a LineageOS GSI.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace X110Tool
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            try { if (Environment.OSVersion.Platform == PlatformID.Win32NT) SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Wizard());
        }
    }

    class Step
    {
        public string Title;
        public string Subtitle;
        public Action Build;
        public Func<bool> CanNext;
    }

    class Wizard : Form
    {
        // ---------- constants ----------
        const string UrlSamsungDriver = "https://developer.samsung.com/android-usb-driver";
        const string UrlPlatformToolsZip = "https://dl.google.com/android/repository/platform-tools-latest-windows.zip";
        const string UrlGoogleDriver = "https://developer.android.com/studio/run/win-usb";
        const string UrlOdin = "https://www.thecustomdroid.com/download-odin-flash-tool";
        const string UrlFirmware = "https://samfw.com/firmware/SM-X110";
        const string UrlGsi = "https://github.com/MisterZtr/LineageOS_gsi/releases";
        const string UrlPatcher = "https://github.com/ratcoded/fastboot-patcher";
        const string UrlPatchRecovery = "https://github.com/Johx22/Patch-Recovery";
        const string UrlSevenZip = "https://www.7-zip.org/";
        const string UrlXdaGuide = "https://xdaforums.com/t/guide-sm-x110-how-to-install-gsi-root-on-tab-a9.4715422/";

        // ---------- state ----------
        readonly string appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X110LineageTool");
        string platformTools;
        string odinExe;
        bool wslOk;
        bool useExistingTar;
        string firmwareDir;
        string recoveryTar;
        string gsiSource;
        string gsiImg;
        bool fastbootOk;
        bool flashed;
        readonly Dictionary<string, bool> checks = new Dictionary<string, bool>();

        // ---------- UI ----------
        readonly List<Step> steps = new List<Step>();
        int cur;
        bool busy;
        float scale = 1f;
        Panel side;
        FlowLayoutPanel content;
        Label title, subtitle;
        TextBox log;
        Button back, next;
        readonly Font baseFont = new Font("Segoe UI", 10f);
        readonly Font boldFont = new Font("Segoe UI", 10f, FontStyle.Bold);
        readonly Font headFont = new Font("Segoe UI Semibold", 12f, FontStyle.Bold);
        readonly Font titleFont = new Font("Segoe UI", 17f, FontStyle.Bold);
        readonly Font monoFont = new Font("Consolas", 9f);

        static readonly Color Accent = Color.FromArgb(22, 119, 89);
        static readonly Color Warn = Color.FromArgb(176, 74, 0);
        static readonly Color Bad = Color.FromArgb(190, 30, 45);
        static readonly Color Muted = Color.FromArgb(95, 99, 104);
        static readonly Color SideBg = Color.FromArgb(244, 246, 248);

        public Wizard()
        {
            using (var g = CreateGraphics()) scale = g.DpiX / 96f;
            Text = "Tab A9 (SM-X110) LineageOS Installer";
            Font = baseFont;
            BackColor = Color.White;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(S(1040), S(760));
            MinimumSize = new Size(S(860), S(620));

            try { Directory.CreateDirectory(appDir); } catch { }
            var auto = Path.Combine(appDir, "platform-tools");
            if (File.Exists(Path.Combine(auto, "fastboot.exe"))) platformTools = auto;
            odinExe = FindOdin();

            BuildChrome();
            DefineSteps();
            int startPage;
            Show(int.TryParse(Environment.GetEnvironmentVariable("X110_PAGE"), out startPage) && startPage >= 0 && startPage < steps.Count ? startPage : 0);
            ResizeEnd += (s, e) => Show(cur);
            FormClosing += (s, e) =>
            {
                if (busy && MessageBox.Show(this, "A task is still running. Quit anyway?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    e.Cancel = true;
            };
        }

        int S(int px) { return (int)Math.Round(px * scale); }

        // =====================================================================
        // Chrome: sidebar, header, content, log, nav buttons
        // =====================================================================
        void BuildChrome()
        {
            side = new Panel { Dock = DockStyle.Left, Width = S(250), BackColor = SideBg, Padding = new Padding(S(16), S(20), S(8), S(8)) };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = S(56), Padding = new Padding(S(16), S(10), S(16), S(10)) };
            next = new Button { Text = "Next  ›", AutoSize = true, MinimumSize = new Size(S(110), S(34)), Dock = DockStyle.Right, BackColor = Accent, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = boldFont };
            next.FlatAppearance.BorderSize = 0;
            back = new Button { Text = "‹  Back", AutoSize = true, MinimumSize = new Size(S(110), S(34)), Dock = DockStyle.Right };
            var gap = new Panel { Width = S(10), Dock = DockStyle.Right };
            var copyLog = new Button { Text = "Copy log", AutoSize = true, MinimumSize = new Size(S(90), S(34)), Dock = DockStyle.Left, FlatStyle = FlatStyle.Flat, ForeColor = Accent };
            copyLog.FlatAppearance.BorderSize = 0;
            copyLog.Click += (s, e) => { try { if (log.Text.Length > 0) Clipboard.SetText(log.Text); } catch { } };
            bottom.Controls.Add(back);
            bottom.Controls.Add(gap);
            bottom.Controls.Add(next);
            bottom.Controls.Add(copyLog);
            back.Click += (s, e) => { if (cur > 0) Show(cur - 1); };
            next.Click += (s, e) =>
            {
                if (cur == steps.Count - 1) { Close(); return; }
                Show(cur + 1);
            };

            log = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Bottom,
                Height = S(110), Font = monoFont, BackColor = Color.FromArgb(28, 30, 33), ForeColor = Color.FromArgb(220, 223, 228),
                BorderStyle = BorderStyle.None
            };
            var logLabel = new Label { Text = "  Activity log", Dock = DockStyle.Bottom, Height = S(22), ForeColor = Muted, BackColor = Color.FromArgb(236, 238, 241), TextAlign = ContentAlignment.MiddleLeft };

            var header = new Panel { Dock = DockStyle.Top, Height = S(86), Padding = new Padding(S(28), S(18), S(20), 0) };
            subtitle = new Label { Dock = DockStyle.Top, AutoSize = false, Height = S(24), ForeColor = Muted };
            title = new Label { Dock = DockStyle.Top, AutoSize = false, Height = S(38), Font = titleFont };
            header.Controls.Add(subtitle);
            header.Controls.Add(title);

            content = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = true, Padding = new Padding(S(28), S(4), S(20), S(16))
            };

            Controls.Add(content);
            Controls.Add(header);
            Controls.Add(logLabel);
            Controls.Add(log);
            Controls.Add(bottom);
            Controls.Add(side);
        }

        void RefreshSide()
        {
            side.SuspendLayout();
            side.Controls.Clear();
            var appName = new Label { Text = "LineageOS Installer", Font = headFont, AutoSize = true, Margin = new Padding(0, 0, 0, S(2)) };
            var dev = new Label { Text = "Galaxy Tab A9 · SM-X110", ForeColor = Muted, AutoSize = true, Margin = new Padding(0, 0, 0, S(18)) };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
            flow.Controls.Add(appName);
            flow.Controls.Add(dev);
            for (int i = 0; i < steps.Count; i++)
            {
                string mark = i < cur ? "✓" : (i + 1).ToString();
                var l = new Label
                {
                    Text = mark + "   " + steps[i].Title,
                    AutoSize = true,
                    Margin = new Padding(0, S(4), 0, S(4)),
                    Font = i == cur ? boldFont : baseFont,
                    ForeColor = i == cur ? Accent : (i < cur ? Color.FromArgb(60, 64, 67) : Muted)
                };
                flow.Controls.Add(l);
            }
            side.Controls.Add(flow);
            side.ResumeLayout();
        }

        void Show(int i)
        {
            cur = i;
            var st = steps[i];
            title.Text = st.Title;
            subtitle.Text = "Step " + (i + 1) + " of " + steps.Count + "  ·  " + st.Subtitle;
            content.SuspendLayout();
            foreach (Control c in content.Controls.Cast<Control>().ToList()) c.Dispose();
            content.Controls.Clear();
            st.Build();
            content.ResumeLayout();
            content.AutoScrollPosition = new Point(0, 0);
            RefreshSide();
            UpdateNav();
        }

        void UpdateNav()
        {
            if (InvokeRequired) { BeginInvoke((Action)UpdateNav); return; }
            back.Enabled = !busy && cur > 0;
            next.Text = cur == steps.Count - 1 ? "Finish" : "Next  ›";
            bool ok = !busy && steps[cur].CanNext();
            next.Enabled = ok;
            next.BackColor = ok ? Accent : Color.FromArgb(180, 186, 192);
        }

        // =====================================================================
        // Content helpers
        // =====================================================================
        int W { get { return Math.Max(S(300), content.ClientSize.Width - content.Padding.Horizontal - S(24)); } }

        Label Txt(string s, Color? color = null, Font font = null, int top = 2, int bottom = 8)
        {
            var l = new Label
            {
                Text = s, AutoSize = true, MaximumSize = new Size(W, 0),
                Margin = new Padding(0, S(top), 0, S(bottom)),
                ForeColor = color ?? Color.FromArgb(32, 33, 36)
            };
            if (font != null) l.Font = font;
            content.Controls.Add(l);
            return l;
        }

        void H(string s) { Txt(s, null, headFont, 14, 4); }

        void Steps(params string[] items)
        {
            for (int i = 0; i < items.Length; i++)
                Txt((i + 1) + ".  " + items[i], null, null, 0, 4);
            Txt("", null, null, 0, 2);
        }

        void Callout(string s, Color color)
        {
            int inner = W - S(30);
            var sz = TextRenderer.MeasureText(s, baseFont, new Size(inner, 0), TextFormatFlags.WordBreak);
            var l = new Label { Text = s, AutoSize = false, Size = new Size(inner, sz.Height + S(4)), BackColor = Color.Transparent, ForeColor = Color.FromArgb(32, 33, 36) };
            var p = new Panel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(W, 0),
                BackColor = Color.FromArgb(28, color), Padding = new Padding(S(16), S(10), S(12), S(10)),
                Margin = new Padding(0, S(6), 0, S(10))
            };
            l.Location = new Point(S(16), S(10));
            p.Controls.Add(l);
            p.Paint += (o, e) => { using (var b = new SolidBrush(color)) e.Graphics.FillRectangle(b, 0, 0, S(4), p.Height); };
            content.Controls.Add(p);
        }

        CheckBox Check(string key, string text)
        {
            bool val;
            checks.TryGetValue(key, out val);
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0, S(2), 0, S(4)) };
            var c = new CheckBox { AutoSize = true, Checked = val, Margin = new Padding(0, S(3), S(4), 0) };
            var l = new Label { Text = text, AutoSize = true, MaximumSize = new Size(W - S(30), 0), Margin = new Padding(0, S(2), 0, 0), Cursor = Cursors.Hand };
            l.Click += (s, e) => c.Checked = !c.Checked;
            c.CheckedChanged += (s, e) => { checks[key] = c.Checked; UpdateNav(); };
            row.Controls.Add(c);
            row.Controls.Add(l);
            content.Controls.Add(row);
            return c;
        }

        bool Checked(string key) { bool v; return checks.TryGetValue(key, out v) && v; }

        FlowLayoutPanel Row(params Control[] items)
        {
            var row = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, MaximumSize = new Size(W, 0), Margin = new Padding(0, S(4), 0, S(8)) };
            foreach (var c in items) row.Controls.Add(c);
            content.Controls.Add(row);
            return row;
        }

        Button Btn(string text, Action a, bool primary = false)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(S(90), S(32)), Margin = new Padding(0, 0, S(8), S(4)), Padding = new Padding(S(6), 0, S(6), 0) };
            if (primary) { b.BackColor = Accent; b.ForeColor = Color.White; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; b.Font = boldFont; }
            b.Click += (s, e) => { if (!busy) a(); };
            return b;
        }

        LinkLabel Link(string text, string url)
        {
            var l = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, S(6), S(16), S(4)), LinkColor = Accent };
            l.LinkClicked += (s, e) => OpenUrl(url);
            return l;
        }

        Label Status(string s, bool? good)
        {
            var c = good == null ? Muted : (good.Value ? Accent : Bad);
            string icon = good == null ? "•  " : (good.Value ? "✔  " : "✖  ");
            return Txt(icon + s, c, boldFont, 2, 8);
        }

        TextBox PathBox(string value, int width)
        {
            return new TextBox { Text = value ?? "", ReadOnly = true, Width = width, Margin = new Padding(0, S(3), S(8), S(4)), BackColor = Color.FromArgb(248, 249, 250) };
        }

        // =====================================================================
        // Process + logging
        // =====================================================================
        void Log(string s)
        {
            if (log.InvokeRequired) { log.BeginInvoke((Action<string>)Log, s); return; }
            log.AppendText(s + "\r\n");
        }

        static string Q(string path) { return "\"" + path + "\""; }

        void Run(string file, string args, string workDir, Action<int, string> done)
        {
            busy = true;
            UpdateNav();
            UseWaitCursor = true;
            Log("> " + Path.GetFileName(file) + " " + args);
            var output = new StringBuilder();
            var t = new Thread(() =>
            {
                int code = -1;
                try
                {
                    var psi = new ProcessStartInfo(file, args)
                    {
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                        WorkingDirectory = string.IsNullOrEmpty(workDir) ? Environment.CurrentDirectory : workDir
                    };
                    using (var p = new Process { StartInfo = psi })
                    {
                        DataReceivedEventHandler h = (s, e) =>
                        {
                            if (e.Data == null) return;
                            var line = e.Data.Replace("\0", "");
                            lock (output) output.AppendLine(line);
                            Log(line);
                        };
                        p.OutputDataReceived += h;
                        p.ErrorDataReceived += h;
                        p.Start();
                        p.BeginOutputReadLine();
                        p.BeginErrorReadLine();
                        p.WaitForExit();
                        code = p.ExitCode;
                    }
                }
                catch (Exception ex)
                {
                    Log("Could not start " + file + ": " + ex.Message);
                }
                string all;
                lock (output) all = output.ToString();
                BeginInvoke((Action)(() =>
                {
                    busy = false;
                    UseWaitCursor = false;
                    Log("  (exit code " + code + ")");
                    done(code, all);
                    UpdateNav();
                }));
            }) { IsBackground = true };
            t.Start();
        }

        static string Sys32(string exe)
        {
            var win = Environment.GetEnvironmentVariable("WINDIR") ?? @"C:\Windows";
            var native = Path.Combine(win, "Sysnative", exe);
            if (File.Exists(native)) return native;
            return Path.Combine(win, "System32", exe);
        }

        string Fastboot { get { return Path.Combine(platformTools ?? "", "fastboot.exe"); } }

        static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        static string FindOdin()
        {
            var dirs = new[] { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + @"\Downloads", Environment.GetFolderPath(Environment.SpecialFolder.Desktop) };
            foreach (var d in dirs)
            {
                try
                {
                    if (!Directory.Exists(d)) continue;
                    var f = Directory.GetFiles(d, "Odin3*.exe").FirstOrDefault();
                    if (f != null) return f;
                    foreach (var sub in Directory.GetDirectories(d))
                    {
                        try { f = Directory.GetFiles(sub, "Odin3*.exe").FirstOrDefault(); } catch { }
                        if (f != null) return f;
                    }
                }
                catch { }
            }
            return null;
        }

        static string FindSevenZip()
        {
            foreach (var env in new[] { "ProgramFiles", "ProgramW6432", "ProgramFiles(x86)" })
            {
                var pf = Environment.GetEnvironmentVariable(env);
                if (string.IsNullOrEmpty(pf)) continue;
                var p = Path.Combine(pf, "7-Zip", "7z.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        static string FirstMatch(string dir, params string[] patterns)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
            foreach (var pat in patterns)
            {
                var f = Directory.GetFiles(dir, pat).FirstOrDefault();
                if (f != null) return f;
            }
            return null;
        }

        static string FileSize(string path)
        {
            try { return (new FileInfo(path).Length / 1048576.0).ToString("0") + " MB"; } catch { return "?"; }
        }

        // =====================================================================
        // Steps
        // =====================================================================
        void DefineSteps()
        {
            steps.Add(new Step { Title = "Welcome", Subtitle = "What this does and what it costs", Build = PageWelcome, CanNext = () => Checked("ack_wipe") && Checked("ack_knox") && Checked("ack_risk") });
            steps.Add(new Step { Title = "Check the tablet", Subtitle = "Make sure it's the right model and firmware", Build = PageTablet, CanNext = () => Checked("t_model") && Checked("t_oneui") && Checked("t_batt") && Checked("t_updates") && Checked("t_backup") });
            steps.Add(new Step { Title = "PC tools", Subtitle = "Drivers, fastboot, Odin and WSL", Build = PageTools, CanNext = () => Checked("drv_samsung") && File.Exists(Fastboot) && !string.IsNullOrEmpty(odinExe) && File.Exists(odinExe) && (wslOk || useExistingTar) });
            steps.Add(new Step { Title = "Unlock bootloader", Subtitle = "Done on the tablet, wipes everything", Build = PageUnlock, CanNext = () => Checked("unlocked") });
            steps.Add(new Step { Title = "Build recovery", Subtitle = "Make a fastbootd recovery from your stock firmware", Build = PageRecovery, CanNext = () => !string.IsNullOrEmpty(recoveryTar) && File.Exists(recoveryTar) });
            steps.Add(new Step { Title = "Flash recovery", Subtitle = "Install it with Odin, then boot into it", Build = PageOdin, CanNext = () => Checked("odin_pass") && Checked("in_fastbootd") });
            steps.Add(new Step { Title = "Connect fastbootd", Subtitle = "Confirm the PC can talk to the tablet", Build = PageConnect, CanNext = () => fastbootOk });
            steps.Add(new Step { Title = "Choose LineageOS", Subtitle = "Pick the GSI image", Build = PageGsi, CanNext = () => !string.IsNullOrEmpty(gsiImg) && File.Exists(gsiImg) });
            steps.Add(new Step { Title = "Flash LineageOS", Subtitle = "Write the image and wipe data", Build = PageFlash, CanNext = () => flashed && Checked("wiped") });
            steps.Add(new Step { Title = "Done", Subtitle = "First boot and what to do if it goes wrong", Build = PageDone, CanNext = () => true });
        }

        // ---------- 1 ----------
        void PageWelcome()
        {
            Txt("Installs LineageOS 23.2 on a Galaxy Tab A9 Wi-Fi (SM-X110). The app runs the PC commands; you do the button presses on the tablet.");
            Callout("Never update the tablet to One UI 8. It removes OEM unlocking permanently.", Warn);
            H("Before you start, confirm:");
            Check("ack_wipe", "I understand unlocking and installing will erase everything on the tablet.");
            Check("ack_knox", "I understand Knox trips permanently (Samsung Pass, Secure Folder and Wallet stop working).");
            Check("ack_risk", "I'm doing this at my own risk and will keep my stock firmware to restore with.");
            content.Controls.Add(Link("Community guide this is based on (XDA)", UrlXdaGuide));
        }

        // ---------- 2 ----------
        void PageTablet()
        {
            Txt("On the tablet: Settings → About tablet → Software information.");
            Check("t_model", "Model is SM-X110 (Wi-Fi). Not the SM-X115 LTE or the A9+.");
            Check("t_oneui", "One UI version is 7 or lower (yours showed 6.1).");
            Check("t_batt", "Battery is charged to at least 60%.");
            Check("t_updates", "Auto updates are off (Software update → Auto download over Wi-Fi).");
            Check("t_backup", "I've backed up my files and signed out of my Google and Samsung accounts.");
                    }

        // ---------- 3 ----------
        void PageTools()
        {
            H("1. Samsung USB driver");
            Txt("Lets Odin see the tablet in Download mode.");
            Row(Btn("Download driver", () => OpenUrl(UrlSamsungDriver)));
            Check("drv_samsung", "Samsung USB driver is installed.");

            H("2. Android platform-tools (fastboot)");
            if (File.Exists(Fastboot)) Status("Found: " + platformTools, true);
            else Status("Not set up yet", false);
            Row(
                Btn("Download automatically", DownloadPlatformTools, !File.Exists(Fastboot)),
                Btn("Choose existing folder…", () =>
                {
                    using (var d = new FolderBrowserDialog { Description = "Select the platform-tools folder (contains fastboot.exe)" })
                    {
                        if (d.ShowDialog(this) != DialogResult.OK) return;
                        if (!File.Exists(Path.Combine(d.SelectedPath, "fastboot.exe")))
                        {
                            MessageBox.Show(this, "fastboot.exe isn't in that folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        platformTools = d.SelectedPath;
                        Show(cur);
                    }
                }));

            H("3. Odin");
            Txt("Odin 3.14.1 or patched 3.14.1_3B. Unzip it, then select Odin3 .exe.");
            if (!string.IsNullOrEmpty(odinExe) && File.Exists(odinExe)) Status("Found: " + odinExe, true);
            else Status("Not selected yet", false);
            Row(
                Btn("Get Odin", () => OpenUrl(UrlOdin)),
                Btn("Select Odin3 .exe…", () =>
                {
                    using (var d = new OpenFileDialog { Title = "Select Odin3 .exe", Filter = "Odin|Odin*.exe|Programs|*.exe" })
                    {
                        if (d.ShowDialog(this) == DialogResult.OK) { odinExe = d.FileName; Show(cur); }
                    }
                }));

            H("4. WSL (Windows Subsystem for Linux)");
            Txt("Used in step 5 to build the recovery (the patch tool is Linux-only). Leave the box below unticked unless someone gave you a ready-made recovery .tar.");
            if (useExistingTar) Status("Skipped: you'll supply a ready-made recovery .tar", null);
            else if (wslOk) Status("WSL is working", true);
            else Status("Not checked yet", null);
            Row(
                Btn("Check WSL", CheckWsl, !wslOk && !useExistingTar),
                Btn("Install WSL (admin, needs reboot)…", () =>
                {
                    if (MessageBox.Show(this, "This opens an administrator PowerShell and runs 'wsl --install'.\n\nAfter it finishes, reboot, let Ubuntu finish setting up (choose a username and password), then reopen this app.\n\nContinue?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
                    try { Process.Start(new ProcessStartInfo("powershell.exe", "-NoExit -Command wsl --install") { UseShellExecute = true, Verb = "runas" }); }
                    catch (Exception ex) { Log("Could not start PowerShell: " + ex.Message); }
                }));
            var skip = Check("skip_wsl", "I already have a fastbootd recovery .tar (skip WSL).");
            skip.CheckedChanged += (s, e) => { useExistingTar = skip.Checked; BeginInvoke((Action)(() => Show(cur))); };
            useExistingTar = Checked("skip_wsl");
        }

        void DownloadPlatformTools()
        {
            var zip = Path.Combine(appDir, "platform-tools-latest-windows.zip");
            busy = true; UpdateNav();
            Log("Downloading platform-tools from Google…");
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            var wc = new WebClient();
            int lastPct = -1;
            wc.DownloadProgressChanged += (s, e) =>
            {
                if (e.ProgressPercentage / 10 != lastPct / 10) { lastPct = e.ProgressPercentage; Log("  " + e.ProgressPercentage + "%"); }
            };
            wc.DownloadFileCompleted += (s, e) =>
            {
                busy = false;
                wc.Dispose();
                if (e.Error != null)
                {
                    Log("Download failed: " + e.Error.Message);
                    UpdateNav();
                    return;
                }
                Run(Sys32("tar.exe"), "-xf " + Q(zip) + " -C " + Q(appDir), appDir, (code, o) =>
                {
                    var pt = Path.Combine(appDir, "platform-tools");
                    if (File.Exists(Path.Combine(pt, "fastboot.exe"))) { platformTools = pt; Log("platform-tools ready."); }
                    else Log("Couldn't unpack the zip. Unzip it yourself and use 'Choose existing folder'.");
                    Show(cur);
                });
            };
            try { wc.DownloadFileAsync(new Uri(UrlPlatformToolsZip), zip); }
            catch (Exception ex) { busy = false; Log("Download failed: " + ex.Message); UpdateNav(); }
        }

        void CheckWsl()
        {
            Run(Sys32("wsl.exe"), "-e uname -a", null, (code, o) =>
            {
                wslOk = code == 0 && o.Contains("Linux");
                if (!wslOk) Log("WSL isn't ready. Install it, reboot, finish the Ubuntu setup, then check again.");
                Show(cur);
            });
        }

        // ---------- 4 ----------
        void PageUnlock()
        {
            Txt("Do this on the tablet. It wipes the tablet.");
            Steps(
                "Settings → About tablet → Software information → tap Build number 7 times to turn on Developer options.",
                "Developer options → turn on OEM unlocking.",
                "Power the tablet off completely.",
                "Hold Volume Up + Volume Down together and plug in the USB cable from the PC. A blue warning screen appears.",
                "Long-press Volume Up to open the unlock screen, then press Volume Up again to confirm. The tablet wipes and reboots.",
                "Finish setup and connect to Wi-Fi. Skip account sign-in.",
                "Re-enable Developer options and check OEM unlocking says 'Bootloader is already unlocked'.");
            Callout("Odin later says 'Only official released binaries are allowed'? Boot normally, stay on Wi-Fi a while, retry.", Muted);
            Check("unlocked", "OEM unlocking now shows 'Bootloader is already unlocked'.");
        }

        // ---------- 5 ----------
        void PageRecovery()
        {
            if (useExistingTar)
            {
                Txt("Select your Odin-ready fastbootd recovery (.tar or .tar.md5).");
                Row(PathBox(recoveryTar, W - S(170)), Btn("Browse…", () =>
                {
                    using (var d = new OpenFileDialog { Filter = "Odin tar|*.tar;*.md5" })
                        if (d.ShowDialog(this) == DialogResult.OK) { recoveryTar = d.FileName; Show(cur); }
                }));
                if (!string.IsNullOrEmpty(recoveryTar)) Status(Path.GetFileName(recoveryTar), File.Exists(recoveryTar));
                return;
            }

            Txt("Builds a recovery with fastbootd from your own stock firmware, so it matches your tablet exactly.");
            H("1. Get your stock firmware");
            Txt("Same build as your tablet (X110XXS4BYBA). Unzip it to its own folder.");
            Row(Btn("Open firmware site", () => OpenUrl(UrlFirmware)));

            H("2. Select the folder containing the AP_ and BL_ files");
            Row(PathBox(firmwareDir, W - S(170)), Btn("Browse…", () =>
            {
                using (var d = new FolderBrowserDialog { Description = "Select the folder with the AP_ and BL_ .tar.md5 files" })
                    if (d.ShowDialog(this) == DialogResult.OK) { firmwareDir = d.SelectedPath; Show(cur); }
            }));
            string ap = FirstMatch(firmwareDir, "AP_*.tar.md5", "AP_*.tar");
            string bl = FirstMatch(firmwareDir, "BL_*.tar.md5", "BL_*.tar");
            if (!string.IsNullOrEmpty(firmwareDir))
            {
                Status("AP file: " + (ap != null ? Path.GetFileName(ap) : "not found"), ap != null);
                Status("BL file: " + (bl != null ? Path.GetFileName(bl) : "not found"), bl != null);
                if (ap != null && !Path.GetFileName(ap).Contains("X110"))
                    Callout("That AP file doesn't look like SM-X110 firmware. Double-check before going on.", Warn);
            }

            H("3. Build the recovery");
            Txt("Takes a few minutes. The result is saved in an 'output' folder next to your firmware.");
            content.Controls.Add(Link("fastboot-patcher on GitHub", UrlPatcher));
            content.Controls.Add(Link("Patch-Recovery on GitHub (original)", UrlPatchRecovery));
            var build = Btn("Build fastbootd recovery", BuildRecovery, true);
            build.Enabled = ap != null;
            Row(build);
            if (!string.IsNullOrEmpty(recoveryTar) && File.Exists(recoveryTar))
            {
                Status("Ready: " + recoveryTar, true);
                Row(Btn("Show in folder", () => Process.Start("explorer.exe", "/select," + Q(recoveryTar))));
            }
        }

        void BuildRecovery()
        {
            string script = Path.Combine(firmwareDir, "patch-recovery.sh");
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("patch.sh"))
                using (var r = new StreamReader(s))
                {
                    var text = r.ReadToEnd().Replace("\r\n", "\n");
                    File.WriteAllBytes(script, new UTF8Encoding(false).GetBytes(text));
                }
            }
            catch (Exception ex) { Log("Couldn't write the patch script: " + ex.Message); return; }

            var wsl = Sys32("wsl.exe");
            Log("Installing Linux tools in WSL (as root, no password needed)…");
            Run(wsl, "-u root -e sh -c \"apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq git wget lz4 tar openssl python3\"", firmwareDir, (c1, o1) =>
            {
                if (c1 != 0) { Log("Package install failed. Check WSL has internet, then try again."); Show(cur); return; }
                Run(wsl, "-u root -e env SKIP_APT=1 bash ./patch-recovery.sh", firmwareDir, (c2, o2) =>
                {
                    var outTar = Path.Combine(firmwareDir, "output", "SM-X110-fastbootd-recovery.tar.md5");
                    if (c2 == 0 && File.Exists(outTar))
                    {
                        recoveryTar = outTar;
                        Log("Recovery built: " + outTar);
                    }
                    else Log("Build failed. Use 'Copy log' and check the messages above.");
                    Show(cur);
                });
            });
        }

        // ---------- 6 ----------
        void PageOdin()
        {
            H("1. Download mode");
            Steps(
                "Power the tablet off.",
                "Hold Volume Up + Volume Down and plug in USB.",
                "Short-press Volume Up. You'll see a blue screen.");

            H("2. Flash with Odin");
            Row(
                Btn("Open Odin", () =>
                {
                    try { Process.Start(new ProcessStartInfo(odinExe) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(odinExe) }); }
                    catch (Exception ex) { Log("Couldn't open Odin: " + ex.Message); }
                }, true),
                Btn("Copy recovery path", () => { try { Clipboard.SetText(recoveryTar ?? ""); Log("Copied: " + recoveryTar); } catch { } }));
            Steps(
                "Options tab: untick Auto Reboot.",
                "Click AP, paste the path into the file box, Open.",
                "Click Start and wait for green PASS!");
            Callout("After PASS the tablet stays on the blue screen, and its progress bar may stop partway (often ~25%). That's normal.", Muted);
            Check("odin_pass", "Odin showed PASS!");

            H("3. Go straight into recovery");
            Txt("Do this from the blue screen, cable still in. If the tablet starts normally instead, Samsung may restore the stock recovery.");
            Steps(
                "Hold Power + Volume Down until the screen goes black.",
                "Keep holding Power. Move your other finger from Volume Down to Volume Up.",
                "Hold Power + Volume Up until the recovery menu appears.",
                "Choose 'Enter fastboot' (volume keys move, Power selects).");
            Callout("Lines like 'Failed to load locales' or 'No command specified' at the bottom are normal. Reached the home screen instead? Repeat from step 1.", Muted);
            Check("in_fastbootd", "The tablet shows the fastbootd screen.");
        }

        // ---------- 7 ----------
        void PageConnect()
        {
            Txt("With the tablet on the fastbootd screen and plugged in, check the connection.");
            if (fastbootOk) Status("Connected in fastbootd", true);
            Row(Btn("Check connection", CheckFastboot, !fastbootOk));
            H("Not detected? Install the Google USB driver");
            Steps(
                "Download and unzip it (button below).",
                "Device Manager: right-click the tablet (usually under 'Other devices') → Update driver.",
                "Browse my computer → Let me pick → Have Disk → select android_winusb.inf.",
                "Pick 'Android Bootloader Interface', accept the warning, then Check connection.");
            Row(Btn("Google USB driver", () => OpenUrl(UrlGoogleDriver)), Btn("Open Device Manager", () => { try { Process.Start("devmgmt.msc"); } catch { } }));
        }

        void CheckFastboot()
        {
            Run(Fastboot, "devices", platformTools, (c1, o1) =>
            {
                if (!o1.Contains("fastboot"))
                {
                    fastbootOk = false;
                    Log("No device found in fastboot.");
                    Show(cur);
                    return;
                }
                Run(Fastboot, "getvar is-userspace", platformTools, (c2, o2) =>
                {
                    fastbootOk = o2.Contains("is-userspace: yes");
                    if (!fastbootOk) Log("Connected, but not in fastbootd (userspace). Choose 'Enter fastboot' in the custom recovery.");
                    Show(cur);
                });
            });
        }

        // ---------- 8 ----------
        void PageGsi()
        {
            Txt("Recommended: LineageOS-23.2-…-GAPPS-EROFS-GSI.7z (has Play Store). VANILLA = no Google. If EROFS won't boot, try EXT4.");
            Row(Btn("Open LineageOS GSI releases", () => OpenUrl(UrlGsi)));
            H("Select the download");
            Row(PathBox(gsiSource, W - S(170)), Btn("Browse…", () =>
            {
                using (var d = new OpenFileDialog { Filter = "LineageOS GSI|*.7z;*.img|All files|*.*" })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) return;
                    gsiSource = d.FileName;
                    gsiImg = gsiSource.EndsWith(".img", StringComparison.OrdinalIgnoreCase) ? gsiSource : null;
                    Show(cur);
                    if (gsiImg == null) ExtractGsi();
                }
            }));
            if (!string.IsNullOrEmpty(gsiImg) && File.Exists(gsiImg))
            {
                Status("Image ready: " + Path.GetFileName(gsiImg) + " (" + FileSize(gsiImg) + ")", true);
                if (gsiImg.IndexOf("lineage", StringComparison.OrdinalIgnoreCase) < 0 && gsiSource != null && gsiSource.IndexOf("lineage", StringComparison.OrdinalIgnoreCase) < 0)
                    Callout("That file name doesn't mention LineageOS. Make sure it's an arm64 A/B GSI.", Warn);
            }
            else if (!string.IsNullOrEmpty(gsiSource) && !gsiSource.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
            {
                Row(Btn("Extract again", ExtractGsi));
            }
        }

        void ExtractGsi()
        {
            var outDir = Path.Combine(appDir, "gsi");
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, true); Directory.CreateDirectory(outDir); } catch { }
            var seven = FindSevenZip();
            Action<int, string> after = (code, o) =>
            {
                string img = null;
                try { img = Directory.GetFiles(outDir, "*.img", SearchOption.AllDirectories).OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault(); } catch { }
                if (img != null) { gsiImg = img; Log("Extracted: " + img); }
                else
                {
                    Log("Couldn't extract the .img.");
                    if (seven == null)
                        MessageBox.Show(this, "Install 7-Zip (7-zip.org) and select the file again, or extract the .img yourself and select that instead.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                Show(cur);
            };
            Log("Extracting " + Path.GetFileName(gsiSource) + " (about 2–3 GB, give it a minute)…");
            if (seven != null) Run(seven, "e -y -bsp1 -o" + Q(outDir) + " " + Q(gsiSource) + " *.img -r", outDir, after);
            else Run(Sys32("tar.exe"), "-xf " + Q(gsiSource) + " -C " + Q(outDir), outDir, after);
        }

        // ---------- 9 ----------
        void PageFlash()
        {
            Txt("The tablet must be on the fastbootd screen.");
            Status("Image: " + Path.GetFileName(gsiImg ?? "") + " (" + (gsiImg != null ? FileSize(gsiImg) : "?") + ")", null);
            if (!flashed)
            {
                Callout("This replaces the tablet's system with LineageOS. Don't unplug the cable while it runs.", Warn);
                Row(Btn("Flash LineageOS now", FlashGsi, true));
            }
            else
            {
                Status("LineageOS flashed", true);
                H("Wipe data and boot");
                Row(Btn("Reboot to recovery", () => Run(Fastboot, "reboot recovery", platformTools, (c, o) => { })));
                Steps(
                    "In recovery, choose 'Wipe data/factory reset' and confirm.",
                    "Choose 'Reboot system now'.",
                    "First boot takes 5–10 minutes. Only worry if it loops past 15.");
                Check("wiped", "I wiped data and rebooted the tablet.");
            }
        }

        void FlashGsi()
        {
            if (MessageBox.Show(this, "Flash " + Path.GetFileName(gsiImg) + " to the tablet's system partition?\n\nKeep the cable connected until it finishes.", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Run(Fastboot, "getvar is-userspace", platformTools, (c0, o0) =>
            {
                if (!o0.Contains("is-userspace: yes"))
                {
                    Log("Tablet isn't in fastbootd. Go back to recovery and choose 'Enter fastboot'.");
                    return;
                }
                Run(Fastboot, "erase system", platformTools, (c1, o1) =>
                {
                    Run(Fastboot, "flash system " + Q(gsiImg), platformTools, (c2, o2) =>
                    {
                        if (c2 == 0 && !o2.Contains("FAILED")) { FlashDone(); return; }
                        var low = o2.ToLowerInvariant();
                        if (low.Contains("space") || low.Contains("resize"))
                        {
                            Log("Not enough room. Removing the 'product' partition to free space, then retrying…");
                            Run(Fastboot, "delete-logical-partition product", platformTools, (c3, o3) =>
                            {
                                Run(Fastboot, "flash system " + Q(gsiImg), platformTools, (c4, o4) =>
                                {
                                    if (c4 == 0 && !o4.Contains("FAILED")) FlashDone();
                                    else Log("Flash failed. Try the other build (EXT4 vs EROFS), and use 'Copy log' if you need help.");
                                });
                            });
                        }
                        else Log("Flash failed. Use 'Copy log' and check the messages above.");
                    });
                });
            });
        }

        void FlashDone()
        {
            flashed = true;
            Log("LineageOS flashed successfully.");
            Show(cur);
        }

        // ---------- 10 ----------
        void PageDone()
        {
            Txt("Done. In LineageOS, Settings → Treble Settings fixes Samsung quirks like brightness or audio.", Accent, boldFont);
            H("If something goes wrong");
            Steps(
                "Bootloop: go to recovery, wipe data again. Still looping? Try the EXT4 or VANILLA build.",
                "No Wi-Fi or sensors: check Treble Settings, or try a different GSI.",
                "Recovery won't stick: flash it again and boot straight into recovery.");
            H("Going back to stock");
            Txt("Download mode → Odin with BL, AP, CP and CSC from your firmware, Auto Reboot on, Start. Relock only after stock boots.");
            Row(Link("XDA guide", UrlXdaGuide), Link("LineageOS GSI releases", UrlGsi), Link("7-Zip", UrlSevenZip));
        }
    }
}
