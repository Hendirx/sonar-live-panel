using System;
using System.Collections.Generic;
using System.Text;

namespace LivePanel
{
    static class McuTest
    {
        static int fails;
        static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
        static string Hex(byte[] b) { return BitConverter.ToString(b); }

        static byte[] Lcd(int offset, string text)
        {
            List<byte> m = new List<byte> { 0xF0, 0, 0, 0x66, 0x14, 0x12, (byte)offset };
            m.AddRange(Encoding.ASCII.GetBytes(text));
            m.Add(0xF7);
            return m.ToArray();
        }

        static int Note(int note, int vel) { return 0x90 | (note << 8) | (vel << 16); }

        static int Main()
        {
            long now = 1000;
            List<int> shorts = new List<int>();
            List<byte[]> sysex = new List<byte[]>();
            Mcu m = new Mcu();
            m.Clock = () => now;
            m.SendShort = shorts.Add;
            m.SendSysex = sysex.Add;

            // Handshake
            m.OnSysex(new byte[] { 0xF0, 0, 0, 0x66, 0x14, 0x00, 0xF7 });
            Check(sysex.Count == 1 && Hex(sysex[0]) == "F0-00-00-66-14-01-4C-49-56-45-50-4E-4C-01-02-03-04-F7", "device query reply " + Hex(sysex[0]));
            m.OnSysex(new byte[] { 0xF0, 0, 0, 0x66, 0x14, 0x02, 0x4C, 0x49, 0x56, 0x45, 0x50, 0x4E, 0x4C, 9, 9, 9, 9, 0xF7 });
            Check(Hex(sysex[1]) == "F0-00-00-66-14-03-4C-49-56-45-50-4E-4C-F7", "connection confirm");

            // LCD names: 8 strips x 7 chars on the top row, values on the bottom row
            m.OnSysex(Lcd(0, "Piano  Strings Bass   Pad    Lead   "));
            m.OnSysex(Lcd(56, "Pan    Pan    Pan    Pan    Pan    "));
            Check(m.Name(0) == "Piano" && m.Name(1) == "Strings" && m.Name(4) == "Lead", "names parsed");
            Check(!m.HasTrack(5) && !m.HasTrack(7), "empty strips have no track");

            // Replay of the real Sonar log: names, select popup (full + partial rewrite), restore
            Mcu s = new Mcu();
            s.Clock = () => now;
            s.OnSysex(Lcd(0, "AttcB1 Sfizz1"));
            Check(s.Name(0) == "AttcB1" && s.Name(1) == "Sfizz1" && !s.HasTrack(2), "real Sonar names: two tracks");
            s.Select(1);
            s.OnSysex(Lcd(0, " Track 2: \"sfizz 1\""));
            Check(s.Name(0) == "AttcB1" && s.Name(1) == "Sfizz1" && !s.HasTrack(2), "popup doesn't overwrite names");
            Check(s.FullName(1) == "sfizz 1", "full name learned from popup: " + s.FullName(1));
            s.OnShort(Note(0x19, 127));
            now += 500;
            s.Select(0);
            s.OnSysex(Lcd(7, "1: \"Attack Bass 1\""));   // popup still showing: Sonar only rewrites the changed part
            Check(!s.HasTrack(2), "partial popup rewrite ignored too");
            Check(s.FullName(0) == "Attack Bass 1", "full name from partial popup: " + s.FullName(0));
            s.OnSysex(Lcd(0, "AttcB1 Sfizz1            "));
            Check(s.FullName(1) == "sfizz 1" && s.Name(1) == "Sfizz1", "names restored, full names kept");
            List<int> sent = new List<int>();
            s.SendShort = sent.Add;
            s.Volume(0.02);
            Check(sent.Count == 0, "no volume change before Sonar reports the fader");
            List<byte[]> sx = new List<byte[]>();
            s.SendSysex = sx.Add;
            s.Refresh();
            Check(sx.Count == 2 && Hex(sx[0]) == "F0-00-00-66-14-01-4C-49-56-45-50-4E-4C-01-02-03-04-F7"
                && Hex(sx[1]) == "F0-00-00-66-14-1B-4C-49-56-45-50-4E-4C-F7" && sent.Count == 0,
                "refresh = wake-up then serial number, no button presses");
            s.OnShort(0xE0 | (0x10 << 8) | (0x64 << 16));   // Sonar's forced fader reply
            Check(s.FaderKnown[0] && s.Fader[0] == 12816, "fader known after refresh");
            s.Volume(0.02);
            Check(sent.Count == 3, "volume works after that");
            s.Bank(1);
            Check(s.FaderKnown[0], "bank change keeps values (Sonar only resends differences)");

            // Select sends Select-button press/release
            shorts.Clear();
            m.Select(2);
            Check(shorts.Count == 2 && shorts[0] == Note(0x1A, 127) && shorts[1] == Note(0x1A, 0), "select strip 3 presses note 0x1A");
            Check(m.Selected == 2, "selected optimistically");
            m.OnShort(Note(0x1A, 127));
            shorts.Clear();
            m.Select(2);
            Check(shorts.Count == 0, "re-selecting the lit strip doesn't toggle it off");
            shorts.Clear();
            m.Select(6);
            Check(shorts.Count == 0 && m.Selected == 2, "empty strip ignored");

            // LED feedback moves selection
            m.OnShort(Note(0x1B, 127));
            m.OnShort(Note(0x1A, 0));
            Check(m.Selected == 3, "LED feedback tracks selection");

            // Step next within bank
            shorts.Clear();
            m.Step(1);
            Check(m.Selected == 4 && shorts[0] == Note(0x1C, 127), "next track");
            shorts.Clear();
            m.Step(1);   // strip 5 is empty -> no-op
            Check(shorts.Count == 0 && m.Selected == 4, "next stops at last track");

            // Step past the edge: channel right, then select strip 8 after the LCD refreshes
            m.OnSysex(Lcd(0, "T1     T2     T3     T4     T5     T6     T7     T8     "));
            m.OnShort(Note(0x1C, 0));
            m.OnShort(Note(0x1F, 127));
            Check(m.Selected == 7, "strip 8 selected via LED");
            shorts.Clear();
            now += 1000;
            m.Step(1);
            Check(shorts.Count == 2 && shorts[0] == Note(0x31, 127), "channel right pressed at edge");
            shorts.Clear();
            now += 20; m.OnSysex(Lcd(0, "T2     T3     T4     T5     T6     T7     T8     T9     "));
            now += 20; m.Tick();
            Check(shorts.Count == 0, "waits for LCD to settle");
            now += 70; m.Tick();
            Check(shorts.Count == 2 && shorts[0] == Note(0x1F, 127), "then selects edge strip (T9)");

            // Edge with no more tracks: no LCD change -> pending expires, nothing selected
            shorts.Clear();
            m.OnShort(Note(0x18, 127)); m.OnShort(Note(0x1F, 0));
            now += 1000;
            m.Step(-1);
            Check(shorts.Count == 2 && shorts[0] == Note(0x30, 127), "channel left pressed at left edge");
            shorts.Clear();
            now += 600; m.Tick();
            now += 100; m.Tick();
            Check(shorts.Count == 0, "no select when the list didn't move");

            // Volume: touch, pitch bend, release
            Check(m.Selected == 0, "a scroll that goes nowhere keeps the selection");
            m.OnShort(Note(0x18, 127));
            m.OnShort(0xE0 | 0 | (0 << 8) | (0x40 << 16));   // fader 1 at 8192
            Check(m.Fader[0] == 8192, "fader feedback");
            shorts.Clear();
            m.Volume(0.02);
            int v = 8192 + 328;
            Check(shorts.Count == 3 && shorts[0] == Note(0x68, 127) && shorts[1] == (0xE0 | ((v & 0x7F) << 8) | ((v >> 7) << 16)) && shorts[2] == Note(0x68, 0), "volume up sends touch + pitch bend");

            // Pan: relative V-Pot CC
            shorts.Clear();
            m.Pan(-1); m.Pan(2);
            Check(shorts[0] == (0xB0 | (0x10 << 8) | (0x41 << 16)) && shorts[1] == (0xB0 | (0x10 << 8) | (0x02 << 16)), "pan left/right V-Pot deltas");
            m.OnShort(0xB0 | (0x30 << 8) | (0x11 << 16));    // ring mode 1, pos 1 = hard left
            Check(Math.Abs(m.PanPosition(0) + 1) < 1e-9, "pan ring decoded");

            // Arm on select
            m.ArmOnSelect = true;
            m.OnShort(Note(0x01, 127));                       // strip 2 armed
            shorts.Clear();
            m.Select(3);
            Check(shorts.Contains(Note(0x01, 127)) && shorts.Contains(Note(0x03, 127)), "arm-on-select disarms old, arms new");
            shorts.Clear();
            m.Select(4);                                      // Sonar sent no rec LEDs for strip 4's arm
            Check(shorts.Contains(Note(0x03, 127)) && shorts.Contains(Note(0x04, 127)), "disarms our own earlier arm without LED feedback");
            shorts.Clear();
            m.Select(4);
            Check(shorts.Count == 0, "re-picking the same track doesn't toggle the arm off");

            Check(Mcu.Decibels(12640) == "-0.7 dB", "dB matches Sonar's readout: " + Mcu.Decibels(12640));
            Check(Mcu.Decibels(16383) == "+6.0 dB" && Mcu.Decibels(0) == "-inf dB", "dB ends: " + Mcu.Decibels(16383));
            Console.WriteLine("   fader 12816 (track 1) -> " + Mcu.Decibels(12816));

            // Auto-retry refresh while no tracks are known, and stop once they arrive
            Mcu r = new Mcu();
            List<byte[]> rx = new List<byte[]>();
            r.Clock = () => now;
            r.SendSysex = rx.Add;
            int learned = 0;
            r.NameLearned += () => learned++;
            r.Tick();
            Check(rx.Count == 2, "refreshes when no tracks");
            now += 1000; r.Tick();
            Check(rx.Count == 2, "waits 3 s between retries");
            now += 2500; r.Tick();
            Check(rx.Count == 4, "retries after 3 s");
            r.OnSysex(Lcd(0, "AttcB1 Sfizz1"));
            now += 5000; r.Tick();
            Check(rx.Count == 4, "stops retrying once tracks arrive");
            r.Select(1);
            r.OnSysex(Lcd(0, " Track 2: \"sfizz 1\""));
            Check(learned == 1 && r.FullNames["Sfizz1"] == "sfizz 1", "raises NameLearned for saving");

            // Pinned track: skipped by number keys and next/prev, has its own volume
            Mcu q = new Mcu();
            List<int> qs = new List<int>();
            q.Clock = () => now;
            q.SendShort = qs.Add;
            q.OnSysex(Lcd(0, "Vocal  Piano  Bass   Pad"));
            q.PinnedName = "Vocal";
            Check(q.PlayableStrip(0) == 1 && q.PlayableStrip(2) == 3 && q.PlayableStrip(3) == -1, "keys map past the pinned track");
            q.SelectPlayable(0);
            Check(q.Selected == 1 && qs[0] == Note(0x19, 127), "key 1 = first unpinned track");
            q.Step(-1);
            Check(q.Selected == 1, "prev doesn't land on the pinned track at the left edge");
            q.Step(1); q.Step(1);
            Check(q.Selected == 3, "next walks the unpinned tracks");
            q.OnShort(0xE0 | (0x00 << 8) | (0x60 << 16));   // pinned (strip 1) fader known
            qs.Clear();
            q.PinnedVolume(0.02);
            Check(qs.Count == 3 && (qs[1] & 0xFF) == 0xE0, "pinned volume moves strip 1's fader");
            q.PinnedName = null;
            Check(q.PlayableStrip(0) == 0, "unpinning restores normal keys");

            Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED");
            return fails;
        }
    }
}
