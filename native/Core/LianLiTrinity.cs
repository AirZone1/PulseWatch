using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HidSharp;

namespace PulseWatch.Core
{
    /// <summary>
    /// Lian Li Galahad II Trinity AIO (USB 0416:7371 Performance / 0416:7373 Regular).
    /// Protocol: 64-byte HID output reports, report id 0x01, 6-byte header [id, cmd, 0, 0, 0, len].
    /// Command map follows the MIT-licensed lian-li-linux project (github.com/sgtaziz/lian-li-linux, fork github.com/ccmalcom/lian-li-linux),
    /// and was checked against live captures from this machine (0x81 status → fan RPM @6-7, pump RPM @8-9, big endian).
    /// The device has no coolant temperature sensor.
    /// </summary>
    public sealed class LianLiTrinity : IDisposable
    {
        private const byte ReportId = 0x01;
        private const byte CmdHandshake = 0x81, CmdFirmware = 0x86, CmdPumpPwm = 0x8A, CmdFanPwm = 0x8B, CmdPumpLight = 0x83, CmdFanLight = 0x85;

        public static (int Mode, string Name)[] Modes => new[]
        {
            (3, L.T("סטטי", "Static")), (4, L.T("נשימה", "Breathing")), (1, L.T("קשת", "Rainbow")), (2, L.T("קשת מתחלפת", "Rainbow morph")),
            (5, L.T("מסלול", "Runway")), (6, L.T("מטאור", "Meteor")), (7, L.T("מערבולת", "Vortex")), (8, L.T("הצלבה", "Crossing over")),
            (9, L.T("טאי-צ'י", "Tai chi")), (10, L.T("לילה צבעוני", "Colorful starry night")), (11, L.T("לילה כוכבים", "Static starry night")),
            (13, L.T("ביג-בנג", "Big bang")), (14, L.T("פעימת משאבה", "Pump")), (15, L.T("צבעים מתחלפים", "Colors morph")),
            (16, L.T("קפיצה", "Bounce")), (12, L.T("קול", "Voice"))
        };

        private readonly HidStream _stream;
        public string Model { get; }
        public string Firmware { get; private set; } = "";
        public int PumpMinPct { get; }
        public int FanMinPct => 10;
        public int? FanRpm { get; private set; }
        public int? PumpRpm { get; private set; }
        public DateTime LastStatus { get; private set; }
        public int? LastFanPct { get; private set; }
        public int? LastPumpPct { get; private set; }

        private LianLiTrinity(HidStream stream, int pid)
        {
            _stream = stream;
            Model = pid == 0x7371 ? "Galahad II Trinity Performance" : "Galahad II Trinity";
            PumpMinPct = 30;
            _stream.ReadTimeout = 250;
            _stream.WriteTimeout = 500;
        }

        public static LianLiTrinity? TryOpen()
        {
            try
            {
                var dev = DeviceList.Local.GetHidDevices(0x0416)
                    .Where(d => d.ProductID == 0x7373 || d.ProductID == 0x7371)
                    .FirstOrDefault(d => d.GetMaxOutputReportLength() == 64);
                if (dev == null) return null;
                var cfg = new OpenConfiguration();
                cfg.SetOption(OpenOption.Exclusive, false);
                cfg.SetOption(OpenOption.Interruptible, true);
                if (!dev.TryOpen(cfg, out HidStream stream)) return null;
                var t = new LianLiTrinity(stream, dev.ProductID);
                t.ReadFirmware();
                t.Poll();
                Log.Info($"Lian Li {t.Model} opened, fw '{t.Firmware}', fan {t.FanRpm} rpm, pump {t.PumpRpm} rpm");
                return t;
            }
            catch (Exception ex)
            {
                Log.Warn("Lian Li open failed: " + ex.Message);
                return null;
            }
        }

        private void Write(byte cmd, params byte[] payload)
        {
            var pkt = new byte[64];
            pkt[0] = ReportId;
            pkt[1] = cmd;
            pkt[5] = (byte)payload.Length;
            Array.Copy(payload, 0, pkt, 6, payload.Length);
            _stream.Write(pkt);
        }

        private byte[]? ReadFor(byte cmd, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            var buf = new byte[64];
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    int n = _stream.Read(buf, 0, buf.Length);
                    if (n >= 10 && buf[1] == cmd) return buf;
                }
                catch (TimeoutException) { return null; }
            }
            return null;
        }

        private void ReadFirmware()
        {
            try
            {
                Write(CmdFirmware);
                var r = ReadFor(CmdFirmware, 800);
                if (r != null)
                {
                    int len = Math.Min((int)r[5], 58);
                    Firmware = System.Text.Encoding.ASCII.GetString(r, 6, len).TrimEnd('\0', ' ');
                    ReadFor(CmdFirmware, 200); // second packet carries the build date
                }
            }
            catch (Exception ex) { Log.Warn("Lian Li firmware read: " + ex.Message); }
        }

        /// <summary>Requests a status packet and updates fan/pump RPM.</summary>
        public bool Poll()
        {
            try
            {
                Write(CmdHandshake);
                var r = ReadFor(CmdHandshake, 300);
                if (r == null) return false;
                FanRpm = (r[6] << 8) | r[7];
                PumpRpm = (r[8] << 8) | r[9];
                LastStatus = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Lian Li poll: " + ex.Message);
                return false;
            }
        }

        public void SetFanPct(int pct, bool mbSync = false)
        {
            pct = Math.Clamp(pct, FanMinPct, 100);
            Write(CmdFanPwm, (byte)(mbSync ? 1 : 0), (byte)pct);
            LastFanPct = mbSync ? null : pct;
        }

        public void SetPumpPct(int pct, bool mbSync = false)
        {
            pct = Math.Clamp(pct, PumpMinPct, 100);
            Write(CmdPumpPwm, (byte)(mbSync ? 1 : 0), (byte)pct);
            LastPumpPct = mbSync ? null : pct;
        }

        private static byte[] Rgb(IList<string> colors, int count)
        {
            var bytes = new byte[count * 3];
            for (int i = 0; i < count; i++)
            {
                string hex = i < colors.Count ? colors[i] : "#FFFFFF";
                var c = ParseColor(hex);
                bytes[i * 3] = c.r; bytes[i * 3 + 1] = c.g; bytes[i * 3 + 2] = c.b;
            }
            return bytes;
        }

        public static (byte r, byte g, byte b) ParseColor(string hex)
        {
            try
            {
                hex = hex.TrimStart('#');
                if (hex.Length == 8) hex = hex[2..];
                return (Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
            }
            catch { return (255, 255, 255); }
        }

        /// <param name="mbSource">true = take ARGB from the motherboard header instead of the AIO's own controller.</param>
        public void SetPumpLight(LightEffect e, bool mbSource)
        {
            var p = new byte[19];
            p[0] = (byte)Math.Clamp(e.Scope, 0, 2);
            p[1] = (byte)e.Mode;
            p[2] = (byte)Math.Clamp(e.Brightness, 0, 4);
            p[3] = (byte)Math.Clamp(e.Speed, 0, 4);
            Array.Copy(Rgb(e.Colors, 4), 0, p, 4, 12);
            p[16] = (byte)Math.Clamp(e.Direction, 0, 5);
            p[17] = (byte)(e.Off ? 1 : 0);
            p[18] = (byte)(mbSource ? 1 : 0);
            Write(CmdPumpLight, p);
        }

        public void SetFanLight(LightEffect e, bool mbSource, bool syncToPump)
        {
            var p = new byte[20];
            p[0] = (byte)e.Mode;
            p[1] = (byte)Math.Clamp(e.Brightness, 0, 4);
            p[2] = (byte)Math.Clamp(e.Speed, 0, 4);
            Array.Copy(Rgb(e.Colors, 4), 0, p, 3, 12);
            p[15] = (byte)Math.Clamp(e.Direction, 0, 5);
            p[16] = (byte)(e.Off ? 1 : 0);
            p[17] = (byte)(mbSource ? 1 : 0);
            p[18] = (byte)(syncToPump ? 1 : 0);
            p[19] = 24;
            Write(CmdFanLight, p);
        }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { }
        }
    }
}
