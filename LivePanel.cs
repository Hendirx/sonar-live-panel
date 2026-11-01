// Sonar Live Panel: pretends to be a Mackie Control surface so Cakewalk Sonar sends it
// the track names, then lets computer-keyboard hotkeys pick tracks like patch buttons on a synth.
// Built with the C# 5 compiler that ships with Windows (see build.bat), so no C# 6+ syntax.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Sonar Live Panel")]
[assembly: System.Reflection.AssemblyProduct("Sonar Live Panel")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]
// Load winmm/user32/shell32 only from System32, never from the exe's folder (DLL planting).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace LivePanel
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("kernel32.dll")] static extern bool SetDefaultDllDirectories(int flags);
        const int LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;

        [STAThread]
        static void Main()
        {
            SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);   // same for DLLs Windows loads on our behalf
            SetProcessDPIAware();   // draw at real pixel size instead of being stretched blurry by Windows
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            Settings s = Settings.Load(Path.Combine(dir, "settings.ini"));
            Application.EnableVisualStyles();
            Application.Run(new PanelForm(s));
        }
    }

    class Settings
    {
        public string InPort = "From Sonar";
        public string OutPort = "To Sonar";
        public bool StartLive = true;
        public bool AlwaysOnTop = true;
        public bool DockBottom = true;
        public bool ArmOnSelect = false;
        public bool NamesOnBottomRow = false;
        public double VolumeStep = 0.02;
        public int PanTicks = 1;
        public bool Debug = false;
        public bool StartLoopMidi = true;
        public string LoopMidiPath = "";

        // Action -> comma-separated keys (System.Windows.Forms.Keys names, optional Ctrl+/Alt+/Shift+).
        public readonly Dictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ToggleLive", "Scroll" },
            { "Select1", "D1,NumPad1" }, { "Select2", "D2,NumPad2" }, { "Select3", "D3,NumPad3" }, { "Select4", "D4,NumPad4" },
            { "Select5", "D5,NumPad5" }, { "Select6", "D6,NumPad6" }, { "Select7", "D7,NumPad7" }, { "Select8", "D8,NumPad8" },
            { "PrevTrack", "Up" }, { "NextTrack", "Down" },
            { "PrevBank", "PageUp" }, { "NextBank", "PageDown" },
            { "VolDown", "OemMinus,Subtract" }, { "VolUp", "Oemplus,Add" },
            { "PanLeft", "Left,OemOpenBrackets" }, { "PanRight", "Right,OemCloseBrackets" },
            { "PinVolDown", "Shift+OemMinus,Shift+Subtract" }, { "PinVolUp", "Shift+Oemplus,Shift+Add" },
        };

        public static Settings Load(string path)
        {
            Settings s = new Settings();
            if (!File.Exists(path)) return s;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                switch (k.ToLowerInvariant())
                {
                    case "inport": s.InPort = v; break;
                    case "outport": s.OutPort = v; break;
                    case "startlive": s.StartLive = Bool(v); break;
                    case "alwaysontop": s.AlwaysOnTop = Bool(v); break;
                    case "dockbottom": s.DockBottom = Bool(v); break;
                    case "armonselect": s.ArmOnSelect = Bool(v); break;
                    case "namesonbottomrow": s.NamesOnBottomRow = Bool(v); break;
                    case "volumestep": double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out s.VolumeStep); break;
                    case "panticks": int.TryParse(v, out s.PanTicks); break;
                    case "debug": s.Debug = Bool(v); break;
                    case "startloopmidi": s.StartLoopMidi = Bool(v); break;
                    case "loopmidipath": s.LoopMidiPath = v; break;
                    default: if (s.Keys.ContainsKey(k)) s.Keys[k] = v; break;
                }
            }
            return s;
        }

        static bool Bool(string v) { return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase); }
    }

    // The Mackie Control protocol side: no Windows UI or MIDI calls, so it can be tested on its own.
    class Mcu
    {
        public const int Strips = 8;
        const int SelectNote = 0x18, RecNote = 0x00, TouchNote = 0x68;
        const int BankLeft = 0x2E, BankRight = 0x2F, ChannelLeft = 0x30, ChannelRight = 0x31;

        public Action<int> SendShort = delegate { };
        public Action<byte[]> SendSysex = delegate { };
        public Func<long> Clock = delegate { return Environment.TickCount; };
        public event Action Changed = delegate { };
        public bool ArmOnSelect;
        public int NameOffset;   // 0 = top LCD row holds track names, 56 = bottom row

        public readonly char[] Lcd = new char[112];
        public readonly int[] Fader = new int[Strips];   // 0..16383
        public readonly bool[] FaderKnown = new bool[Strips];
        public readonly int[] Ring = new int[Strips];    // V-Pot LED ring byte
        public readonly bool[] SelLed = new bool[Strips];
        public readonly bool[] RecLed = new bool[Strips];
        public int Selected = -1;
        public bool HostSeen;

        // After a Select press Sonar briefly writes  Track 2: "sfizz 1"  across the whole name row,
        // then restores the 6-character names. That message gives the full name, so remember it.
        static readonly Regex Popup = new Regex("^\\s*[A-Za-z]+ \\d+: \"(.*)\"\\s*$");
        readonly char[] names = new char[56];
        // Short name -> full name; the form loads and saves this so names survive restarts.
        public readonly Dictionary<string, string> FullNames = new Dictionary<string, string>();
        public event Action NameLearned = delegate { };
        static readonly byte[] Serial = Encoding.ASCII.GetBytes("LIVEPNL");
        int pendingStrip = -1, pendingDir, lastPressed = -1;
        long pendingSince, pendingDeadline, lastLcd = -1, lastPressTime, lastRefresh = long.MinValue / 2;

        public Mcu()
        {
            for (int i = 0; i < Lcd.Length; i++) Lcd[i] = ' ';
            for (int i = 0; i < names.Length; i++) names[i] = ' ';
        }

        public string Name(int i) { return new string(names, i * 7, 7).Trim(); }
        public bool HasTrack(int i) { return Name(i).Length > 0; }

        public string FullName(int i)
        {
            string full, abbr = Name(i);
            return FullNames.TryGetValue(abbr, out full) ? full : abbr;
        }

        public void OnShort(int msg)
        {
            int status = msg & 0xFF, d1 = (msg >> 8) & 0x7F, d2 = (msg >> 16) & 0x7F;
            int type = status & 0xF0, ch = status & 0x0F;
            if (type == 0x80) { type = 0x90; d2 = 0; }
            if (type == 0x90 && d1 >= SelectNote && d1 < SelectNote + Strips) SetSelLed(d1 - SelectNote, d2 > 0);
            else if (type == 0x90 && d1 >= RecNote && d1 < RecNote + Strips) RecLed[d1 - RecNote] = d2 > 0;
            else if (type == 0xE0 && ch < Strips) { Fader[ch] = d1 | (d2 << 7); FaderKnown[ch] = true; }
            else if (type == 0xB0 && d1 >= 0x30 && d1 < 0x30 + Strips) Ring[d1 - 0x30] = d2;
            else return;
            HostSeen = true;
            Changed();
        }

        void SetSelLed(int i, bool on)
        {
            SelLed[i] = on;
            if (on) Selected = i;
            else if (Selected == i) Selected = Array.IndexOf(SelLed, true);
        }

        public void OnSysex(byte[] d)
        {
            if (d.Length < 7 || d[0] != 0xF0 || d[1] != 0 || d[2] != 0 || d[3] != 0x66) return;
            byte model = d[4], cmd = d[5];
            HostSeen = true;
            switch (cmd)
            {
                case 0x00: Reply(model, 0x01, Serial, new byte[] { 1, 2, 3, 4 }); break; // device query -> serial + challenge
                case 0x02: Reply(model, 0x03, Serial); break;                             // host reply -> connection confirmed
                case 0x1A: Reply(model, 0x1B, Serial); break;                             // serial number request
                case 0x13: Reply(model, 0x14, Encoding.ASCII.GetBytes("V1.02")); break;   // firmware version request
                case 0x12:                                                                 // LCD text: offset, chars...
                    int pos = d[6];
                    for (int k = 7; k < d.Length && d[k] != 0xF7; k++, pos++)
                        if (pos < Lcd.Length) Lcd[pos] = d[k] < 0x20 ? ' ' : (char)d[k];
                    Match popup = Popup.Match(new string(Lcd, NameOffset, 56));
                    if (!popup.Success)
                    {
                        Array.Copy(Lcd, NameOffset, names, 0, names.Length);
                        lastLcd = Clock();
                    }
                    else if (lastPressed >= 0 && Clock() - lastPressTime < 1000 && HasTrack(lastPressed))
                    {
                        string abbr = Name(lastPressed), full = popup.Groups[1].Value, old;
                        if (!FullNames.TryGetValue(abbr, out old) || old != full)
                        {
                            FullNames[abbr] = full;
                            NameLearned();
                        }
                    }
                    break;
            }
            Changed();
        }

        void Reply(byte model, byte cmd, params byte[][] parts)
        {
            List<byte> m = new List<byte> { 0xF0, 0x00, 0x00, 0x66, model, cmd };
            foreach (byte[] p in parts) m.AddRange(p);
            m.Add(0xF7);
            SendSysex(m.ToArray());
        }

        void Press(int note)
        {
            SendShort(0x90 | (note << 8) | (127 << 16));
            SendShort(0x90 | (note << 8));
        }

        // Sent once the ports open, and when the status line is clicked. Sonar only sends values that changed,
        // but per MidiInLongMsg in Cakewalk's MackieControlBase.cpp, a wake-up (0x01) clears its "have serial
        // number" flag and a serial number reply (0x1B) then makes it force-send every fader, ring, LED and LCD.
        public void Refresh()
        {
            lastRefresh = Clock();
            Reply(0x14, 0x01, Serial, new byte[] { 1, 2, 3, 4 });
            Reply(0x14, 0x1B, Serial);
        }

        public void Select(int i)
        {
            if (i < 0 || i >= Strips || !HasTrack(i)) return;
            if (i != Selected)   // Select toggles in Sonar, and it doesn't always light the LED, so never re-press
            {
                Press(SelectNote + i);
                lastPressed = i;
                lastPressTime = Clock();
            }
            if (ArmOnSelect)
            {
                // Arm is a toggle too, so track the state ourselves in case Sonar doesn't echo the LED.
                for (int k = 0; k < Strips; k++)
                    if (k != i && RecLed[k]) { Press(RecNote + k); RecLed[k] = false; }
                if (!RecLed[i]) { Press(RecNote + i); RecLed[i] = true; }
            }
            Selected = i;
            Changed();
        }

        // The pinned track (e.g. an always-on audio track) is shown on its own and skipped by track switching.
        public string PinnedName;

        public bool IsPinned(int i) { return PinnedName != null && HasTrack(i) && Name(i) == PinnedName; }
        public bool IsPlayable(int i) { return HasTrack(i) && !IsPinned(i); }

        public int PinnedStrip()
        {
            for (int i = 0; i < Strips; i++)
                if (IsPinned(i)) return i;
            return -1;
        }

        // Strip index of the n-th (0-based) switchable track in view, or -1.
        public int PlayableStrip(int n)
        {
            for (int i = 0; i < Strips; i++)
                if (IsPlayable(i) && n-- == 0) return i;
            return -1;
        }

        public void SelectPlayable(int n) { Select(PlayableStrip(n)); }

        public void Step(int dir)
        {
            int from = Selected >= 0 ? Selected : (dir > 0 ? -1 : Strips);
            for (int to = from + dir; to >= 0 && to < Strips; to += dir)
            {
                if (!HasTrack(to)) return;   // end of the track list
                if (IsPinned(to)) continue;
                Select(to);
                return;
            }
            // Past the edge of the visible 8: scroll one track, then pick the edge strip once the names refresh.
            // If nothing scrolls (no more tracks that way), the current selection simply stays.
            Press(dir > 0 ? ChannelRight : ChannelLeft);
            pendingStrip = dir > 0 ? Strips - 1 : 0;
            pendingDir = dir;
            pendingSince = Clock();
            pendingDeadline = pendingSince + 500;
        }

        public void Bank(int dir)
        {
            Press(dir > 0 ? BankRight : BankLeft);
            ForgetSelection();
        }

        // The strips now show different tracks; Sonar's LED feedback will re-light the selected one if it's in view.
        // Faders and rings stay: Sonar only skips sending a strip's value when it equals what it last sent for that
        // strip, which is exactly what we already hold.
        void ForgetSelection()
        {
            Array.Clear(SelLed, 0, Strips);
            Selected = -1;
            Changed();
        }

        // Called every ~50 ms from the UI timer.
        public bool AnyTrack()
        {
            for (int i = 0; i < Strips; i++)
                if (HasTrack(i)) return true;
            return false;
        }

        public void Tick()
        {
            long now = Clock();
            // Refresh is a resend request, not a button press, so retrying it can't change anything in Sonar.
            if (!AnyTrack() && now - lastRefresh > 3000) Refresh();
            if (pendingStrip < 0) return;
            if (lastLcd > pendingSince && now - lastLcd >= 60)
            {
                int s = pendingStrip;
                pendingStrip = -1;
                ForgetSelection();
                if (IsPinned(s)) Step(pendingDir);   // scrolled onto the pinned track: keep going
                else Select(s);
            }
            else if (now > pendingDeadline) pendingStrip = -1;
        }

        public void Volume(double delta) { StripVolume(Selected, delta); }
        public void PinnedVolume(double delta) { StripVolume(PinnedStrip(), delta); }

        void StripVolume(int i, double delta)
        {
            if (i < 0 || !FaderKnown[i]) return;   // never jump the fader from a guessed position
            int v =Math.Max(0, Math.Min(16383, Fader[i] + (int)Math.Round(delta * 16383)));
            SendShort(0x90 | ((TouchNote + i) << 8) | (127 << 16));
            SendShort(0xE0 | i | ((v & 0x7F) << 8) | ((v >> 7) << 16));
            SendShort(0x90 | ((TouchNote + i) << 8));
            Fader[i] = v;
            Changed();
        }

        public void Pan(int ticks)
        {
            int i = Selected;
            if (i < 0 || ticks == 0) return;
            int val = Math.Min(Math.Abs(ticks), 15) | (ticks < 0 ? 0x40 : 0);
            SendShort(0xB0 | ((0x10 + i) << 8) | (val << 16));
        }

        // Sonar sends its normalized volume (0..1) scaled linearly to 14 bits (MackieControlFader.cpp in
        // Cakewalk's Control Surface SDK). Its volume taper is cube-law with +6 dB at the top, which matches
        // Sonar's own readout (fader 12640 = -0.7 dB).
        public static string Decibels(int fader)
        {
            if (fader <= 0) return "-inf dB";
            // Sonar masks off the low 4 bits before sending (& 0xFFF0), so use the middle of that range.
            double db = 6 + 60 * Math.Log10(Math.Min(fader + 8, 16383) / 16383.0);
            return db < -144 ? "-inf dB" : (db > 0.05 ? "+" : "") + db.ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        }

        // -1 (hard left) .. 1 (hard right) from the V-Pot ring, or NaN if Sonar hasn't sent one.
        public double PanPosition(int i)
        {
            int pos = Ring[i] & 0x0F;
            return pos == 0 ? double.NaN : (pos - 6) / 5.0;
        }
    }

    // loopMIDI provides the two virtual ports the panel and Sonar talk over, so the panel starts it if needed.
    static class LoopMidi
    {
        public const string Url = "https://www.tobias-erichsen.de/software/loopmidi.html";

        public enum Action { None, Start, Wait, Missing, NotRunning }

        // running: loopMIDI is up. exe: where it's installed, or null. msSinceStart: since we last launched it.
        public static Action Decide(bool running, bool autoStart, string exe, long msSinceStart)
        {
            if (running) return Action.None;
            if (exe == null) return Action.Missing;
            if (!autoStart) return Action.NotRunning;
            return msSinceStart < 15000 ? Action.Wait : Action.Start;   // give it time to create its ports
        }

        public static bool IsRunning()
        {
            Process[] ps = Process.GetProcessesByName("loopMIDI");
            foreach (Process p in ps) p.Dispose();
            return ps.Length > 0;
        }

        // Its installer doesn't record the exe's location, so check the usual install folders.
        public static string FindExe(string configured)
        {
            List<string> candidates = new List<string>();
            if (!string.IsNullOrEmpty(configured)) candidates.Add(configured);
            foreach (Environment.SpecialFolder f in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
                candidates.Add(Path.Combine(Environment.GetFolderPath(f), @"Tobias Erichsen\loopMIDI\loopMIDI.exe"));
            return candidates.Find(File.Exists);
        }
    }

    // A pair of WinMM MIDI ports (e.g. two loopMIDI ports) with sysex input.
    sealed class MidiPorts : IDisposable
    {
        const int CALLBACK_FUNCTION = 0x30000, MIM_DATA = 0x3C3, MIM_LONGDATA = 0x3C4, MHDR_DONE = 1;
        const int BufferCount = 8, BufferSize = 1024;

        delegate void MidiInProc(IntPtr h, int msg, IntPtr inst, IntPtr p1, IntPtr p2);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MIDIINCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint dwSupport; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MIDIOUTCAPS { public ushort wMid, wPid; public uint vDriverVersion; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public ushort wTechnology, wVoices, wNotes, wChannelMask; public uint dwSupport; }

        [StructLayout(LayoutKind.Sequential)]
        struct MIDIHDR { public IntPtr lpData; public int dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public int dwFlags; public IntPtr lpNext, reserved; public int dwOffset; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public IntPtr[] dwReserved; }

        [DllImport("winmm.dll")] static extern int midiInGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "midiInGetDevCapsW", CharSet = CharSet.Unicode)] static extern int midiInGetDevCaps(IntPtr id, ref MIDIINCAPS caps, int size);
        [DllImport("winmm.dll")] static extern int midiInOpen(out IntPtr h, int id, MidiInProc cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int midiInStart(IntPtr h);
        [DllImport("winmm.dll")] static extern int midiInStop(IntPtr h);
        [DllImport("winmm.dll")] static extern int midiInReset(IntPtr h);
        [DllImport("winmm.dll")] static extern int midiInClose(IntPtr h);
        [DllImport("winmm.dll")] static extern int midiInPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int midiInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int midiInAddBuffer(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int midiOutGetNumDevs();
        [DllImport("winmm.dll", EntryPoint = "midiOutGetDevCapsW", CharSet = CharSet.Unicode)] static extern int midiOutGetDevCaps(IntPtr id, ref MIDIOUTCAPS caps, int size);
        [DllImport("winmm.dll")] static extern int midiOutOpen(out IntPtr h, int id, IntPtr cb, IntPtr inst, int flags);
        [DllImport("winmm.dll")] static extern int midiOutClose(IntPtr h);
        [DllImport("winmm.dll")] static extern int midiOutShortMsg(IntPtr h, int msg);
        [DllImport("winmm.dll")] static extern int midiOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int midiOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int midiOutLongMsg(IntPtr h, IntPtr hdr, int size);

        static readonly int HdrSize = Marshal.SizeOf(typeof(MIDIHDR));
        static readonly int FlagsOffset = Marshal.OffsetOf(typeof(MIDIHDR), "dwFlags").ToInt32();

        public event Action<int> Short = delegate { };
        public event Action<byte[]> Sysex = delegate { };

        IntPtr hin, hout;
        readonly MidiInProc proc;   // kept in a field so the GC can't collect the native callback
        readonly List<IntPtr> headers = new List<IntPtr>();
        readonly Queue<KeyValuePair<int, IntPtr>> queue = new Queue<KeyValuePair<int, IntPtr>>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        Thread worker;
        volatile bool closing;

        MidiPorts() { proc = OnMidiIn; }

        public static List<string> InputNames()
        {
            List<string> names = new List<string>();
            for (int i = 0; i < midiInGetNumDevs(); i++)
            {
                MIDIINCAPS c = new MIDIINCAPS();
                names.Add(midiInGetDevCaps((IntPtr)i, ref c, Marshal.SizeOf(c)) == 0 ? c.szPname : "");
            }
            return names;
        }

        public static List<string> OutputNames()
        {
            List<string> names = new List<string>();
            for (int i = 0; i < midiOutGetNumDevs(); i++)
            {
                MIDIOUTCAPS c = new MIDIOUTCAPS();
                names.Add(midiOutGetDevCaps((IntPtr)i, ref c, Marshal.SizeOf(c)) == 0 ? c.szPname : "");
            }
            return names;
        }

        static int Find(List<string> names, string want)
        {
            int idx = names.FindIndex(n => n.Equals(want, StringComparison.OrdinalIgnoreCase));
            return idx >= 0 ? idx : names.FindIndex(n => n.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Returns null and an explanation if either port is missing or busy. Call Start() after hooking events.
        public static MidiPorts Open(string inName, string outName, out string error)
        {
            int inId = Find(InputNames(), inName), outId = Find(OutputNames(), outName);
            if (inId < 0 || outId < 0)
            {
                error = MissingPortHint(inId < 0 ? inName : outName);
                return null;
            }
            MidiPorts p = new MidiPorts();
            if (midiOutOpen(out p.hout, outId, IntPtr.Zero, IntPtr.Zero, 0) != 0)
            {
                error = string.Format("Couldn't open \"{0}\" (another program may have it open).", outName);
                return null;
            }
            if (midiInOpen(out p.hin, inId, p.proc, IntPtr.Zero, CALLBACK_FUNCTION) != 0)
            {
                midiOutClose(p.hout);
                error = string.Format("Couldn't open \"{0}\" (another program may have it open).", inName);
                return null;
            }
            for (int i = 0; i < BufferCount; i++)
            {
                MIDIHDR h = new MIDIHDR { lpData = Marshal.AllocHGlobal(BufferSize), dwBufferLength = BufferSize, dwReserved = new IntPtr[8] };
                IntPtr ptr = Marshal.AllocHGlobal(HdrSize);
                Marshal.StructureToPtr(h, ptr, false);
                midiInPrepareHeader(p.hin, ptr, HdrSize);
                midiInAddBuffer(p.hin, ptr, HdrSize);
                p.headers.Add(ptr);
            }
            error = "";
            return p;
        }

        // Tells the user what to do when a port is missing, depending on whether loopMIDI is up.
        public static string MissingPortHint(string port)
        {
            return string.Format("loopMIDI has no port named \"{0}\" yet. Open loopMIDI and add it.", port);
        }

        public void Start()
        {
            worker = new Thread(Pump) { IsBackground = true, Name = "MIDI in" };
            worker.Start();
            midiInStart(hin);
        }

        void OnMidiIn(IntPtr h, int msg, IntPtr inst, IntPtr p1, IntPtr p2)
        {
            if (msg != MIM_DATA && msg != MIM_LONGDATA) return;
            lock (queue) queue.Enqueue(new KeyValuePair<int, IntPtr>(msg, p1));
            signal.Set();
        }

        // Runs off the driver callback, since WinMM forbids calling midiIn* from inside it.
        void Pump()
        {
            while (true)
            {
                signal.WaitOne();
                while (true)
                {
                    KeyValuePair<int, IntPtr> item;
                    lock (queue)
                    {
                        if (queue.Count == 0) break;
                        item = queue.Dequeue();
                    }
                    if (item.Key == MIM_DATA) { Short((int)item.Value.ToInt64()); continue; }
                    MIDIHDR hdr = (MIDIHDR)Marshal.PtrToStructure(item.Value, typeof(MIDIHDR));
                    if (hdr.dwBytesRecorded > 0 && !closing)
                    {
                        byte[] data = new byte[hdr.dwBytesRecorded];
                        Marshal.Copy(hdr.lpData, data, 0, data.Length);
                        Sysex(data);
                    }
                    if (!closing) midiInAddBuffer(hin, item.Value, HdrSize);
                }
                if (closing) return;
            }
        }

        public void SendShort(int msg) { midiOutShortMsg(hout, msg); }

        public void SendSysex(byte[] data)
        {
            IntPtr buf = Marshal.AllocHGlobal(data.Length), ptr = Marshal.AllocHGlobal(HdrSize);
            try
            {
                Marshal.Copy(data, 0, buf, data.Length);
                MIDIHDR h = new MIDIHDR { lpData = buf, dwBufferLength = data.Length, dwBytesRecorded = data.Length, dwReserved = new IntPtr[8] };
                Marshal.StructureToPtr(h, ptr, false);
                midiOutPrepareHeader(hout, ptr, HdrSize);
                if (midiOutLongMsg(hout, ptr, HdrSize) == 0)
                    for (int t = 0; t < 100 && (Marshal.ReadInt32(ptr, FlagsOffset) & MHDR_DONE) == 0; t++) Thread.Sleep(5);
                midiOutUnprepareHeader(hout, ptr, HdrSize);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                Marshal.FreeHGlobal(ptr);
            }
        }

        public void Dispose()
        {
            closing = true;
            midiInStop(hin);
            midiInReset(hin);
            signal.Set();
            if (worker != null) worker.Join(1000);
            foreach (IntPtr ptr in headers)
            {
                midiInUnprepareHeader(hin, ptr, HdrSize);
                Marshal.FreeHGlobal(((MIDIHDR)Marshal.PtrToStructure(ptr, typeof(MIDIHDR))).lpData);
                Marshal.FreeHGlobal(ptr);
            }
            midiInClose(hin);
            midiOutClose(hout);
        }
    }

    class PanelForm : Form
    {
        const int WM_HOTKEY = 0x0312, MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
        const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_APPWINDOW = 0x00040000;

        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, int mods, int vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);

        static readonly Color Bg = Color.FromArgb(17, 19, 22), Tile = Color.FromArgb(32, 36, 42), TileEmpty = Color.FromArgb(23, 26, 30);
        static readonly Color Fg = Color.FromArgb(232, 234, 237), Dim = Color.FromArgb(120, 128, 140);
        static readonly Color Accent = Color.FromArgb(240, 168, 48), AccentFg = Color.FromArgb(24, 20, 12), LiveOn = Color.FromArgb(62, 207, 110);
        static readonly Color Pinned = Color.FromArgb(24, 58, 62), PinnedFg = Color.FromArgb(110, 200, 205);

        class Binding { public string Action; public int Mods, Vk, Id; public bool Registered; }

        readonly Settings settings;
        readonly Mcu mcu = new Mcu();
        readonly Dictionary<string, Action> actions = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase);
        readonly List<Binding> bindings = new List<Binding>();
        readonly List<string> keyProblems = new List<string>();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 50 };
        MidiPorts ports;
        string portError = "Opening MIDI ports...";
        int lastPortTry = int.MinValue / 2;
        bool live;
        StreamWriter log;

        public PanelForm(Settings s)
        {
            settings = s;
            mcu.ArmOnSelect = s.ArmOnSelect;
            mcu.NameOffset = s.NamesOnBottomRow ? 56 : 0;
            mcu.Changed += Invalidate;
            LoadNames();
            LoadPinned();
            mcu.NameLearned += SaveNames;
            if (s.Debug)
            {
                log = new StreamWriter(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "debug-log.txt"), false) { AutoFlush = true };
                Log("MIDI inputs:  " + string.Join(" | ", MidiPorts.InputNames().ToArray()));
                Log("MIDI outputs: " + string.Join(" | ", MidiPorts.OutputNames().ToArray()));
            }

            Text = "Sonar Live Panel";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (ArgumentException) { }
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            StartPosition = FormStartPosition.Manual;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            if (s.DockBottom)
            {
                // Final position comes from the app bar registration in OnHandleCreated.
                FormBorderStyle = FormBorderStyle.None;
                Bounds = new Rectangle(wa.Left, wa.Bottom - D(BarHeight), wa.Width, D(BarHeight));
            }
            else
            {
                ClientSize = new Size(Math.Min(wa.Width, D(1280)), D(BarHeight));
                Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height);
            }
            BackColor = Bg;
            TopMost = s.AlwaysOnTop;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            actions["PrevTrack"] = () => mcu.Step(-1);
            actions["NextTrack"] = () => mcu.Step(1);
            actions["PrevBank"] = () => mcu.Bank(-1);
            actions["NextBank"] = () => mcu.Bank(1);
            actions["VolUp"] = () => mcu.Volume(settings.VolumeStep);
            actions["VolDown"] = () => mcu.Volume(-settings.VolumeStep);
            actions["PanLeft"] = () => mcu.Pan(-settings.PanTicks);
            actions["PanRight"] = () => mcu.Pan(settings.PanTicks);
            actions["PinVolUp"] = () => mcu.PinnedVolume(settings.VolumeStep);
            actions["PinVolDown"] = () => mcu.PinnedVolume(-settings.VolumeStep);
            for (int i = 0; i < Mcu.Strips; i++)
            {
                int n = i;
                actions["Select" + (i + 1)] = () => mcu.SelectPlayable(n);   // number keys skip the pinned track
            }
            ParseBindings();

            timer.Tick += delegate
            {
                mcu.Tick();
                if (ports == null && Environment.TickCount - lastPortTry > 2000) TryOpenPorts();
            };
        }

        // Show and click without stealing keyboard focus from Sonar.
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_APPWINDOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (settings.DockBottom) RegisterAppBar();
            foreach (Binding b in bindings)
                if (b.Action == "ToggleLive") Register(b);
            SetLive(settings.StartLive);
            TryOpenPorts();
            timer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            timer.Stop();
            foreach (Binding b in bindings)
                if (b.Registered) UnregisterHotKey(Handle, b.Id);
            if (ports != null) ports.Dispose();
            ports = null;
            if (log != null) log.Dispose();
            log = null;
            if (appBar)
            {
                APPBARDATA d = NewAppBarData();
                SHAppBarMessage(ABM_REMOVE, ref d);
                appBar = false;
            }
            base.OnFormClosing(e);
        }

        // ---- docking: register as a Windows app bar (like the taskbar) so maximized windows stop above it ----

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA { public int cbSize; public IntPtr hWnd; public int uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }

        [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(int msg, ref APPBARDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);

        const int ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABE_BOTTOM = 3, ABN_POSCHANGED = 1;
        int appBarMsg;
        bool appBar;

        APPBARDATA NewAppBarData()
        {
            APPBARDATA d = new APPBARDATA();
            d.cbSize = Marshal.SizeOf(d);
            d.hWnd = Handle;
            return d;
        }

        void RegisterAppBar()
        {
            appBarMsg = RegisterWindowMessage("SonarLivePanelAppBar");
            APPBARDATA d = NewAppBarData();
            d.uCallbackMessage = appBarMsg;
            SHAppBarMessage(ABM_NEW, ref d);
            appBar = true;
            PositionAppBar();
        }

        void PositionAppBar()
        {
            Rectangle sb = Screen.PrimaryScreen.Bounds;
            int h = D(BarHeight);
            APPBARDATA d = NewAppBarData();
            d.uEdge = ABE_BOTTOM;
            d.rc.Left = sb.Left;
            d.rc.Right = sb.Right;
            d.rc.Bottom = sb.Bottom;
            d.rc.Top = sb.Bottom - h;
            SHAppBarMessage(ABM_QUERYPOS, ref d);   // Windows moves the rect above the taskbar etc.
            d.rc.Top = d.rc.Bottom - h;
            SHAppBarMessage(ABM_SETPOS, ref d);
            Bounds = new Rectangle(d.rc.Left, d.rc.Top, d.rc.Right - d.rc.Left, h);
        }

        void ParseBindings()
        {
            int id = 1;
            foreach (KeyValuePair<string, string> kv in settings.Keys)
            {
                if (kv.Key != "ToggleLive" && !actions.ContainsKey(kv.Key)) continue;
                foreach (string combo in kv.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int mods = 0, vk = 0;
                    try
                    {
                        foreach (string part in combo.Split('+'))
                        {
                            string p = part.Trim();
                            if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) mods |= MOD_CONTROL;
                            else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mods |= MOD_ALT;
                            else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mods |= MOD_SHIFT;
                            else vk = (int)((Keys)Enum.Parse(typeof(Keys), p, true) & Keys.KeyCode);
                        }
                    }
                    catch (ArgumentException)
                    {
                        keyProblems.Add("unknown key \"" + combo.Trim() + "\"");
                        continue;
                    }
                    bool repeats = kv.Key.StartsWith("Vol") || kv.Key.StartsWith("Pan") || kv.Key.StartsWith("PinVol") || kv.Key.EndsWith("Track");
                    bindings.Add(new Binding { Action = kv.Key, Mods = mods | (repeats ? 0 : MOD_NOREPEAT), Vk = vk, Id = id++ });
                }
            }
        }

        void Register(Binding b)
        {
            b.Registered = RegisterHotKey(Handle, b.Id, b.Mods, b.Vk);
            if (!b.Registered) keyProblems.Add(KeyLabel(b) + " is taken by another app");
        }

        void SetLive(bool on)
        {
            live = on;
            keyProblems.RemoveAll(p => p.EndsWith("another app"));
            foreach (Binding b in bindings)
            {
                if (b.Action == "ToggleLive") { if (!b.Registered) Register(b); continue; }
                if (on && !b.Registered) Register(b);
                else if (!on && b.Registered) { UnregisterHotKey(Handle, b.Id); b.Registered = false; }
            }
            Invalidate();
        }

        void TryOpenPorts()
        {
            lastPortTry = Environment.TickCount;
            MidiPorts p = MidiPorts.Open(settings.InPort, settings.OutPort, out portError);
            if (p == null && log != null && portError != lastLoggedError) Log(lastLoggedError = portError);
            if (p == null) CheckLoopMidi();
            else loopMidi = LoopMidi.Action.None;
            if (p != null)
            {
                Log("Ports open");
                p.Short += m => Post(() => { Log("in   " + ShortHex(m)); mcu.OnShort(m); });
                p.Sysex += d => Post(() =>
                {
                    Log("in   " + BitConverter.ToString(d));
                    mcu.OnSysex(d);
                    if (d.Length > 5 && d[5] == 0x12)
                        Log("LCD  top    |" + new string(mcu.Lcd, 0, 56) + "|\r\n             bottom |" + new string(mcu.Lcd, 56, 56) + "|");
                });
                mcu.SendShort = m => { Log("out  " + ShortHex(m)); p.SendShort(m); };
                mcu.SendSysex = d => { Log("out  " + BitConverter.ToString(d)); p.SendSysex(d); };
                ports = p;
                p.Start();
                mcu.Refresh();
            }
            Invalidate();
        }

        string lastLoggedError;

        // Full track names learned from Sonar, as "short=full" lines, so they survive restarts.
        static string NamesFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "names.txt"); } }

        void LoadNames()
        {
            if (!File.Exists(NamesFile)) return;
            foreach (string line in File.ReadAllLines(NamesFile))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) mcu.FullNames[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
        }

        void SaveNames()
        {
            List<string> lines = new List<string>();
            foreach (KeyValuePair<string, string> kv in mcu.FullNames) lines.Add(kv.Key + "=" + kv.Value);
            try { File.WriteAllLines(NamesFile, lines.ToArray()); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // The pinned track's short name, kept in pinned.txt so it survives restarts.
        static string PinnedFile { get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pinned.txt"); } }

        void LoadPinned()
        {
            if (File.Exists(PinnedFile))
            {
                string name = File.ReadAllText(PinnedFile).Trim();
                mcu.PinnedName = name.Length > 0 ? name : null;
            }
        }

        void SetPinned(string name)
        {
            mcu.PinnedName = name;
            try { File.WriteAllText(PinnedFile, name ?? ""); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Invalidate();
        }

        void Log(string s)
        {
            if (log != null) log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s);
        }

        static string ShortHex(int m)
        {
            return string.Format("{0:X2} {1:X2} {2:X2}", m & 0xFF, (m >> 8) & 0xFF, (m >> 16) & 0xFF);
        }

        void Post(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();
                Binding b = bindings.Find(x => x.Id == id);
                if (b != null)
                {
                    if (b.Action == "ToggleLive") SetLive(!live);
                    else if (live) actions[b.Action]();
                }
                return;
            }
            if (appBar && m.Msg == appBarMsg && m.WParam.ToInt32() == ABN_POSCHANGED) PositionAppBar();
            base.WndProc(ref m);
        }

        // ---- layout ----
        // One slim row: [LIVE] [pinned track] [track buttons...] [current track: volume + pan] [Refresh] [x]

        const int BarHeight = 84;   // logical pixels (scaled for display DPI)
        float scale = 1;

        int D(float v) { return (int)Math.Round(v * scale); }

        class BarLayout
        {
            public Rectangle Live, Pinned, Tracks, Current, Knob, Refresh, Close;
            public readonly List<KeyValuePair<int, Rectangle>> Tiles = new List<KeyValuePair<int, Rectangle>>();
        }

        BarLayout ComputeLayout()
        {
            BarLayout l = new BarLayout();
            int pad = D(10), gap = D(8), h = ClientSize.Height - 2 * pad, y = pad;
            int x = pad;
            l.Live = new Rectangle(x, y + (h - D(36)) / 2, D(76), D(36));
            x = l.Live.Right + gap;

            int right = ClientSize.Width - pad;
            l.Close = new Rectangle(right - D(28), y + (h - D(28)) / 2, D(28), D(28));
            l.Refresh = new Rectangle(l.Close.Left - gap - D(78), y + (h - D(30)) / 2, D(78), D(30));
            l.Current = new Rectangle(l.Refresh.Left - gap - D(390), y, D(390), h);
            int knob = Math.Min(h - D(8), D(52));
            l.Knob = new Rectangle(l.Current.Right - D(140), y + (h - knob) / 2, knob, knob);

            if (mcu.PinnedStrip() >= 0)
            {
                l.Pinned = new Rectangle(x, y, D(200), h);
                x = l.Pinned.Right + gap * 2;
            }
            l.Tracks = new Rectangle(x, y, Math.Max(0, l.Current.Left - gap * 2 - x), h);

            List<int> strips = new List<int>();
            for (int i = 0; i < Mcu.Strips; i++)
                if (mcu.IsPlayable(i)) strips.Add(i);
            if (strips.Count > 0)
            {
                int w = Math.Min(D(170), (l.Tracks.Width - gap * (strips.Count - 1)) / strips.Count);
                for (int k = 0; k < strips.Count; k++)
                    l.Tiles.Add(new KeyValuePair<int, Rectangle>(strips[k], new Rectangle(x + k * (w + gap), y, w, h)));
            }
            return l;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            BarLayout l = ComputeLayout();
            if (e.Button == MouseButtons.Left)
            {
                if (l.Live.Contains(e.Location)) SetLive(!live);
                else if (l.Refresh.Contains(e.Location)) mcu.Refresh();
                else if (l.Close.Contains(e.Location)) Close();
                else if (ports == null && loopMidi == LoopMidi.Action.Missing && l.Tracks.Contains(e.Location)) Process.Start(LoopMidi.Url);
                else foreach (KeyValuePair<int, Rectangle> t in l.Tiles)
                    if (t.Value.Contains(e.Location)) mcu.Select(t.Key);
            }
            else if (e.Button == MouseButtons.Right)
            {
                // Right-click a track to pin it (always shown, skipped by switching); right-click the pinned one to unpin.
                if (l.Pinned.Contains(e.Location)) SetPinned(null);
                else foreach (KeyValuePair<int, Rectangle> t in l.Tiles)
                    if (t.Value.Contains(e.Location)) SetPinned(mcu.Name(t.Key));
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            BarLayout l = ComputeLayout();
            int dir = Math.Sign(e.Delta);
            if (l.Pinned.Contains(e.Location)) mcu.PinnedVolume(dir * settings.VolumeStep);
            else if (l.Knob.Contains(e.Location)) mcu.Pan(dir * settings.PanTicks);
            else if (l.Current.Contains(e.Location)) mcu.Volume(dir * settings.VolumeStep);
        }

        // ---- drawing ----

        Font Px(float px, bool bold) { return new Font("Segoe UI" + (bold ? " Semibold" : ""), Math.Max(8f, px * scale), GraphicsUnit.Pixel); }

        // Shrinks the font until the text fits the width, so long full track names stay readable.
        Font FitFont(string text, Font start, int width)
        {
            float px = start.Size;
            while (px > 11 * scale)
            {
                using (Font f = new Font(start.FontFamily, px, start.Style, GraphicsUnit.Pixel))
                    if (TextRenderer.MeasureText(text, f).Width <= width) break;
                px *= 0.9f;
            }
            return new Font(start.FontFamily, px, start.Style, GraphicsUnit.Pixel);
        }

        static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static void Fill(Graphics g, Rectangle r, int radius, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        static void DrawLabel(Graphics g, string s, Font f, Rectangle r, Color c, TextFormatFlags align)
        {
            TextRenderer.DrawText(g, s, f, r, c, align | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        }

        LoopMidi.Action loopMidi = LoopMidi.Action.None;
        int lastLoopMidiStart = int.MinValue / 2;

        // Called while the ports can't be opened: starts loopMIDI if it's installed but not running.
        void CheckLoopMidi()
        {
            string exe = LoopMidi.FindExe(settings.LoopMidiPath);
            loopMidi = LoopMidi.Decide(LoopMidi.IsRunning(), settings.StartLoopMidi, exe, Environment.TickCount - lastLoopMidiStart);
            if (loopMidi != LoopMidi.Action.Start) return;
            lastLoopMidiStart = Environment.TickCount;
            Log("Starting loopMIDI: " + exe);
            try { Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), WindowStyle = ProcessWindowStyle.Minimized }); }
            catch (Win32Exception) { loopMidi = LoopMidi.Action.Missing; }
        }

        string Status()
        {
            if (ports == null)
            {
                if (loopMidi == LoopMidi.Action.Missing)
                    return "loopMIDI is required but wasn't found. Click here to download it (or set LoopMidiPath in settings.ini)";
                if (loopMidi == LoopMidi.Action.NotRunning) return "loopMIDI isn't running. Start it and the panel connects by itself";
                if (Environment.TickCount - lastLoopMidiStart < 8000) return "Starting loopMIDI...";
                return portError;
            }
            if (!mcu.HostSeen) return "Ports open - waiting for Sonar (is the Mackie Control surface set up?)";
            if (!mcu.AnyTrack()) return "Connected, waiting for tracks (retrying). Sonar ignores the panel while a dialog is open";
            return "";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Bg);
            BarLayout l = ComputeLayout();
            int radius = D(10);

            using (Font small = Px(11, false), smallBold = Px(12, true), name = Px(18, true))
            {
                Fill(g, l.Live, l.Live.Height / 2, live ? LiveOn : Tile);
                DrawLabel(g, live ? "LIVE" : "OFF", smallBold, l.Live, live ? AccentFg : Dim, TextFormatFlags.HorizontalCenter);

                // Pinned (always-on) track.
                int pin = mcu.PinnedStrip();
                if (pin >= 0)
                {
                    Rectangle p = l.Pinned;
                    Fill(g, p, radius, Pinned);
                    DrawLabel(g, "PINNED  " + KeyHint("PinVolDown") + " / " + KeyHint("PinVolUp"), small, new Rectangle(p.X + D(10), p.Y + D(4), p.Width - D(20), D(16)), PinnedFg, TextFormatFlags.Left);
                    Rectangle nr = new Rectangle(p.X + D(10), p.Y + D(20), p.Width - D(20), D(24));
                    string label = mcu.FullName(pin);
                    using (Font fit = FitFont(label, name, nr.Width))
                        DrawLabel(g, label, fit, nr, Fg, TextFormatFlags.Left);
                    DrawVolume(g, pin, new Rectangle(p.X + D(10), p.Bottom - D(16), p.Width - D(20), D(10)), smallBold, false);
                }

                // Switchable tracks, or the connection status while there are none.
                string status = Status();
                if (status.Length > 0 || l.Tiles.Count == 0)
                    DrawLabel(g, status.Length > 0 ? status : "No tracks", small, l.Tracks, Accent, TextFormatFlags.Left);
                int n = 0;
                foreach (KeyValuePair<int, Rectangle> t in l.Tiles)
                {
                    Rectangle r = t.Value;
                    bool sel = t.Key == mcu.Selected;
                    Fill(g, r, radius, sel ? Accent : Tile);
                    DrawLabel(g, KeyHint("Select" + (++n)), small, new Rectangle(r.X + D(8), r.Y + D(4), r.Width - D(16), D(16)), sel ? AccentFg : Dim, TextFormatFlags.Left);
                    Rectangle nr = new Rectangle(r.X + D(6), r.Y + D(14), r.Width - D(12), r.Height - D(14));
                    string label = mcu.FullName(t.Key);
                    using (Font fit = FitFont(label, name, nr.Width))
                        DrawLabel(g, label, fit, nr, sel ? AccentFg : Fg, TextFormatFlags.HorizontalCenter);
                }
                if (keyProblems.Count > 0)
                    DrawLabel(g, "Keys: " + string.Join(", ", keyProblems.ToArray()), small, new Rectangle(l.Tracks.X, l.Tracks.Bottom - D(14), l.Tracks.Width, D(14)), Accent, TextFormatFlags.Left);

                DrawCurrent(g, l, small, smallBold);

                Fill(g, l.Refresh, l.Refresh.Height / 2, Tile);
                DrawLabel(g, "Refresh", smallBold, l.Refresh, Fg, TextFormatFlags.HorizontalCenter);
                DrawLabel(g, "✕", smallBold, l.Close, Dim, TextFormatFlags.HorizontalCenter);
            }
        }

        // Volume bar with the dB value at its right.
        void DrawVolume(Graphics g, int strip, Rectangle bar, Font f, bool bigText)
        {
            int textW = D(64);
            Rectangle track = new Rectangle(bar.X, bar.Y, bar.Width - textW, bar.Height);
            Fill(g, track, track.Height / 2, TileEmpty);
            Rectangle text = new Rectangle(track.Right + D(8), bar.Y - D(6), textW - D(8), bar.Height + D(12));
            if (!mcu.FaderKnown[strip])
            {
                DrawLabel(g, "--", f, text, Dim, TextFormatFlags.Left);
                return;
            }
            int w = (int)(track.Width * (mcu.Fader[strip] / 16383.0));
            if (w > 0) Fill(g, new Rectangle(track.X, track.Y, Math.Max(w, track.Height), track.Height), track.Height / 2, Accent);
            DrawLabel(g, Mcu.Decibels(mcu.Fader[strip]), f, text, Fg, TextFormatFlags.Left);
        }

        void DrawCurrent(Graphics g, BarLayout l, Font small, Font smallBold)
        {
            Rectangle c = l.Current;
            Fill(g, c, D(10), Tile);
            int sel = mcu.Selected >= 0 && mcu.HasTrack(mcu.Selected) ? mcu.Selected : -1;
            int left = c.X + D(12), width = l.Knob.Left - D(12) - left;

            using (Font big = Px(16, true))
                DrawLabel(g, sel >= 0 ? mcu.FullName(sel) : "Pick a track", big, new Rectangle(left, c.Y + D(6), width, D(24)), sel >= 0 ? Accent : Dim, TextFormatFlags.Left);
            DrawLabel(g, "VOL  " + KeyHint("VolDown") + " / " + KeyHint("VolUp"), small, new Rectangle(left, c.Y + D(32), width, D(14)), Dim, TextFormatFlags.Left);
            if (sel >= 0) DrawVolume(g, sel, new Rectangle(left, c.Bottom - D(16), width, D(10)), smallBold, true);

            // Pan knob.
            Rectangle k = l.Knob;
            int stroke = Math.Max(2, D(5));
            using (Pen track = new Pen(TileEmpty, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(track, k, 135, 270);
            Rectangle labelR = new Rectangle(k.Right + D(8), c.Y, c.Right - k.Right - D(12), c.Height);
            double pan = sel >= 0 ? mcu.PanPosition(sel) : double.NaN;
            if (double.IsNaN(pan))
            {
                DrawLabel(g, "PAN", small, labelR, Dim, TextFormatFlags.Left);
                return;
            }
            float sweep = (float)(pan * 135);
            using (Pen val = new Pen(Accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                if (Math.Abs(sweep) > 0.5f) g.DrawArc(val, k, 270, sweep);
            double a = (270 + sweep) * Math.PI / 180;
            PointF ctr = new PointF(k.X + k.Width / 2f, k.Y + k.Height / 2f);
            using (Pen ptr = new Pen(Fg, Math.Max(2, D(3))) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(ptr, ctr, new PointF(ctr.X + (float)Math.Cos(a) * k.Width * 0.32f, ctr.Y + (float)Math.Sin(a) * k.Height * 0.32f));
            string label = Math.Abs(pan) < 0.01 ? "Centre" : (pan < 0 ? "L " : "R ") + (int)Math.Round(Math.Abs(pan) * 100) + "%";
            DrawLabel(g, "PAN  " + KeyHint("PanLeft") + "/" + KeyHint("PanRight"), small, new Rectangle(labelR.X, c.Y + D(8), labelR.Width, D(14)), Dim, TextFormatFlags.Left);
            DrawLabel(g, label, smallBold, new Rectangle(labelR.X, c.Y + D(24), labelR.Width, c.Height - D(30)), Fg, TextFormatFlags.Left);
        }

        string KeyHint(string action)
        {
            Binding b = bindings.Find(x => x.Action == action);
            return b == null ? "?" : KeyLabel(b);
        }

        static string KeyLabel(Binding b)
        {
            string s = "";
            if ((b.Mods & MOD_CONTROL) != 0) s += "Ctrl+";
            if ((b.Mods & MOD_ALT) != 0) s += "Alt+";
            if ((b.Mods & MOD_SHIFT) != 0) s += "Shift+";
            Keys k = (Keys)b.Vk;
            if (k >= Keys.D0 && k <= Keys.D9) return s + (char)('0' + (k - Keys.D0));
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return s + "Num" + (k - Keys.NumPad0);
            switch (k)
            {
                case Keys.Up: return s + "↑";
                case Keys.Down: return s + "↓";
                case Keys.Left: return s + "←";
                case Keys.Right: return s + "→";
                case Keys.PageUp: return s + "PgUp";
                case Keys.PageDown: return s + "PgDn";
                case Keys.OemMinus: return s + "-";
                case Keys.Oemplus: return s + "=";
                case Keys.OemOpenBrackets: return s + "[";
                case Keys.OemCloseBrackets: return s + "]";
                case Keys.Scroll: return s + "Scroll Lock";
                default: return s + k;
            }
        }
    }
}
