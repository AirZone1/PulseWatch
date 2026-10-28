using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NvAPIWrapper;
using NvAPIWrapper.DRS;
using NvAPIWrapper.Native;

namespace PulseWatch.Core
{
    public sealed class NvOption
    {
        public uint Value { get; init; }
        public string Label { get; init; } = "";
        public override string ToString() => Label;
    }

    public sealed class NvSettingDef
    {
        public uint Id { get; init; }
        public string Title { get; init; } = "";
        public string Help { get; init; } = "";
        public string Category { get; init; } = "";
        public NvOption[] Options { get; init; } = Array.Empty<NvOption>();
        /// <summary>For numeric settings (e.g. frame-rate cap) instead of a list.</summary>
        public (uint min, uint max)? Range { get; init; }
        public string RangeUnit { get; init; } = "";
        public string ZeroLabel { get; init; } = "";

        // Live
        public uint Current { get; set; }
        public uint Default { get; set; }
        public bool IsSet { get; set; }
        public bool Available { get; set; } = true;
    }

    public sealed class NvDisplay
    {
        public string DeviceName { get; init; } = "";
        public string Title { get; init; } = "";
        public int Vibrance { get; set; }
        public int VibranceMin { get; init; }
        public int VibranceMax { get; init; }
        public int VibranceDefault { get; init; }
    }

    /// <summary>NVIDIA Control Panel global (base profile) driver settings via the documented NvAPI DRS interface.</summary>
    public static class NvidiaSettings
    {
        private static bool _init;
        private static readonly object Gate = new();

        private static NvOption O(uint v, string he, string en) => new() { Value = v, Label = L.T(he, en) };

        /// <summary>Rebuilt when the UI language changes (values are re-read by <see cref="Load"/>).</summary>
        public static NvSettingDef[] Defs { get; private set; } = Build();

        public static void Relocalize() => Defs = Build();

        private static NvSettingDef[] Build()
        {
            string perf = L.T("ביצועים", "Performance"), disp = L.T("תצוגה", "Display"), img = L.T("איכות תמונה", "Image quality"), adv = L.T("מתקדם", "Advanced");
            string off = L.T("כבוי", "Off"), on = L.T("מופעל", "On"), app = L.T("לפי האפליקציה", "Application-controlled");
            return new NvSettingDef[]
            {
                new() { Id = 0x1057EB71, Category = perf, Title = L.T("מצב ניהול צריכת חשמל", "Power management mode"),
                    Help = L.T("\"ביצועים מרביים\" מחזיק את השעונים גבוהים גם בעומס נמוך — פחות גמגומים, יותר חום וצריכה.", "\"Prefer maximum performance\" keeps clocks high even at light load — fewer stutters, more heat and power."),
                    Options = new[] { O(5, "נורמלי (ברירת מחדל)", "Normal (default)"), O(1, "העדף ביצועים מרביים", "Prefer maximum performance"), O(0, "אדפטיבי", "Adaptive"), O(3, "העדף ביצועים עקביים", "Prefer consistent performance") } },
                new() { Id = 0x007BA09E, Category = perf, Title = L.T("מצב השהיה נמוכה (Low Latency)", "Low Latency Mode"),
                    Help = L.T("מגביל את תור הפריימים של המעבד ל-1. מוריד Input Lag במשחקים שלא תומכים ב-Reflex.", "Limits the CPU render queue to 1 frame. Lowers input lag in games without Reflex."),
                    Options = new[] { O(0, "כבוי / לפי האפליקציה", "Off / application"), new NvOption { Value = 1, Label = on } } },
                new() { Id = 0x10835002, Category = perf, Title = L.T("הגבלת קצב פריימים (Max FPS)", "Max Frame Rate"),
                    Help = L.T("מגביל FPS גלובלית ברמת הדרייבר. מומלץ: 3 פחות מקצב הרענון של המסך עם G-SYNC.", "Global driver-level FPS cap. With G-SYNC, use refresh rate minus 3."),
                    Range = (0, 1000), RangeUnit = "FPS", ZeroLabel = L.T("ללא הגבלה", "Unlimited") },
                new() { Id = 0x00A879CF, Category = disp, Title = L.T("סנכרון אנכי (V-Sync)", "Vertical sync"),
                    Help = L.T("\"מהיר\" (Fast Sync) מונע קריעה בלי להגביל FPS. עם G-SYNC מומלץ \"מופעל\" + הגבלת FPS.", "Fast Sync removes tearing without capping FPS. With G-SYNC use On + an FPS cap."),
                    Options = new[] { new NvOption { Value = 0x60925292, Label = app }, new NvOption { Value = 0x08416747, Label = off }, new NvOption { Value = 0x47814940, Label = on }, O(0x18888888, "מהיר (Fast Sync)", "Fast") } },
                new() { Id = 0x0064B541, Category = disp, Title = L.T("קצב רענון מועדף", "Preferred refresh rate"),
                    Help = L.T("\"הגבוה ביותר\" מכריח משחקים במסך מלא לרוץ בקצב הרענון המקסימלי של המסך.", "\"Highest available\" forces full-screen games to the monitor's maximum refresh rate."),
                    Options = new[] { new NvOption { Value = 0, Label = app }, O(1, "הגבוה ביותר הזמין", "Highest available") } },
                new() { Id = 0x00CE2691, Category = img, Title = L.T("סינון טקסטורות — איכות", "Texture filtering - Quality"),
                    Help = L.T("\"ביצועים גבוהים\" מוסיף מעט FPS במחיר חדות הטקסטורות.", "\"High performance\" adds a little FPS at the cost of texture sharpness."),
                    Options = new[] { O(0xFFFFFFF6, "איכות גבוהה", "High quality"), O(0, "איכות (ברירת מחדל)", "Quality (default)"), O(10, "ביצועים", "Performance"), O(20, "ביצועים גבוהים", "High performance") } },
                new() { Id = 0x10D2BB16, Category = img, Title = L.T("סינון אנאיזוטרופי — מצב", "Anisotropic filtering - Mode"),
                    Help = L.T("\"ידני\" מפעיל את הרמה שנבחרה למטה בכל המשחקים.", "\"User-defined\" forces the level below in every game."),
                    Options = new[] { new NvOption { Value = 0, Label = app }, O(1, "ידני (שליטה גלובלית)", "User-defined (global)") } },
                new() { Id = 0x101E61A9, Category = img, Title = L.T("סינון אנאיזוטרופי — רמה", "Anisotropic filtering - Level"),
                    Help = L.T("פועל רק כשהמצב למעלה הוא \"ידני\". 16x כמעט ללא עלות ביצועים בכרטיסים מודרניים.", "Only used when the mode above is user-defined. 16x is nearly free on modern cards."),
                    Options = new[] { new NvOption { Value = 1, Label = off }, O(2, "2x", "2x"), O(4, "4x", "4x"), O(8, "8x", "8x"), O(16, "16x", "16x") } },
                new() { Id = 0x1074C972, Category = img, Title = "FXAA",
                    Help = L.T("החלקת קצוות זולה ברמת הדרייבר. מטשטשת מעט את התמונה.", "Cheap driver-level anti-aliasing. Slightly softens the image."),
                    Options = new[] { new NvOption { Value = 0, Label = off }, new NvOption { Value = 1, Label = on } } },
                new() { Id = 0x20C1221E, Category = adv, Title = L.T("אופטימיזציה מרובת תהליכונים (OpenGL)", "Threaded optimization (OpenGL)"),
                    Help = L.T("משפיע בעיקר על משחקי OpenGL ישנים.", "Mainly affects older OpenGL games."),
                    Options = new[] { O(0, "אוטומטי", "Auto"), new NvOption { Value = 1, Label = on }, new NvOption { Value = 2, Label = off } } },
                new() { Id = 0x00198FFF, Category = adv, Title = "Shader Cache",
                    Help = L.T("שומר Shaders מקומפלים בדיסק — טעינות מהירות יותר ופחות גמגומים.", "Keeps compiled shaders on disk — faster loads and fewer stutters."),
                    Options = new[] { new NvOption { Value = 1, Label = on }, new NvOption { Value = 0, Label = off } } },
            };
        }

        private static void EnsureInit()
        {
            if (_init) return;
            NVIDIA.Initialize();
            _init = true;
        }

        public static string DriverVersion
        {
            get
            {
                try { lock (Gate) { EnsureInit(); return $"{NVIDIA.DriverVersion / 100}.{NVIDIA.DriverVersion % 100:00}"; } }
                catch { return "?"; }
            }
        }

        public static void Load()
        {
            lock (Gate)
            {
                EnsureInit();
                using var session = DriverSettingsSession.CreateAndLoad();
                var profile = session.CurrentGlobalProfile;
                foreach (var d in Defs)
                {
                    try
                    {
                        var vals = DRSApi.EnumAvailableSettingValues(d.Id);
                        d.Default = vals.DefaultValueAsInteger();
                        d.Available = true;
                    }
                    catch { d.Available = false; continue; }
                    try
                    {
                        var s = profile.GetSetting(d.Id);
                        d.Current = Convert.ToUInt32(s.CurrentValue);
                        d.IsSet = true;
                    }
                    catch
                    {
                        d.Current = d.Default;
                        d.IsSet = false;
                    }
                }
            }
        }

        private static void Backup(DriverSettingsSession session)
        {
            string path = Path.Combine(AppPaths.DataDir, "nvidia-settings-backup.nvidia");
            if (File.Exists(path)) return;
            try { session.Save(path); Log.Info("NVIDIA settings backup written to " + path); }
            catch (Exception ex) { Log.Warn("NVIDIA backup failed: " + ex.Message); }
        }

        public static void Set(NvSettingDef def, uint value)
        {
            lock (Gate)
            {
                EnsureInit();
                using var session = DriverSettingsSession.CreateAndLoad();
                Backup(session);
                session.CurrentGlobalProfile.SetSetting(def.Id, value);
                session.Save();
                def.Current = value;
                def.IsSet = true;
                Log.Info($"NVIDIA setting 0x{def.Id:X8} = 0x{value:X}");
            }
        }

        public static void Reset(NvSettingDef def)
        {
            lock (Gate)
            {
                EnsureInit();
                using var session = DriverSettingsSession.CreateAndLoad();
                Backup(session);
                try { session.CurrentGlobalProfile.RestoreSettingToDefault(def.Id); }
                catch { try { session.CurrentGlobalProfile.DeleteSetting(def.Id); } catch { } }
                session.Save();
                def.Current = def.Default;
                def.IsSet = false;
            }
        }

        public static void ResetAll()
        {
            foreach (var d in Defs.Where(d => d.Available)) Reset(d);
        }

        // ─────────────── Digital vibrance ───────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string? device, uint devNum, ref DISPLAY_DEVICE dd, uint flags);

        private static string MonitorName(string adapterDevice)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            return EnumDisplayDevices(adapterDevice, 0, ref dd, 0) ? dd.DeviceString : "";
        }

        public static List<NvDisplay> Displays()
        {
            var list = new List<NvDisplay>();
            lock (Gate)
            {
                EnsureInit();
                var screens = System.Windows.Forms.Screen.AllScreens;
                int i = 0;
                foreach (var d in NvAPIWrapper.Display.Display.GetDisplays())
                {
                    i++;
                    var dvc = d.DigitalVibranceControl;
                    var scr = screens.FirstOrDefault(s => string.Equals(s.DeviceName, d.Name, StringComparison.OrdinalIgnoreCase));
                    string mon = MonitorName(d.Name);
                    string res = scr != null ? $"{scr.Bounds.Width}×{scr.Bounds.Height}{(scr.Primary ? L.T(" · ראשי", " · primary") : "")}" : "";
                    list.Add(new NvDisplay
                    {
                        DeviceName = d.Name,
                        Title = L.T($"מסך {i}", $"Display {i}") + (string.IsNullOrWhiteSpace(mon) ? "" : $" · {mon}") + (res.Length > 0 ? $"  ({res})" : ""),
                        Vibrance = dvc.CurrentLevel,
                        VibranceMin = dvc.MinimumLevel,
                        VibranceMax = dvc.MaximumLevel,
                        VibranceDefault = dvc.DefaultLevel
                    });
                }
            }
            return list;
        }

        public static void SetVibrance(string deviceName, int level)
        {
            lock (Gate)
            {
                EnsureInit();
                var d = NvAPIWrapper.Display.Display.GetDisplays().First(x => x.Name == deviceName);
                d.DigitalVibranceControl.CurrentLevel = level;
            }
        }
    }
}
