using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PulseWatch.Core
{
    public enum FanMode { Auto, Fixed, Curve }

    public class CurvePoint
    {
        public double T { get; set; }
        public double P { get; set; }
        public CurvePoint() { }
        public CurvePoint(double t, double p) { T = t; P = p; }
    }

    public class FanCurve
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string Name { get; set; } = "Curve";
        /// <summary>Sensor ids or aliases ("@cpu.temp"). The curve follows the hottest of them.</summary>
        public List<string> Sources { get; set; } = new() { "@cpu.temp" };
        public List<CurvePoint> Points { get; set; } = new();
        /// <summary>Temperature must drop this much below the last used value before the fan slows down.</summary>
        public double Hysteresis { get; set; } = 3;
        /// <summary>Max % change per second when speeding up / slowing down (smooths noise spikes).</summary>
        public double StepUpPerSec { get; set; } = 8;
        public double StepDownPerSec { get; set; } = 3;

        public double Evaluate(double temp)
        {
            if (Points.Count == 0) return 100;
            var pts = Points.OrderBy(p => p.T).ToList();
            if (temp <= pts[0].T) return pts[0].P;
            if (temp >= pts[^1].T) return pts[^1].P;
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var a = pts[i];
                var b = pts[i + 1];
                if (temp >= a.T && temp <= b.T)
                {
                    double f = (b.T - a.T) < 0.001 ? 1 : (temp - a.T) / (b.T - a.T);
                    return a.P + (b.P - a.P) * f;
                }
            }
            return pts[^1].P;
        }

        public override string ToString() => Defaults.DisplayName(Id, Name);

        public FanCurve Clone() => new()
        {
            Id = Id, Name = Name, Sources = new List<string>(Sources), Hysteresis = Hysteresis,
            StepUpPerSec = StepUpPerSec, StepDownPerSec = StepDownPerSec,
            Points = Points.Select(p => new CurvePoint(p.T, p.P)).ToList()
        };
    }

    public class ChannelAssignment
    {
        public FanMode Mode { get; set; } = FanMode.Auto;
        public double FixedPct { get; set; } = 50;
        public string? CurveId { get; set; }
        public ChannelAssignment Clone() => new() { Mode = Mode, FixedPct = FixedPct, CurveId = CurveId };
    }

    public class CoolingProfile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public Dictionary<string, ChannelAssignment> Channels { get; set; } = new();
        [JsonIgnore] public string DisplayName => Defaults.DisplayName(Id, Name);
    }

    public class GpuTune
    {
        public int PowerLimitW { get; set; }
        /// <summary>Offsets in MSI Afterburner units.</summary>
        public int CoreOffsetMhz { get; set; }
        public int MemOffsetMhz { get; set; }
        public int TempLimitC { get; set; }
        /// <summary>Per-point V/F curve offsets (point index → MHz). Null/empty = stock curve.</summary>
        public Dictionary<int, int>? CurveDeltas { get; set; }
        /// <summary>
        /// Voltage cap (mV) of a flat-topped curve (undervolt / underclock), or null. The stock curve shifts by 15 MHz
        /// steps with temperature, and not by the same amount at every voltage, so fixed offsets alone let the flat
        /// part creep above the cap point; the hub re-flattens it from the live curve (<see cref="SensorHub"/>).
        /// </summary>
        public double? CapMv { get; set; }

        public bool HasCurve => CurveDeltas != null && CurveDeltas.Values.Any(v => v != 0);

        public GpuTune Clone()
        {
            var c = (GpuTune)MemberwiseClone();
            c.CurveDeltas = CurveDeltas == null ? null : new Dictionary<int, int>(CurveDeltas);
            return c;
        }
    }

    public class GpuConfig
    {
        public GpuTune? Applied { get; set; }
        public GpuTune?[] Slots { get; set; } = new GpuTune?[5];
        public bool ApplyOnStartup { get; set; }
        public int? DefaultPowerLimitW { get; set; }
        public int? DefaultTempLimitC { get; set; }
    }

    public class LightEffect
    {
        public int Mode { get; set; } = 3;          // Galahad II mode byte (3 = static)
        public int Brightness { get; set; } = 4;    // 0..4
        public int Speed { get; set; } = 2;         // 0..4
        public int Direction { get; set; } = 0;     // 0..5
        public int Scope { get; set; } = 2;         // pump only: 0 inner, 1 outer, 2 all
        public List<string> Colors { get; set; } = new() { "#00C8FF", "#7C3AED", "#FF2D95", "#FFFFFF" };
        public bool Off { get; set; }
        public LightEffect Clone() => new() { Mode = Mode, Brightness = Brightness, Speed = Speed, Direction = Direction, Scope = Scope, Colors = new List<string>(Colors), Off = Off };
    }

    public class LightingConfig
    {
        /// <summary>"unchanged" = never touch lighting, "custom" = Pump/Fans effects, "thermal" = color follows temperature, "mbsync" = motherboard ARGB.</summary>
        public string Mode { get; set; } = "unchanged";
        public LightEffect Pump { get; set; } = new();
        public LightEffect Fans { get; set; } = new();
        public bool SyncFansToPump { get; set; } = true;
        public string ThermalSource { get; set; } = "@cpu.temp";
        public double ThermalCold { get; set; } = 40;
        public double ThermalHot { get; set; } = 80;
    }

    /// <summary>Windows notification when a temperature stays above its limit (once, until it drops 5 °C below).</summary>
    public class AlertConfig
    {
        public bool Enabled { get; set; } = true;
        public double CpuC { get; set; } = 90;
        public double GpuC { get; set; } = 83;
        public double HotspotC { get; set; } = 100;
        public double VramC { get; set; } = 100;
        public double NvmeC { get; set; } = 70;
    }

    /// <summary>Always-on-top overlay with live values (not shown over exclusive-fullscreen games).</summary>
    public class OsdConfig
    {
        public bool Enabled { get; set; }
        /// <summary>"tl", "tc", "tr", "bl", "bc" or "br".</summary>
        public string Corner { get; set; } = "tl";
        /// <summary>"rows" (one line per part) or "line" (everything on one line).</summary>
        public string Layout { get; set; } = "rows";
        /// <summary>Font size: 10 small, 12 normal, 15 large.</summary>
        public double Size { get; set; } = 12;
        /// <summary>Windows display device name ("\\.\DISPLAY2"); empty = the main screen.</summary>
        public string Screen { get; set; } = "";
        /// <summary>FPS of the program in the foreground (DirectX games), measured through Windows event tracing.</summary>
        public bool ShowFps { get; set; } = true;
        /// <summary>Whose FPS: "fg" = the program in the foreground, or a display device name = the program drawing most on that screen.</summary>
        public string FpsSource { get; set; } = "fg";
        /// <summary>Values shown, by <see cref="PulseWatch.Views.OsdWindow.Items"/> key, in table order.</summary>
        public List<string> Items { get; set; } = new() { "cpu.temp", "cpu.load", "cpu.power", "gpu.temp", "gpu.load", "gpu.power", "gpu.clock", "gpu.hotspot", "gpu.memtemp" };
        public bool ShowCpu { get; set; } = true;
        public bool ShowGpu { get; set; } = true;
        public bool ShowGpuDetail { get; set; } = true;
        public bool ShowRam { get; set; }
        public double Opacity { get; set; } = 0.85;
    }

    /// <summary>While <see cref="Exe"/> runs: switch to a cooling profile and/or a GPU OC slot; restored when it exits.</summary>
    public class GameRule
    {
        /// <summary>Process name without ".exe", compared case-insensitively.</summary>
        public string Exe { get; set; } = "";
        public string? ProfileId { get; set; }
        /// <summary>1–5, or null to leave the GPU alone.</summary>
        public int? GpuSlot { get; set; }
    }

    public class AppConfig
    {
        /// <summary>"he" or "en".</summary>
        public string Language { get; set; } = "he";
        /// <summary>Bumped when stored units change. v2: GPU memory offset stored in Afterburner units.</summary>
        public int SchemaVersion { get; set; } = 1;
        public int PollMs { get; set; } = 1000;
        public bool MinimizeToTray { get; set; } = true;
        public bool StartMinimized { get; set; }
        public bool CloseConflictsOnLaunch { get; set; }
        public double SafetyCpuC { get; set; } = 90;
        public double SafetyGpuC { get; set; } = 87;
        public double LianLiExitFanPct { get; set; } = 60;
        /// <summary>How often spinning (SATA/HDD) drives are read. Their temperature changes over minutes and a SMART read is slow.</summary>
        public int HddPollSeconds { get; set; } = 300;
        /// <summary>Never read a drive Windows reports as spun down (reading SMART would wake it).</summary>
        public bool HddKeepAsleep { get; set; } = true;
        /// <summary>How often NVMe drives are read while the window is open (in the tray at least every 10 s).</summary>
        public int NvmePollSeconds { get; set; } = 5;

        public AlertConfig Alerts { get; set; } = new();
        public OsdConfig Osd { get; set; } = new();
        public bool HotkeysEnabled { get; set; } = true;
        public bool GameRulesEnabled { get; set; } = true;
        public List<GameRule> GameRules { get; set; } = new();

        public string ActiveProfileId { get; set; } = "balanced";
        public List<CoolingProfile> Profiles { get; set; } = new();
        public List<FanCurve> Curves { get; set; } = new();
        public Dictionary<string, string> ChannelLabels { get; set; } = new();
        public List<string> HiddenChannels { get; set; } = new();
        public Dictionary<string, string> SensorLabels { get; set; } = new();
        public List<string> GraphSensors { get; set; } = new() { "@cpu.temp", "@gpu.temp", "@gpu.hotspot", "@gpu.memtemp" };
        public Dictionary<string, string> ConflictAppPaths { get; set; } = new();

        /// <summary>Digital vibrance per display device name, re-applied at startup.</summary>
        public Dictionary<string, int> Vibrance { get; set; } = new();

        public GpuConfig Gpu { get; set; } = new();
        public LightingConfig Lighting { get; set; } = new();

        [JsonIgnore]
        public CoolingProfile ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles.First();

        public FanCurve? FindCurve(string? id) => id == null ? null : Curves.FirstOrDefault(c => c.Id == id);
    }

    public static class ConfigStore
    {
        private static readonly object Gate = new();
        private static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static AppConfig Load()
        {
            AppConfig cfg;
            try
            {
                cfg = File.Exists(AppPaths.ConfigFile)
                    ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigFile), Opts) ?? new AppConfig()
                    : new AppConfig();
            }
            catch (Exception ex)
            {
                Log.Error("Config load failed, using defaults", ex);
                try { File.Copy(AppPaths.ConfigFile, AppPaths.ConfigFile + ".broken", true); } catch { }
                cfg = new AppConfig();
            }
            if (cfg.SchemaVersion < 2)
            {
                // v1 stored the memory offset in NVML units, which are 2× MSI Afterburner's
                foreach (var t in cfg.Gpu.Slots.Append(cfg.Gpu.Applied))
                    if (t != null) t.MemOffsetMhz /= 2;
                cfg.SchemaVersion = 2;
            }
            Defaults.EnsureBase(cfg);
            L.Set(cfg.Language);
            return cfg;
        }

        public static void Save(AppConfig cfg)
        {
            try
            {
                lock (Gate)
                {
                    string tmp = AppPaths.ConfigFile + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Opts));
                    File.Move(tmp, AppPaths.ConfigFile, true);
                }
            }
            catch (Exception ex) { Log.Error("Config save failed", ex); }
        }
    }

    /// <summary>Factory defaults: curves and the four GCC-style profiles.</summary>
    public static class Defaults
    {
        public const string Silent = "silent", Balanced = "balanced", Performance = "performance", Max = "max";

        public static void EnsureBase(AppConfig cfg)
        {
            void AddCurve(string id, string name, string src, params (double t, double p)[] pts)
            {
                if (cfg.Curves.Any(c => c.Id == id)) return;
                cfg.Curves.Add(new FanCurve { Id = id, Name = name, Sources = new() { src }, Points = pts.Select(p => new CurvePoint(p.t, p.p)).ToList() });
            }

            AddCurve("cpu-silent", "CPU · שקט", "@cpu.temp", (40, 25), (60, 35), (72, 50), (82, 75), (90, 100));
            AddCurve("cpu-balanced", "CPU · מאוזן", "@cpu.temp", (40, 30), (55, 40), (70, 60), (80, 85), (88, 100));
            AddCurve("cpu-performance", "CPU · ביצועים", "@cpu.temp", (40, 45), (55, 60), (68, 80), (78, 100));
            AddCurve("gpu-silent", "GPU · שקט", "@gpu.temp", (45, 0), (55, 32), (68, 50), (78, 75), (84, 100));
            AddCurve("gpu-balanced", "GPU · מאוזן", "@gpu.temp", (40, 0), (50, 35), (65, 55), (75, 80), (82, 100));
            AddCurve("gpu-performance", "GPU · ביצועים", "@gpu.temp", (35, 40), (50, 55), (62, 75), (72, 100));
            // System (case) fans: CPU and GPU both dump heat into the case, the hotter one wins
            AddCurve("case-silent", "מאווררי מערכת · שקט", "@cpu.temp", (45, 25), (65, 35), (78, 55), (88, 100));
            AddCurve("case-balanced", "מאווררי מערכת · מאוזן", "@cpu.temp", (40, 30), (60, 45), (75, 70), (85, 100));
            AddCurve("case-performance", "מאווררי מערכת · ביצועים", "@cpu.temp", (40, 40), (55, 55), (70, 80), (80, 100));
            AddCurve("nvme-balanced", "NVMe · מאוזן", "@nvme.temp", (40, 25), (50, 35), (60, 55), (68, 80), (74, 100));
            AddCurve("nvme-performance", "NVMe · ביצועים", "@nvme.temp", (35, 40), (48, 60), (58, 85), (65, 100));
            foreach (var c in cfg.Curves.Where(c => c.Id.StartsWith("case-")))
            {
                if (!c.Sources.Contains("@gpu.temp")) c.Sources.Add("@gpu.temp");
                if (c.Name is "מארז · CPU+GPU" or "Case · CPU+GPU") c.Name = "מאווררי מערכת · מאוזן"; // renamed built-in
            }

            void AddProfile(string id, string name)
            {
                if (!cfg.Profiles.Any(p => p.Id == id)) cfg.Profiles.Add(new CoolingProfile { Id = id, Name = name });
            }
            AddProfile(Silent, "שקט");
            AddProfile(Balanced, "מאוזן");
            AddProfile(Performance, "ביצועים");
            AddProfile(Max, "מקסימום");
            if (cfg.Profiles.All(p => p.Id != cfg.ActiveProfileId)) cfg.ActiveProfileId = Balanced;
        }

        private static readonly Dictionary<string, (string he, string en)> BuiltInNames = new()
        {
            ["cpu-silent"] = ("CPU · שקט", "CPU · Silent"),
            ["cpu-balanced"] = ("CPU · מאוזן", "CPU · Balanced"),
            ["cpu-performance"] = ("CPU · ביצועים", "CPU · Performance"),
            ["gpu-silent"] = ("GPU · שקט", "GPU · Silent"),
            ["gpu-balanced"] = ("GPU · מאוזן", "GPU · Balanced"),
            ["gpu-performance"] = ("GPU · ביצועים", "GPU · Performance"),
            ["case-silent"] = ("מאווררי מערכת · שקט", "System fans · Silent"),
            ["case-balanced"] = ("מאווררי מערכת · מאוזן", "System fans · Balanced"),
            ["case-performance"] = ("מאווררי מערכת · ביצועים", "System fans · Performance"),
            ["nvme-balanced"] = ("NVMe · מאוזן", "NVMe · Balanced"),
            ["nvme-performance"] = ("NVMe · ביצועים", "NVMe · Performance"),
            [Silent] = ("שקט", "Silent"),
            [Balanced] = ("מאוזן", "Balanced"),
            [Performance] = ("ביצועים", "Performance"),
            [Max] = ("מקסימום", "Max"),
        };

        /// <summary>Built-in curves/profiles follow the UI language unless the user renamed them.</summary>
        public static string DisplayName(string id, string stored)
        {
            if (BuiltInNames.TryGetValue(id, out var n) && (stored == n.he || stored == n.en)) return L.T(n.he, n.en);
            return stored;
        }

        /// <summary>Fills in a sensible assignment for a newly discovered channel in every profile.</summary>
        public static void EnsureChannel(AppConfig cfg, string channelId, ChannelKind kind)
        {
            foreach (var prof in cfg.Profiles)
            {
                if (prof.Channels.ContainsKey(channelId)) continue;
                prof.Channels[channelId] = DefaultFor(prof.Id, kind);
            }
        }

        private static ChannelAssignment DefaultFor(string profile, ChannelKind kind)
        {
            ChannelAssignment Curve(string id) => new() { Mode = FanMode.Curve, CurveId = id };
            ChannelAssignment Fixed(double p) => new() { Mode = FanMode.Fixed, FixedPct = p };
            ChannelAssignment Auto() => new() { Mode = FanMode.Auto };

            if (profile == Max) return Fixed(100);
            return kind switch
            {
                ChannelKind.Gpu => profile switch
                {
                    Silent => Curve("gpu-silent"),
                    Performance => Curve("gpu-performance"),
                    _ => Auto()
                },
                ChannelKind.AioFans => profile switch
                {
                    Silent => Curve("cpu-silent"),
                    Performance => Curve("cpu-performance"),
                    _ => Curve("cpu-balanced")
                },
                ChannelKind.Pump => profile switch
                {
                    Silent => Fixed(50),
                    Performance => Fixed(90),
                    _ => Fixed(70)
                },
                ChannelKind.NvmeFan => profile switch
                {
                    Performance => Curve("nvme-performance"),
                    _ => Curve("nvme-balanced")
                },
                // Motherboard headers: BIOS Smart Fan until the user decides what is plugged where.
                _ => Auto()
            };
        }
    }
}
