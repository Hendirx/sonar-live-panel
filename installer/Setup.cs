// Setup for Sonar Live Panel. Installs for the current Windows user only (no admin needed):
// copies the panel to %LOCALAPPDATA%\Programs\Sonar Live Panel, adds shortcuts, registers in
// Settings > Apps, and copies itself there as Uninstall.exe. The panel's files are embedded
// as resources by build.bat. Built with the C# 5 compiler that ships with Windows.
//
//   SonarLivePanel-Setup.exe                         install / upgrade (wizard)
//   SonarLivePanel-Setup.exe /silent [/desktop] [/startup]
//   Uninstall.exe /uninstall [/silent] [/purge]      /purge also deletes settings, names and pin
//   Uninstall.exe /uninstall /fromapp                started from the panel's menu: closes it without asking
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Sonar Live Panel Setup")]
[assembly: AssemblyProduct("Sonar Live Panel")]
[assembly: AssemblyVersion("1.3.0.0")]       // keep in step with LivePanel.cs
[assembly: AssemblyFileVersion("1.3.0.0")]
// Setup usually runs from Downloads, where a malicious site could have dropped a fake DLL:
// load winmm/user32/shell32 only from System32, never from Setup's own folder.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace LivePanelSetup
{
    static class Product
    {
        public const string Name = "Sonar Live Panel";
        public const string Exe = "SonarLivePanel.exe";
        public const string Uninstaller = "Uninstall.exe";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SonarLivePanel";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string RunValue = "SonarLivePanel";

        // Replaced on every install.
        public static readonly string[] ProgramFiles = { Exe, "README.md" };
        // The user's own files: kept on upgrade and uninstall unless they ask to delete them.
        public static readonly string[] UserFiles = { "settings.ini", "names.txt", "pinned.txt", "debug-log.txt" };

        public static string Version
        {
            get { Version v = Assembly.GetExecutingAssembly().GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name); }
        }

        public static string ExePath { get { return Path.Combine(InstallDir, Exe); } }
        public static string UninstallerPath { get { return Path.Combine(InstallDir, Uninstaller); } }
        // Start Menu folder holding the app and an "Uninstall" shortcut.
        public static string StartMenuFolder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name); } }
        public static string StartMenuLink { get { return Path.Combine(StartMenuFolder, Name + ".lnk"); } }
        public static string UninstallLink { get { return Path.Combine(StartMenuFolder, "Uninstall " + Name + ".lnk"); } }
        // 1.0-1.1 put a single shortcut straight in Programs; updates and uninstall remove it.
        public static string LegacyStartMenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"); } }
        public static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk"); } }

        // Either the Apps entry or the program file counts, so a copy that lost its Apps entry is still
        // offered an update (which re-registers it) rather than a fresh install.
        public static bool IsInstalled
        {
            get
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    if (k != null) return true;
                return File.Exists(ExePath);
            }
        }

        public static bool StartsWithWindows
        {
            get { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunValue) != null; }
        }

        // Closes a running copy of the *installed* panel (not one started from elsewhere). False if the user declines.
        public static bool CloseRunningPanel(bool ask)
        {
            foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Exe)))
            {
                string path;
                try { path = p.MainModule.FileName; }
                catch (Exception) { continue; }
                if (!path.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (ask && MessageBox.Show("Sonar Live Panel is running. Close it and continue?", Name,
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                    return false;
                p.CloseMainWindow();   // lets the panel release its MIDI ports and docked screen space
                if (!p.WaitForExit(5000))
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            return true;
        }
    }

    class Options
    {
        public bool StartMenu = true, Desktop, Startup, Purge;
    }

    static class Installer
    {
        public static void Install(Options o)
        {
            string dir = Product.InstallDir;
            Directory.CreateDirectory(dir);
            foreach (string f in Product.ProgramFiles) Extract(f, Path.Combine(dir, f), true);
            Extract("settings.ini", Path.Combine(dir, "settings.ini"), false);   // keep the user's settings on upgrade

            string self = Application.ExecutablePath, uninstaller = Path.Combine(dir, Product.Uninstaller);
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninstaller), StringComparison.OrdinalIgnoreCase))
                File.Copy(self, uninstaller, true);

            DeleteFile(Product.LegacyStartMenuLink);
            if (o.StartMenu) Directory.CreateDirectory(Product.StartMenuFolder);
            SetShortcut(Product.StartMenuLink, o.StartMenu, Product.ExePath, "", "Switch Sonar tracks live from the computer keyboard");
            SetShortcut(Product.UninstallLink, o.StartMenu, uninstaller, "/uninstall", "Remove " + Product.Name);
            if (!o.StartMenu) DeleteEmptyFolder(Product.StartMenuFolder);
            SetShortcut(Product.DesktopLink, o.Desktop, Product.ExePath, "", "Switch Sonar tracks live from the computer keyboard");
            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(Product.RunKey))
            {
                if (o.Startup) run.SetValue(Product.RunValue, "\"" + Product.ExePath + "\"");
                else run.DeleteValue(Product.RunValue, false);
            }

            long bytes = 0;
            foreach (string f in Directory.GetFiles(dir)) bytes += new FileInfo(f).Length;
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Product.UninstallKey))
            {
                k.SetValue("DisplayName", Product.Name);
                k.SetValue("DisplayVersion", FileVersionInfo.GetVersionInfo(Product.ExePath).FileVersion ?? Product.Version);
                k.SetValue("Publisher", Product.Name);
                k.SetValue("DisplayIcon", Product.ExePath + ",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /silent");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            }
        }

        public static void Uninstall(Options o)
        {
            string dir = Product.InstallDir;
            DeleteFile(Product.StartMenuLink);
            DeleteFile(Product.UninstallLink);
            DeleteEmptyFolder(Product.StartMenuFolder);
            DeleteFile(Product.LegacyStartMenuLink);
            DeleteFile(Product.DesktopLink);
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(Product.RunKey, true))
                if (run != null) run.DeleteValue(Product.RunValue, false);
            Registry.CurrentUser.DeleteSubKeyTree(Product.UninstallKey, false);

            foreach (string f in Product.ProgramFiles) DeleteFile(Path.Combine(dir, f));
            if (o.Purge)
                foreach (string f in Product.UserFiles) DeleteFile(Path.Combine(dir, f));

            // Uninstall.exe can't delete itself while running: let a hidden cmd remove it (and the folder,
            // if it's now empty) a moment after this process exits.
            string cmd = string.Format("/c ping 127.0.0.1 -n 3 > nul & del /f /q \"{0}\" & rmdir \"{1}\"",
                Path.Combine(dir, Product.Uninstaller), dir);
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }

        static void Extract(string resource, string dest, bool overwrite)
        {
            if (!overwrite && File.Exists(dest)) return;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (s == null) throw new InvalidOperationException("Setup is missing " + resource + ". Rebuild it with build.bat.");
                using (FileStream f = File.Create(dest)) s.CopyTo(f);
            }
        }

        static void DeleteFile(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        static void DeleteEmptyFolder(string path)
        {
            if (Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0) Directory.Delete(path);
        }

        static void SetShortcut(string link, bool wanted, string target, string args, string description)
        {
            if (!wanted) { DeleteFile(link); return; }
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            try
            {
                dynamic s = shell.CreateShortcut(link);
                s.TargetPath = target;
                s.Arguments = args;
                s.WorkingDirectory = Product.InstallDir;
                s.Description = description;
                s.IconLocation = Product.ExePath + ",0";
                s.Save();
                Marshal.FinalReleaseComObject(s);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
    }

    // What the finish page says about loopMIDI, which the panel needs to reach Sonar.
    static class LoopMidi
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MIDIINCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwSupport; }

        [DllImport("winmm.dll")] static extern int midiInGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "midiInGetDevCapsW", CharSet = CharSet.Unicode)] static extern int midiInGetDevCaps(IntPtr id, ref MIDIINCAPS caps, int size);

        public const string Url = "https://www.tobias-erichsen.de/software/loopmidi.html";

        public static bool Installed()
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                using (RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (RegistryKey un = hklm.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (un == null) continue;
                    foreach (string sub in un.GetSubKeyNames())
                        using (RegistryKey k = un.OpenSubKey(sub))
                            if (k != null && "loopMIDI".Equals(k.GetValue("DisplayName") as string, StringComparison.OrdinalIgnoreCase))
                                return true;
                }
            return false;
        }

        // Port names from the panel's settings.ini, falling back to the defaults.
        static string[] WantedPorts()
        {
            string inPort = "From Sonar", outPort = "To Sonar", ini = Path.Combine(Product.InstallDir, "settings.ini");
            if (File.Exists(ini))
                foreach (string raw in File.ReadAllLines(ini))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("InPort=", StringComparison.OrdinalIgnoreCase)) inPort = line.Substring(7).Trim();
                    if (line.StartsWith("OutPort=", StringComparison.OrdinalIgnoreCase)) outPort = line.Substring(8).Trim();
                }
            return new[] { outPort, inPort };
        }

        public static List<string> MissingPorts()
        {
            List<string> names = new List<string>();
            for (int i = 0; i < midiInGetNumDevs(); i++)
            {
                MIDIINCAPS c = new MIDIINCAPS();
                if (midiInGetDevCaps((IntPtr)i, ref c, Marshal.SizeOf(c)) == 0) names.Add(c.szPname);
            }
            List<string> missing = new List<string>();
            foreach (string want in WantedPorts())
                if (!names.Exists(n => n.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)) missing.Add(want);
            return missing;
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("kernel32.dll")] static extern bool SetDefaultDllDirectories(int flags);
        const int LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;

        // Confirms, closes the installed panel, removes everything and says so. False if the user backed out.
        public static bool UninstallWithPrompts(bool askToClosePanel)
        {
            Options o = new Options();
            using (UninstallForm f = new UninstallForm())
            {
                if (f.ShowDialog() != DialogResult.OK) return false;
                o.Purge = f.Purge;
            }
            if (!Product.CloseRunningPanel(askToClosePanel)) return false;
            Installer.Uninstall(o);
            MessageBox.Show(o.Purge
                ? "Sonar Live Panel was removed."
                : "Sonar Live Panel was removed. Your settings, saved track names and pin were kept in:\n" + Product.InstallDir,
                Product.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }

        [STAThread]
        static int Main(string[] args)
        {
            SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);   // same for DLLs Windows loads on our behalf
            List<string> a = new List<string>();
            foreach (string s in args) a.Add(s.ToLowerInvariant());
            bool silent = a.Contains("/silent");
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                if (a.Contains("/uninstall"))
                {
                    if (!silent) return UninstallWithPrompts(!a.Contains("/fromapp")) ? 0 : 1;
                    Product.CloseRunningPanel(false);
                    Installer.Uninstall(new Options { Purge = a.Contains("/purge") });
                    return 0;
                }

                if (silent)
                {
                    if (!Product.CloseRunningPanel(false)) return 1;
                    Installer.Install(new Options { Desktop = a.Contains("/desktop"), Startup = a.Contains("/startup") });
                    return 0;
                }
                Application.Run(new InstallForm());
                return 0;
            }
            catch (Exception e)
            {
                if (!silent) MessageBox.Show(e.Message, Product.Name + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }
    }

    class SetupForm : Form
    {
        float scale = 1;

        // Layout is written in 96-DPI pixels; these convert to the display's real pixels.
        protected Point Pt(int x, int y) { return new Point((int)(x * scale), (int)(y * scale)); }
        protected Size Sz(int w, int h) { return new Size((int)(w * scale), (int)(h * scale)); }

        protected SetupForm()
        {
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;   // positions are scaled by Pt/Sz; fonts in points already follow DPI
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            Font = new Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (ArgumentException) { }
        }

        protected Label AddLabel(string text, int x, int y, int w, int h, float size, bool bold)
        {
            Label l = new Label { Text = text, Location = Pt(x, y), Size = Sz(w, h), Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
            Controls.Add(l);
            return l;
        }

        protected Button AddButton(string text, int x, int y, EventHandler click)
        {
            Button b = new Button { Text = text, Location = Pt(x, y), Size = Sz(96, 30) };
            b.Click += click;
            Controls.Add(b);
            return b;
        }
    }

    class InstallForm : SetupForm
    {
        readonly CheckBox startMenu, desktop, startup;
        readonly Panel optionsPage = new Panel(), donePage = new Panel();
        CheckBox launch;
        Label loopMidiStatus;
        LinkLabel loopMidiLink;

        public InstallForm()
        {
            bool upgrade = Product.IsInstalled;
            Text = Product.Name + " Setup";
            ClientSize = Sz(520, 330);

            AddLabel(Product.Name + " " + Product.Version, 24, 18, 470, 32, 15, true);
            foreach (Panel p in new[] { optionsPage, donePage })
            {
                p.Location = Pt(0, 56);
                p.Size = Sz(520, 274);
                Controls.Add(p);
            }

            Control[] options =
            {
                Text2("Switch Sonar tracks live from your computer keyboard, with a bar along the bottom of the screen.", 24, 0, 470, 36),
                Text2((upgrade ? "An earlier version is installed and will be updated. Your settings, saved track names and pin are kept.\n\n" : "")
                    + "Installs just for you (no admin needed) to:\n" + Product.InstallDir, 24, 44, 470, upgrade ? 86 : 48),
                startMenu = new CheckBox { Text = "Start Menu shortcut", Checked = true, Location = Pt(24, 140), AutoSize = true },
                desktop = new CheckBox { Text = "Desktop shortcut", Checked = upgrade && File.Exists(Product.DesktopLink), Location = Pt(24, 166), AutoSize = true },
                startup = new CheckBox { Text = "Start automatically when I sign in to Windows", Checked = upgrade && Product.StartsWithWindows, Location = Pt(24, 192), AutoSize = true },
            };
            optionsPage.Controls.AddRange(options);
            Button install = PageButton(optionsPage, upgrade ? "Update" : "Install", 300, Install);
            PageButton(optionsPage, "Cancel", 404, delegate { Close(); });
            if (upgrade)   // already installed: offer removal right next to Update
                PageButton(optionsPage, "Uninstall", 24, delegate
                {
                    // The confirmation dialog already asked, so close a running panel without a second question.
                    if (Program.UninstallWithPrompts(false)) Close();
                });
            AcceptButton = install;

            donePage.Visible = false;
            ResumeLayout(false);
        }

        Label Text2(string text, int x, int y, int w, int h)
        {
            return new Label { Text = text, Location = Pt(x, y), Size = Sz(w, h) };
        }

        Button PageButton(Panel page, string text, int x, EventHandler click)
        {
            Button b = new Button { Text = text, Location = Pt(x, 226), Size = Sz(96, 30) };
            b.Click += click;
            page.Controls.Add(b);
            return b;
        }

        void Install(object sender, EventArgs e)
        {
            if (!Product.CloseRunningPanel(true)) return;
            Cursor = Cursors.WaitCursor;
            try
            {
                Installer.Install(new Options { StartMenu = startMenu.Checked, Desktop = desktop.Checked, Startup = startup.Checked });
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                MessageBox.Show("Install failed: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Cursor = Cursors.Default;
            ShowDone();
        }

        void ShowDone()
        {
            optionsPage.Visible = false;
            donePage.Visible = true;

            string status;
            bool needLink = false;
            if (!LoopMidi.Installed())
            {
                status = "One more thing: the panel talks to Sonar through loopMIDI, which isn't installed. Install it, then create two ports named \"To Sonar\" and \"From Sonar\".";
                needLink = true;
            }
            else
            {
                List<string> missing = LoopMidi.MissingPorts();
                status = missing.Count == 0
                    ? "loopMIDI and its \"To Sonar\" / \"From Sonar\" ports were found."
                    : "loopMIDI is installed, but these ports aren't there yet: \"" + string.Join("\", \"", missing.ToArray())
                      + "\". Open loopMIDI and add them (and make sure it's running).";
            }

            donePage.Controls.Add(Text2("Sonar Live Panel is installed.", 24, 0, 470, 22));
            loopMidiStatus = Text2(status, 24, 28, 470, 60);
            donePage.Controls.Add(loopMidiStatus);
            if (needLink)
            {
                loopMidiLink = new LinkLabel { Text = "Get loopMIDI", Location = Pt(24, 90), AutoSize = true };
                loopMidiLink.LinkClicked += delegate { Process.Start(LoopMidi.Url); };
                donePage.Controls.Add(loopMidiLink);
            }
            donePage.Controls.Add(Text2("First time? The setup guide covers the Sonar settings it needs.", 24, 120, 470, 22));
            Button guide = new Button { Text = "Open setup guide", Location = Pt(24, 146), Size = Sz(140, 30) };
            guide.Click += delegate { Process.Start("notepad.exe", "\"" + Path.Combine(Product.InstallDir, "README.md") + "\""); };
            donePage.Controls.Add(guide);
            launch = new CheckBox { Text = "Start Sonar Live Panel now", Checked = true, Location = Pt(24, 194), AutoSize = true };
            donePage.Controls.Add(launch);
            Button finish = PageButton(donePage, "Finish", 404, delegate
            {
                if (launch.Checked) Process.Start(new ProcessStartInfo(Product.ExePath) { WorkingDirectory = Product.InstallDir });
                Close();
            });
            AcceptButton = finish;
            finish.Focus();
        }
    }

    class UninstallForm : SetupForm
    {
        readonly CheckBox purge;

        public bool Purge { get { return purge.Checked; } }

        public UninstallForm()
        {
            Text = "Uninstall " + Product.Name;
            ClientSize = Sz(460, 190);
            AddLabel("Remove Sonar Live Panel?", 24, 18, 410, 30, 13, true);
            AddLabel("This removes the program, its shortcuts and its entry in Settings > Apps.", 24, 56, 410, 36, 9, false);
            purge = new CheckBox { Text = "Also delete my settings, saved track names and pin", Location = Pt(24, 98), AutoSize = true };
            Controls.Add(purge);
            Button remove = AddButton("Uninstall", 240, 140, delegate { DialogResult = DialogResult.OK; });
            Button cancel = AddButton("Cancel", 344, 140, delegate { DialogResult = DialogResult.Cancel; });
            AcceptButton = remove;
            CancelButton = cancel;
            ResumeLayout(false);
        }
    }
}
