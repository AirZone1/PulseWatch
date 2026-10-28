using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using LibreHardwareMonitor.Hardware;

namespace PulseWatch.Core
{
    /// <summary>
    /// Owns every piece of hardware access (LibreHardwareMonitor, NVML, Lian Li USB) on one dedicated thread,
    /// keeps an hour of history for every sensor and runs the fan engine after each sample.
    /// UI code only reads published values and posts commands through <see cref="Run"/>.
    /// </summary>
    public sealed class SensorHub : IDisposable
    {
        public const int HistorySeconds = 3600;

        private readonly AppConfig _cfg;
        private readonly Thread _thread;
        private readonly ConcurrentQueue<Action> _commands = new();
        private readonly AutoResetEvent _wake = new(false);
        private volatile bool _stop;

        private Computer? _lhm;
        private readonly Dictionary<ISensor, SensorInfo> _lhmMap = new();
        private readonly Dictionary<string, SensorInfo> _sensors = new();
        private readonly List<SensorInfo> _sensorList = new();
        private readonly Dictionary<string, string> _alias = new();
        private readonly Dictionary<string, FanState> _fanState = new();
        private readonly ConcurrentDictionary<string, DateTime> _identifyUntil = new();
        private readonly List<FanChannel> _channels = new();
        private readonly Ring _times = new(HistorySeconds);

        private readonly Dictionary<IHardware, DateTime> _nextUpdate = new();
        private long _netRx, _netTx;
        private NetworkInterface[]? _netAdapters;
        private DateTime _netAdaptersAt;
        private DateTime _netTime;
        private DateTime _lastConflictCheck = DateTime.MinValue;
        private DateTime _lastTick = DateTime.UtcNow;
        private int _tick;
        private bool _cpuEmergency, _gpuEmergency;
        private string _lightSignature = "";
        private DateTime _lastLightWrite = DateTime.MinValue;

        public NvidiaGpu? Gpu { get; private set; }
        public LianLiTrinity? Aio { get; private set; }
        public GpuLive? GpuLive { get; private set; }
        public bool Ready { get; private set; }
        public string? SuperIoChip { get; private set; }
        public string? BoardName { get; private set; }
        private BoardProfile? _board;
        /// <summary>"X870I AORUS PRO ICE (ITE IT8696E)" — what the UI shows for the motherboard.</summary>
        public string BoardDisplay
        {
            get
            {
                string b = _board?.Display ?? (BoardName ?? "").Replace("Gigabyte ", "").Trim();
                if (string.IsNullOrEmpty(b)) return SuperIoChip ?? "";
                return SuperIoChip != null ? $"{b} ({SuperIoChip})" : b;
            }
        }
        public string StatusText { get; private set; } = L.T("מאתחל חומרה…", "Starting hardware…");
        public bool Emergency => _cpuEmergency || _gpuEmergency;

        /// <summary>Which part is over its safety limit, for the banner.</summary>
        public string EmergencyText =>
            _cpuEmergency && _gpuEmergency ? L.T("המעבד וכרטיס המסך הגיעו לגבול החומרה — מאווררים על עקומה ממשיכים לפי העקומה; מאווררים על מהירות קבועה הועלו ל-100%. המשאבה נשארת כפי שהגדרת.", "CPU and graphics card reached their hardware limit — fans on a curve keep following it; fans on a fixed speed were raised to 100%. The pump stays as you set it.")
            : _cpuEmergency ? L.T("המעבד הגיע לגבול החומרה — מאווררים על עקומה ממשיכים לפי העקומה; מאווררי רדיאטור שעל מהירות קבועה הועלו ל-100% עד שיתקרר. המשאבה נשארת כפי שהגדרת.", "CPU reached its hardware limit — fans on a curve keep following it; radiator fans on a fixed speed are raised to 100% until it cools down. The pump stays as you set it.")
            : L.T("כרטיס המסך הגיע לגבול החומרה — מאווררי המערכת ומאווררי הכרטיס שאינם על עקומה הועלו ל-100% עד שיתקרר.", "Graphics card reached its hardware limit — the system fans and the card's fans that are not on a curve were raised to 100% until it cools down.");
        public DateTime LastSample { get; private set; }

        /// <summary>
        /// Set by the UI while a window is on screen. When false, only what fan control and the
        /// thermal safety net need is sampled every second; everything else drops to a slow cadence.
        /// </summary>
        public volatile bool UiActive = true;
        /// <summary>Set while a page that shows drives / all sensors is visible (drives are slow to poll).</summary>
        public volatile bool WantAllSensors;

        public event Action? Updated;
        public event Action<string>? Notice;

        private sealed class FanState
        {
            public double HystTemp = double.NaN;
            public double Pct = double.NaN;
        }

        public SensorHub(AppConfig cfg)
        {
            _cfg = cfg;
            _thread = new Thread(Loop) { IsBackground = true, Name = "SensorHub", Priority = ThreadPriority.AboveNormal };
        }

        public void Start() => _thread.Start();

        // ───────────────────────────── public read API (any thread) ─────────────────────────────

        public IReadOnlyList<FanChannel> Channels { get { lock (_channels) return _channels.ToList(); } }

        public IReadOnlyList<SensorInfo> AllSensors { get { lock (_sensorList) return _sensorList.ToList(); } }

        public SensorInfo? Sensor(string? idOrAlias)
        {
            if (idOrAlias == null) return null;
            lock (_sensorList)
            {
                if (idOrAlias.StartsWith("@"))
                {
                    if (_alias.TryGetValue(idOrAlias, out var real)) idOrAlias = real;
                    else if (idOrAlias.StartsWith(DrivePrefix))
                    {
                        // "@drive:<model>" follows one physical drive by model, independent of disk numbering
                        string model = idOrAlias[DrivePrefix.Length..];
                        var hit = _sensorList.FirstOrDefault(s => s.HardwareKind == "Storage" && s.Type == "Temperature" && s.Hardware.Trim() == model && (s.Name.Contains("Composite") || s.Name == "Temperature"));
                        if (hit == null) return null;
                        _alias[idOrAlias] = hit.Id;
                        return hit;
                    }
                }
                return _sensors.TryGetValue(idOrAlias, out var s2) ? s2 : null;
            }
        }

        public const string DrivePrefix = "@drive:";

        /// <summary>Physical drives that report a temperature (model, sensor), NVMe first.</summary>
        public List<(string Model, SensorInfo Sensor)> DriveTemps()
        {
            lock (_sensorList)
                return _sensorList.Where(s => s.HardwareKind == "Storage" && s.Type == "Temperature" && !s.Id.StartsWith("virt/") && (s.Name.Contains("Composite") || s.Name == "Temperature"))
                    .GroupBy(s => s.Hardware.Trim()).Select(g => (g.Key, g.First()))
                    .OrderBy(x => x.Item2.Id.StartsWith("/nvme") ? 0 : 1).ToList();
        }

        public float Value(string idOrAlias) => Sensor(idOrAlias)?.Value ?? float.NaN;

        public string AliasTarget(string alias)
        {
            lock (_sensorList) return _alias.TryGetValue(alias, out var r) ? r : alias;
        }

        public static (string Alias, string Name)[] KnownAliases => new[]
        {
            ("@cpu.temp", L.T("מעבד (CPU) · Tctl", "CPU · Tctl")),
            ("@cpu.ccd", L.T("מעבד (CPU) · CCD", "CPU · CCD")),
            ("@gpu.temp", L.T("כרטיס מסך (GPU) · ליבה", "GPU · Core")),
            ("@gpu.hotspot", L.T("כרטיס מסך (GPU) · Hot Spot", "GPU · Hot Spot")),
            ("@gpu.memtemp", L.T("כרטיס מסך (GPU) · זיכרון VRAM", "GPU · VRAM")),
            ("@nvme.temp", L.T("כונן NVMe", "NVMe drive")),
            ("@mb.temp", L.T("לוח אם · החיישן החם ביותר", "Motherboard · hottest sensor"))
        };

        /// <summary>Time stamps (unix seconds) of the last n samples, oldest first.</summary>
        public float[] TimeLine(int n) => _times.Last(n);

        public Task Run(Action action)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _commands.Enqueue(() =>
            {
                try { action(); tcs.SetResult(); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            _wake.Set();
            return tcs.Task;
        }

        public Task<T> Run<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _commands.Enqueue(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            _wake.Set();
            return tcs.Task;
        }

        public void Poke() => _wake.Set();

        // ───────────────────────────────────── hub thread ─────────────────────────────────────

        private void Loop()
        {
            try { Init(); }
            catch (Exception ex) { Log.Error("Hub init failed", ex); StatusText = L.T("שגיאה באתחול חומרה: ", "Hardware init failed: ") + ex.Message; }
            Ready = true;
            var sw = Stopwatch.StartNew();
            while (!_stop)
            {
                long started = sw.ElapsedMilliseconds;
                DrainCommands();
                try { Sample(); }
                catch (Exception ex) { Log.Error("Sample failed", ex); }
                try { Updated?.Invoke(); } catch (Exception ex) { Log.Error("Updated handler", ex); }

                int period;
                lock (_cfg) period = Math.Clamp(_cfg.PollMs, 250, 5000);
                while (!_stop)
                {
                    long left = period - (sw.ElapsedMilliseconds - started);
                    if (left <= 0) break;
                    if (_wake.WaitOne((int)left)) DrainCommands();
                }
            }
            Shutdown();
        }

        private void DrainCommands()
        {
            while (_commands.TryDequeue(out var cmd))
            {
                try { cmd(); } catch (Exception ex) { Log.Error("Command failed", ex); }
            }
        }

        private void Init()
        {
            StatusText = L.T("טוען חיישנים…", "Loading sensors…");
            // The NVIDIA card is read directly (NVML + NvAPI, ~1.5 ms) instead of through LibreHardwareMonitor,
            // whose GPU path costs ~86 ms per update on this machine (D3D counters). The idle iGPU is skipped too.
            // Other graphics cards (AMD / Intel) go through LibreHardwareMonitor — only on PCs without an NVIDIA card,
            // so this machine keeps the cheap direct path. Controllers = fan/pump hubs LHM knows (NZXT Kraken/Grid,
            // Aquacomputer, T-Balancer, Heatmaster, …); they are only read until the user picks a mode for them.
            bool hasNvidia = DisplayAdapters().Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
            bool controllers = FanControllerPresent();
            _lhm = new Computer
            {
                IsCpuEnabled = true, IsGpuEnabled = !hasNvidia, IsMemoryEnabled = true, IsMotherboardEnabled = true,
                IsControllerEnabled = controllers, IsStorageEnabled = true, IsNetworkEnabled = false, IsPsuEnabled = false
            };
            _lhm.Open();
            BoardName = _lhm.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Motherboard)?.Name;
            _board = BoardProfile.Find(BoardName);
            var start = DateTime.UtcNow;
            foreach (var hw in _lhm.Hardware) UpdateTiered(hw, start, true); // honours "don't wake sleeping disks" from the first read
            RegisterLhmSensors();
            Log.Info("SuperIO at start: " + string.Join(", ", _lhmMap.Where(kv => kv.Value.HardwareKind == "SuperIO" && kv.Key.SensorType is SensorType.Fan or SensorType.Control)
                                                                     .Select(kv => $"{kv.Value.Id}={kv.Key.Value?.ToString("0") ?? "null"}")));

            Gpu = NvidiaGpu.TryOpen();
            if (Gpu != null)
            {
                var first = Gpu.Read(true);
                lock (_cfg)
                {
                    _cfg.Gpu.DefaultPowerLimitW ??= Gpu.Limits.PowerDefaultW;
                    _cfg.Gpu.DefaultTempLimitC ??= first.TempLimit > 0 ? first.TempLimit : 83;
                    // a capped curve applied earlier in this Windows session is still on the card: keep watching it
                    if (_cfg.Gpu.Applied?.CapMv != null && Gpu.Private?.ReadCurve().Any(x => x.DeltaMhz != 0) == true)
                        _capTune = _cfg.Gpu.Applied.Clone();
                }
                string g = Gpu.Name;
                void V(string id, string he, string en, string type, string unit) => AddVirtual(id, L.T(he, en), g, "GpuNvidia", type, unit);
                V("virt/gpu/temp", "GPU · ליבה", "GPU Core", "Temperature", "°C");
                V("virt/gpu/hotspot", "GPU · Hot Spot (הנקודה החמה ביותר בשבב)", "GPU Hot Spot (hottest point on the die)", "Temperature", "°C");
                V("virt/gpu/memtemp", "GPU · זיכרון VRAM (Junction)", "GPU Memory Junction (VRAM)", "Temperature", "°C");
                V("virt/gpu/load", "GPU · עומס ליבה", "GPU Core Load", "Load", "%");
                V("virt/gpu/memload", "GPU · עומס בקר זיכרון", "GPU Memory Controller", "Load", "%");
                V("virt/gpu/clock", "GPU · תדר ליבה", "GPU Core Clock", "Clock", "MHz");
                V("virt/gpu/memclock", "GPU · תדר זיכרון", "GPU Memory Clock", "Clock", "MHz");
                V("virt/gpu/voltage", "GPU · מתח ליבה", "GPU Core Voltage", "Voltage", "V");
                V("virt/gpu/power", "GPU · הספק לוח", "GPU Board Power", "Power", "W");
                V("virt/gpu/powerlimit", "GPU · Power Limit", "GPU Power Limit", "Power", "W");
                V("virt/gpu/vram", "GPU · VRAM בשימוש", "GPU Memory Used", "SmallData", "MB");
                V("virt/gpu/fanpct", "GPU · מאווררים (%)", "GPU Fans (%)", "Control", "%");
                for (int i = 0; i < Math.Max(1, first.FanRpm.Length); i++)
                    V($"virt/gpu/fan{i + 1}", $"GPU · מאוורר {i + 1}", $"GPU Fan {i + 1}", "Fan", "RPM");
                V("virt/gpu/coreoffset", "GPU · Core Offset", "GPU Core Offset", "Clock", "MHz");
                V("virt/gpu/memoffset", "GPU · Memory Offset", "GPU Memory Offset", "Clock", "MHz");
            }

            Aio = LianLiTrinity.TryOpen();
            if (Aio != null)
            {
                AddVirtual("virt/aio/fan", L.T("מאווררי רדיאטור", "Radiator fans"), Aio.Model, "Cooler", "Fan", "RPM");
                AddVirtual("virt/aio/pump", L.T("משאבה", "Pump"), Aio.Model, "Cooler", "Fan", "RPM");
            }

            AddVirtual("virt/net/down", L.T("הורדה", "Download"), L.T("רשת", "Network"), "Network", "Throughput", "MB/s");
            AddVirtual("virt/net/up", L.T("העלאה", "Upload"), L.T("רשת", "Network"), "Network", "Throughput", "MB/s");
            ReadNetwork(true);

            ResolveAliases();
            BuildChannels();
            DetectHardwareLimits();
            Conflicts.Refresh(_cfg);
            UpdateBlocked(initial: true);

            StatusText = BuildStatus();
            Log.Info("Hub ready: " + StatusText);
        }

        private string BuildStatus()
        {
            var parts = new List<string>();
            parts.Add(SuperIoChip != null ? L.T($"לוח אם: {BoardDisplay}", $"Motherboard: {BoardDisplay}") : L.T("לוח אם: אין גישה (PawnIO?)", "Motherboard: no access (PawnIO?)"));
            parts.Add(Gpu != null ? (Gpu.Private?.CurveSupported == true ? "NVIDIA: NVML + NvAPI" : "NVIDIA: NVML") : L.T("NVIDIA: לא זמין", "NVIDIA: unavailable"));
            parts.Add(Aio != null ? $"Lian Li: {Aio.Model}" : L.T("Lian Li: לא נמצא", "Lian Li: not found"));
            return string.Join("   •   ", parts);
        }

        /// <summary>
        /// Polling tiers. Anything a fan curve or the thermal safety net can depend on stays at 1 s;
        /// expensive or display-only hardware runs slower — especially when no window is open.
        /// Measured costs here: old SATA HDD SMART ≈ 270 ms, NVMe ≈ 5 ms, DIMM SPD ≈ 1 ms, CPU ≈ 8 ms, SuperIO ≈ 0.7 ms.
        /// </summary>
        private TimeSpan IntervalFor(IHardware hw, bool ui)
        {
            string id = hw.Identifier.ToString();
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                case HardwareType.Motherboard:
                case HardwareType.SuperIO:
                    return TimeSpan.Zero;
                case HardwareType.Memory:
                    if (id.StartsWith("/ram") || id.StartsWith("/vram")) return ui ? TimeSpan.Zero : TimeSpan.FromSeconds(5);
                    return TimeSpan.FromSeconds(ui ? 10 : 60);           // DIMM SPD temperatures
                case HardwareType.Storage:
                    if (id.StartsWith("/nvme"))
                    {
                        int nv;
                        lock (_cfg) nv = Math.Clamp(_cfg.NvmePollSeconds, 1, 120);
                        return TimeSpan.FromSeconds(ui ? nv : Math.Max(nv, 10));
                    }
                    // spinning / SATA drives: SMART reads are slow (270 ms here) and temperatures move over minutes
                    int hdd;
                    lock (_cfg) hdd = Math.Clamp(_cfg.HddPollSeconds, 30, 3600);
                    return TimeSpan.FromSeconds(WantAllSensors ? Math.Min(hdd, 30) : hdd);
                default:
                    return TimeSpan.FromSeconds(ui ? 2 : 10);
            }
        }

        /// <summary>
        /// LHM's controller scan takes ~9 s on this PC even with nothing attached, so it only runs when a USB device
        /// of a supported fan-controller maker is present (HID list ~80 ms): NZXT, Aquacomputer, AeroCool, Razer PWM hub.
        /// </summary>
        private static bool FanControllerPresent()
        {
            try
            {
                var hit = HidSharp.DeviceList.Local.GetHidDevices().FirstOrDefault(d =>
                    d.VendorID is 0x1E71 or 0x0C70 or 0x2E97 || (d.VendorID == 0x1532 && d.ProductID == 0x0F3C));
                if (hit != null) Log.Info($"Fan controller found: VID {hit.VendorID:X4} PID {hit.ProductID:X4}");
                return hit != null;
            }
            catch (Exception ex) { Log.Warn("Controller scan: " + ex.Message); return false; }
        }

        /// <summary>The CPU's own maximum temperature (where it starts throttling itself).</summary>
        public double HwCpuLimitC { get; private set; } = 95;
        /// <summary>The graphics card's maximum operating temperature as the card reports it.</summary>
        public double HwGpuLimitC { get; private set; } = 90;

        /// <summary>
        /// Hardware limits for the thermal safety net. AMD does not expose Tjmax, so it comes from the model:
        /// Ryzen 3000/7000/8000/9000 = 95 °C, Ryzen 5000 = 90 °C; Intel desktop parts = 100 °C. The card reports its own.
        /// v5: safety thresholds still on the old fixed defaults (90 / 87) move to these limits.
        /// </summary>
        private void DetectHardwareLimits()
        {
            string cpu = _lhm?.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu)?.Name ?? "";
            var m = System.Text.RegularExpressions.Regex.Match(cpu, @"Ryzen\s+\d\s+(?:PRO\s+)?(\d)\d{3}");
            HwCpuLimitC = cpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? 100
                        : m.Success && m.Groups[1].Value == "5" ? 90 : 95;
            var l = Gpu?.Limits;
            HwGpuLimitC = l == null ? 90 : l.GpuMaxTemp > 0 ? l.GpuMaxTemp : l.SlowdownTemp > 0 ? l.SlowdownTemp - 2 : 90;
            lock (_cfg)
            {
                if (_cfg.SchemaVersion < 5)
                {
                    if (Math.Abs(_cfg.SafetyCpuC - 90) < 0.1) _cfg.SafetyCpuC = HwCpuLimitC;
                    if (Math.Abs(_cfg.SafetyGpuC - 87) < 0.1) _cfg.SafetyGpuC = HwGpuLimitC;
                    _cfg.SchemaVersion = 5;
                }
                _cfg.SafetyCpuC = Math.Min(_cfg.SafetyCpuC, HwCpuLimitC);
                _cfg.SafetyGpuC = Math.Min(_cfg.SafetyGpuC, HwGpuLimitC);
            }
            Log.Info($"Hardware limits: CPU {HwCpuLimitC} °C ({cpu}), GPU {HwGpuLimitC} °C");
        }

        private static List<string> DisplayAdapters()
        {
            var names = new List<string>();
            try
            {
                using var q = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                foreach (var o in q.Get()) using (o) if (o["Name"] is string n) names.Add(n);
            }
            catch (Exception ex) { Log.Warn("Display adapters: " + ex.Message); }
            return names;
        }

        /// <summary>
        /// LHM hardware PulseWatch drives itself or must not touch: NVIDIA (NVML/NvAPI path) and Lian Li devices
        /// (own HID protocol — two programs talking to the same HID device garble each other's replies).
        /// </summary>
        private static bool Skip(IHardware hw) =>
            hw.HardwareType == HardwareType.GpuNvidia ||
            (hw.HardwareType == HardwareType.Cooler && hw.Name.Contains("Lian", StringComparison.OrdinalIgnoreCase));

        private void UpdateTiered(IHardware hw, DateTime now, bool ui)
        {
            if (Skip(hw)) return;
            // stores the last update time, so a changed tier (window opened) takes effect immediately
            if (!_nextUpdate.TryGetValue(hw, out var last) || now - last >= IntervalFor(hw, ui))
            {
                string hid = hw.Identifier.ToString();
                if (hw.HardwareType == HardwareType.Storage && !hid.StartsWith("/nvme") && KeepAsleep(hid))
                {
                    _nextUpdate[hw] = now;   // check again next interval, without touching the disk
                    return;
                }
                long t0 = Stopwatch.GetTimestamp();
                try { hw.Update(); } catch { }
                ProfAdd("lhm:" + hw.HardwareType, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
                _nextUpdate[hw] = now;
            }
            foreach (var sub in hw.SubHardware) UpdateTiered(sub, now, ui);
        }

        /// <summary>Drive ids (e.g. "/hdd/0") Windows currently reports as spun down.</summary>
        public ConcurrentDictionary<string, bool> SleepingDrives { get; } = new();

        /// <summary>True if the drive is asleep and the user asked not to wake it.</summary>
        private bool KeepAsleep(string hardwareId)
        {
            bool enabled;
            lock (_cfg) enabled = _cfg.HddKeepAsleep;
            // LibreHardwareMonitor ids end with the physical drive number ("/hdd/0" = \\.\PhysicalDrive0)
            if (!int.TryParse(hardwareId[(hardwareId.LastIndexOf('/') + 1)..], out int n)) return false;
            bool asleep = DiskPower.IsAsleep(n) == true;
            if (asleep) SleepingDrives[hardwareId] = true; else SleepingDrives.TryRemove(hardwareId, out _);
            return enabled && asleep;
        }

        private static void UpdateRecursive(IHardware hw, bool includeStorage)
        {
            if (hw.HardwareType == HardwareType.Storage && !includeStorage) return;
            try { hw.Update(); } catch { }
            foreach (var sub in hw.SubHardware) UpdateRecursive(sub, includeStorage);
        }

        private void RegisterLhmSensors()
        {
            if (_lhm == null) return;
            var board = _board;
            void Walk(IHardware hw, string parentName)
            {
                if (Skip(hw)) return;
                bool sio = hw.HardwareType == HardwareType.SuperIO;
                if (sio) SuperIoChip = hw.Name;
                foreach (var s in hw.Sensors)
                {
                    if (_lhmMap.ContainsKey(s)) continue;
                    string id = s.Identifier.ToString();
                    string name = s.Name;
                    bool cpuMirror = false;
                    if (sio && board != null)
                    {
                        if (s.SensorType == SensorType.Temperature && board.Temps.TryGetValue(s.Index, out var t)) { name = L.T(t.he, t.en); cpuMirror = t.cpuMirror; }
                        else if (s.SensorType == SensorType.Voltage && board.Volts.TryGetValue(s.Index, out var v)) name = v.name;
                        else if (s.SensorType is SensorType.Fan or SensorType.Control && board.Headers.TryGetValue(s.Index, out var f)) name = L.T(f.he, f.en);
                    }
                    var info = new SensorInfo
                    {
                        Id = id,
                        Name = name,
                        CpuMirror = cpuMirror,
                        // show the board, not just the controller chip
                        Hardware = sio ? BoardDisplay : hw.Name,
                        HardwareKind = hw.HardwareType.ToString(),
                        Type = s.SensorType.ToString(),
                        Unit = UnitOf(s.SensorType),
                        History = new Ring(HistorySeconds, _times.Count)
                    };
                    lock (_cfg) if (_cfg.SensorLabels.TryGetValue(id, out var lbl)) info.Label = lbl;
                    _lhmMap[s] = info;
                    lock (_sensorList) { _sensors[id] = info; _sensorList.Add(info); }
                    if (Ready) Log.Info($"Sensor appeared: {id} ({s.Name}) = {s.Value}");
                }
                foreach (var sub in hw.SubHardware) Walk(sub, hw.Name);
            }
            foreach (var hw in _lhm.Hardware) Walk(hw, "");
        }

        private void AddVirtual(string id, string name, string hardware, string kind, string type, string unit)
        {
            var info = new SensorInfo { Id = id, Name = name, Hardware = hardware, HardwareKind = kind, Type = type, Unit = unit, History = new Ring(HistorySeconds, _times.Count) };
            lock (_cfg) if (_cfg.SensorLabels.TryGetValue(id, out var lbl)) info.Label = lbl;
            lock (_sensorList) { _sensors[id] = info; _sensorList.Add(info); }
        }

        public static string UnitOf(SensorType t) => t switch
        {
            SensorType.Temperature => "°C",
            SensorType.Voltage => "V",
            SensorType.Current => "A",
            SensorType.Power => "W",
            SensorType.Clock => "MHz",
            SensorType.Fan => "RPM",
            SensorType.Load or SensorType.Control or SensorType.Level => "%",
            SensorType.Data => "GB",
            SensorType.SmallData => "MB",
            SensorType.Throughput => "MB/s",
            SensorType.Frequency => "Hz",
            SensorType.Energy => "mWh",
            SensorType.Flow => "L/h",
            _ => ""
        };

        private void ResolveAliases()
        {
            SensorInfo? Find(Func<SensorInfo, bool> pred) { lock (_sensorList) return _sensorList.FirstOrDefault(pred); }
            void Set(string alias, SensorInfo? s) { if (s != null) lock (_sensorList) _alias[alias] = s.Id; }

            bool Cpu(SensorInfo s) => s.HardwareKind == "Cpu";

            Set("@cpu.temp", Find(s => Cpu(s) && s.Type == "Temperature" && s.Name.Contains("Tctl"))
                          ?? Find(s => Cpu(s) && s.Type == "Temperature" && s.Name.Contains("Package"))
                          ?? Find(s => Cpu(s) && s.Type == "Temperature"));
            Set("@cpu.ccd", Find(s => Cpu(s) && s.Type == "Temperature" && s.Name.StartsWith("CCD")) ?? Sensor("@cpu.temp"));
            Set("@cpu.load", Find(s => Cpu(s) && s.Type == "Load" && s.Name == "CPU Total"));
            // the effective clock: "Cores (Average)" reports the requested clock, which reads ~5.2 GHz even at idle
            Set("@cpu.clock", Find(s => Cpu(s) && s.Type == "Clock" && s.Name == "Cores (Average Effective)")
                           ?? Find(s => Cpu(s) && s.Type == "Clock" && s.Name == "Cores (Average)") ?? Find(s => Cpu(s) && s.Type == "Clock"));
            Set("@cpu.power", Find(s => Cpu(s) && s.Type == "Power" && s.Name == "Package"));
            Set("@cpu.voltage", Find(s => Cpu(s) && s.Type == "Voltage" && (s.Name.Contains("Core (SVI") || s.Name.Contains("VDDCR"))) ?? Find(s => Cpu(s) && s.Type == "Voltage"));

            SensorInfo? Id(string id) => Find(s => s.Id == id);
            Set("@gpu.temp", Id("virt/gpu/temp"));
            Set("@gpu.hotspot", Id("virt/gpu/hotspot"));
            Set("@gpu.memtemp", Id("virt/gpu/memtemp"));
            Set("@gpu.load", Id("virt/gpu/load"));
            Set("@gpu.clock", Id("virt/gpu/clock"));
            Set("@gpu.memclock", Id("virt/gpu/memclock"));
            Set("@gpu.power", Id("virt/gpu/power"));
            Set("@gpu.voltage", Id("virt/gpu/voltage"));
            Set("@gpu.vram", Id("virt/gpu/vram"));
            Set("@gpu.fanrpm", Id("virt/gpu/fan1"));
            if (Gpu == null)
            {
                bool G(SensorInfo s) => s.HardwareKind is "GpuAmd" or "GpuIntel";
                SensorInfo? GS(string type, params string[] names) =>
                    names.Select(n => Find(s => G(s) && s.Type == type && s.Name == n)).FirstOrDefault(x => x != null);
                Set("@gpu.temp", GS("Temperature", "GPU Core", "GPU Temperature") ?? Find(s => G(s) && s.Type == "Temperature"));
                Set("@gpu.hotspot", GS("Temperature", "GPU Hot Spot"));
                Set("@gpu.memtemp", GS("Temperature", "GPU Memory"));
                Set("@gpu.load", GS("Load", "GPU Core", "D3D 3D"));
                Set("@gpu.clock", GS("Clock", "GPU Core"));
                Set("@gpu.memclock", GS("Clock", "GPU Memory"));
                Set("@gpu.power", GS("Power", "GPU Package", "GPU Core", "GPU Power"));
                Set("@gpu.fanrpm", GS("Fan", "GPU Fan"));
            }

            Set("@ram.load", Find(s => s.HardwareKind == "Memory" && s.Id.StartsWith("/ram") && s.Type == "Load"));
            Set("@ram.used", Find(s => s.HardwareKind == "Memory" && s.Id.StartsWith("/ram") && s.Type == "Data" && s.Name == "Memory Used"));
            Set("@ram.avail", Find(s => s.HardwareKind == "Memory" && s.Id.StartsWith("/ram") && s.Type == "Data" && s.Name == "Memory Available"));

            // "@nvme.temp" is computed every tick as the hottest NVMe drive (drives the M.2 fan curve)
            AddVirtual("virt/nvme/max", L.T("כונני NVMe · החם ביותר", "NVMe drives · hottest"), "NVMe", "Storage", "Temperature", "°C");
            Set("@nvme.temp", Id("virt/nvme/max"));
            Set("@aio.fan", Find(s => s.Id == "virt/aio/fan"));
            Set("@aio.pump", Find(s => s.Id == "virt/aio/pump"));
            Set("@net.down", Find(s => s.Id == "virt/net/down"));
            Set("@net.up", Find(s => s.Id == "virt/net/up"));

            // "@mb.temp" is computed every tick as the hottest SuperIO sensor
            AddVirtual("virt/mb/max", L.T("לוח אם · החיישן החם ביותר", "Motherboard · hottest sensor"), BoardDisplay, "Motherboard", "Temperature", "°C");
            Set("@mb.temp", Find(s => s.Id == "virt/mb/max"));
        }

        private void BuildChannels()
        {
            var list = new List<FanChannel>();
            var absent = new List<string>();
            if (_lhm != null)
            {
                var board = _board;
                foreach (var hw in _lhm.Hardware.SelectMany(h => h.SubHardware.Prepend(h)))
                {
                    if (Skip(hw)) continue;
                    if (hw.HardwareType is HardwareType.Cooler or HardwareType.GpuAmd or HardwareType.GpuIntel)
                    {
                        foreach (var ctrl in hw.Sensors.Where(x => x.SensorType == SensorType.Control && x.Control != null).OrderBy(x => x.Index))
                        {
                            var fan = hw.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Fan && x.Index == ctrl.Index);
                            list.Add(new LhmControlChannel(ctrl, fan, hw));
                        }
                        continue;
                    }
                    if (hw.HardwareType != HardwareType.SuperIO) continue;
                    foreach (var ctrl in hw.Sensors.Where(s => s.SensorType == SensorType.Control && s.Control != null).OrderBy(s => s.Index))
                    {
                        var fan = hw.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Index == ctrl.Index);
                        string help = board != null && board.HeaderHelp.TryGetValue(ctrl.Index, out var hh) ? L.T(hh.he, hh.en) : "";
                        if (board != null && board.Headers.TryGetValue(ctrl.Index, out var h))
                            list.Add(new SuperIoChannel(ctrl, fan, BoardDisplay, h.kind, L.T(h.he, h.en)) { Help = help });
                        else if (board != null && board.AbsentHeaders.Contains(ctrl.Index))
                        {
                            var ch = new SuperIoChannel(ctrl, fan, BoardDisplay, ChannelKind.Motherboard,
                                L.T($"יציאה #{ctrl.Index + 1} (לא מסומנת)", $"Output #{ctrl.Index + 1} (unlabelled)")) { Help = help };
                            list.Add(ch);
                            absent.Add(ch.Id);
                        }
                        else
                            list.Add(new SuperIoChannel(ctrl, fan, BoardDisplay) { Help = help });
                    }
                }
            }
            if (Gpu != null) list.Add(new GpuFanChannel(Gpu, Sensor("virt/gpu/fan1")?.Id));
            if (Aio != null)
            {
                list.Add(new TrinityPumpChannel(Aio));
                list.Add(new TrinityFanChannel(Aio));
            }

            lock (_cfg)
            {
                bool v3 = _cfg.SchemaVersion < 3;
                foreach (var ch in list)
                {
                    // v3: board profiles may give a header a real role; replace the old "BIOS auto" placeholder once
                    if (v3 && ch.Kind == ChannelKind.NvmeFan)
                        foreach (var p in _cfg.Profiles)
                            if (p.Channels.TryGetValue(ch.Id, out var a) && a.Mode == FanMode.Auto) p.Channels.Remove(ch.Id);
                    Defaults.EnsureChannel(_cfg, ch.Id, ch.Kind);
                }
                // v4: outputs the board profile marks as unwired used to be skipped; they now exist but start hidden
                if (_cfg.SchemaVersion < 4)
                    foreach (var id in absent)
                        if (!_cfg.HiddenChannels.Contains(id)) _cfg.HiddenChannels.Add(id);
                _cfg.SchemaVersion = Math.Max(_cfg.SchemaVersion, 4);
            }
            lock (_channels) { _channels.Clear(); _channels.AddRange(list); }
            foreach (var ch in list) _fanState[ch.Id] = new FanState();
        }

        private void ReadNetwork(bool init)
        {
            long rx = 0, tx = 0;
            try
            {
                // enumerating adapters costs ~5 ms; the set of active adapters rarely changes
                if (_netAdapters == null || (DateTime.UtcNow - _netAdaptersAt).TotalSeconds > 30)
                {
                    _netAdapters = NetworkInterface.GetAllNetworkInterfaces()
                        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                        .ToArray();
                    _netAdaptersAt = DateTime.UtcNow;
                }
                foreach (var ni in _netAdapters)
                {
                    var st = ni.GetIPStatistics();
                    rx += st.BytesReceived;
                    tx += st.BytesSent;
                }
            }
            catch { return; }
            var now = DateTime.UtcNow;
            if (!init && _netTime != default)
            {
                double dt = Math.Max(0.1, (now - _netTime).TotalSeconds);
                Sensor("virt/net/down")?.Set((float)Math.Max(0, (rx - _netRx) / dt / 1048576.0));
                Sensor("virt/net/up")?.Set((float)Math.Max(0, (tx - _netTx) / dt / 1048576.0));
            }
            _netRx = rx; _netTx = tx; _netTime = now;
        }

        private void Sample()
        {
            if (_lhm == null) return;
            _tick++;
            var now0 = DateTime.UtcNow;
            bool ui = UiActive;
            _prof.Restart();
            foreach (var hw in _lhm.Hardware) UpdateTiered(hw, now0, ui);
            if (_tick % 15 == 0) RegisterLhmSensors(); // LHM activates some sensors (e.g. fan tachs) only once they read a value
            Prof("lhm:total");

            foreach (var (s, info) in _lhmMap)
            {
                float v = s.Value ?? float.NaN;
                if (s.SensorType == SensorType.Throughput && !float.IsNaN(v)) v /= 1048576f;
                // board divider tables LHM lacks for this model (e.g. raw 2.03 V → +12.2 V)
                if (_board != null && s.SensorType == SensorType.Voltage && !float.IsNaN(v) && s.Hardware.HardwareType == HardwareType.SuperIO)
                    v = _board.ScaleVoltage(s.Index, v);
                if (s.SensorType == SensorType.Temperature && !float.IsNaN(v) && (v <= 0 || v > 150)) v = float.NaN;
                info.Set(v);
            }

            if (Gpu != null)
            {
                try
                {
                    // voltage + fan RPM are display-only: every 2 s on screen, every 5 s in the tray
                    var g = Gpu.Read(full: _tick % (ui ? 2 : 5) == 1);
                    GpuLive = g;
                    Sensor("virt/gpu/temp")?.Set((float)g.TempCore);
                    Sensor("virt/gpu/hotspot")?.Set((float)g.Hotspot);
                    Sensor("virt/gpu/memtemp")?.Set((float)g.MemJunction);
                    Sensor("virt/gpu/load")?.Set(g.LoadGpu);
                    Sensor("virt/gpu/memload")?.Set(g.LoadMem);
                    Sensor("virt/gpu/clock")?.Set(g.ClockCore);
                    Sensor("virt/gpu/memclock")?.Set(g.ClockMem);
                    Sensor("virt/gpu/voltage")?.Set((float)g.VoltageV);
                    Sensor("virt/gpu/power")?.Set((float)g.PowerW);
                    Sensor("virt/gpu/powerlimit")?.Set((float)g.PowerLimitW);
                    Sensor("virt/gpu/vram")?.Set((float)g.VramUsedMb);
                    Sensor("virt/gpu/fanpct")?.Set(g.FanPct);
                    for (int i = 0; i < g.FanRpm.Length; i++) Sensor($"virt/gpu/fan{i + 1}")?.Set(g.FanRpm[i]);
                    Sensor("virt/gpu/coreoffset")?.Set(g.CoreOffset);
                    Sensor("virt/gpu/memoffset")?.Set(g.MemOffset);
                }
                catch (Exception ex) { Log.Warn("GPU read: " + ex.Message); }
            }
            Prof("gpu");

            float nvMax = float.NaN;
            lock (_sensorList)
            {
                foreach (var s in _sensorList)
                    if (s.HardwareKind == "Storage" && s.Type == "Temperature" && s.Name.Contains("Composite") && s.HasValue && (float.IsNaN(nvMax) || s.Value > nvMax)) nvMax = s.Value;
            }
            Sensor("virt/nvme/max")?.Set(nvMax);

            if (Aio != null)
            {
                bool ok = Aio.Poll();
                bool fresh = ok || (DateTime.UtcNow - Aio.LastStatus).TotalSeconds < 5;
                Sensor("virt/aio/fan")?.Set(fresh && Aio.FanRpm.HasValue ? Aio.FanRpm.Value : float.NaN);
                Sensor("virt/aio/pump")?.Set(fresh && Aio.PumpRpm.HasValue ? Aio.PumpRpm.Value : float.NaN);
            }
            Prof("aio");

            float mbMax = float.NaN;
            lock (_sensorList)
            {
                foreach (var s in _sensorList)
                    if (s.HardwareKind == "SuperIO" && s.Type == "Temperature" && !s.CpuMirror && s.HasValue && (float.IsNaN(mbMax) || s.Value > mbMax)) mbMax = s.Value;
            }
            Sensor("virt/mb/max")?.Set(mbMax);

            Prof("aggregate");
            if (ui) ReadNetwork(false);
            else _netTime = default;
            Prof("network");

            if ((DateTime.UtcNow - _lastConflictCheck).TotalSeconds >= 5)
            {
                _lastConflictCheck = DateTime.UtcNow;
                Conflicts.Refresh(_cfg);
                UpdateBlocked(initial: false);
                KeepVoltageCap();
            }
            Prof("conflicts");

            _times.Add((float)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000));
            lock (_sensorList)
            {
                foreach (var s in _sensorList) s.History.Add(s.Value);
            }
            LastSample = DateTime.Now;
            Prof("history");

            var now = DateTime.UtcNow;
            double dt = Math.Clamp((now - _lastTick).TotalSeconds, 0.05, 5);
            _lastTick = now;
            TickFans(dt);
            Prof("fans");
            TickLighting();
            Prof("lighting");
            ReportProfile(ui);
        }

        // ───────────── self-profiler: one log line per minute with where the time went ─────────────

        private readonly Stopwatch _prof = new();
        private readonly Dictionary<string, double> _profMs = new();
        private int _profTicks;
        private TimeSpan _profCpu0;
        private DateTime _profT0 = DateTime.UtcNow;
        private int _gc0, _gc1, _gc2;

        private void Prof(string phase)
        {
            _profMs[phase] = (_profMs.TryGetValue(phase, out var v) ? v : 0) + _prof.Elapsed.TotalMilliseconds;
            _prof.Restart();
        }

        internal void ProfAdd(string phase, double ms) => _profMs[phase] = (_profMs.TryGetValue(phase, out var v) ? v : 0) + ms;

        private void ReportProfile(bool ui)
        {
            if (++_profTicks < 60) return;
            using var proc = Process.GetCurrentProcess();
            var cpu = proc.TotalProcessorTime;
            double wall = (DateTime.UtcNow - _profT0).TotalMilliseconds;
            double cpuPct = _profCpu0 == TimeSpan.Zero ? double.NaN : (cpu - _profCpu0).TotalMilliseconds / wall / Environment.ProcessorCount * 100;
            string phases = string.Join("  ", _profMs.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}={kv.Value / _profTicks:0.00}"));
            Log.Info($"PERF ui={ui} cpu={cpuPct:0.000}% ws={proc.WorkingSet64 / 1048576}MB managed={GC.GetTotalMemory(false) / 1048576}MB " +
                     $"gc={GC.CollectionCount(0) - _gc0}/{GC.CollectionCount(1) - _gc1}/{GC.CollectionCount(2) - _gc2} ms/tick: {phases}");
            _profMs.Clear();
            _profTicks = 0;
            _profCpu0 = cpu;
            _profT0 = DateTime.UtcNow;
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
        }

        // ───────────────────────────────────── fan engine ─────────────────────────────────────

        private void UpdateBlocked(bool initial)
        {
            foreach (var ch in Channels)
            {
                var app = Conflicts.Apps.FirstOrDefault(a => a.Key == ch.ConflictGroup);
                bool blocked = app?.Running == true;
                if (blocked != ch.Blocked)
                {
                    ch.Blocked = blocked;
                    if (!blocked)
                    {
                        // Re-take control on the next tick
                        ch.LastWrite = DateTime.MinValue;
                        ch.InAuto = true;
                        ch.AppliedPct = null;
                        if (!initial) Notice?.Invoke(L.T($"{app?.Title} נסגר — PulseWatch שולט ב{ch.DefaultName}", $"{app?.Title} closed — PulseWatch now controls {ch.DefaultName}"));
                    }
                }
            }
        }

        private void TickFans(double dt)
        {
            double cpu = Value("@cpu.temp"), gpu = Value("@gpu.temp"), hot = Value("@gpu.hotspot");
            double safetyCpu, safetyGpu;
            lock (_cfg) { safetyCpu = _cfg.SafetyCpuC; safetyGpu = _cfg.SafetyGpuC; }
            // Separate CPU and GPU emergencies: a hot CPU must not spin up the graphics card or the M.2 fan.
            bool cpuOver = !double.IsNaN(cpu) && cpu >= safetyCpu, cpuClear = double.IsNaN(cpu) || cpu < safetyCpu - 6;
            bool gpuOver = (!double.IsNaN(gpu) && gpu >= safetyGpu) || (!double.IsNaN(hot) && hot >= safetyGpu + 12);
            bool gpuClear = (double.IsNaN(gpu) || gpu < safetyGpu - 6) && (double.IsNaN(hot) || hot < safetyGpu + 6);
            if (!_cpuEmergency && cpuOver)
            {
                _cpuEmergency = true;
                Log.Warn($"Thermal emergency (CPU {cpu:0} °C): CPU cooling to 100%");
                Notice?.Invoke(L.T($"⚠ המעבד ב-{cpu:0}°C (גבול החומרה) — מאווררי רדיאטור שעל מהירות קבועה הועלו ל-100%", $"⚠ CPU at {cpu:0} °C (hardware limit) — radiator fans on a fixed speed raised to 100%"));
            }
            else if (_cpuEmergency && cpuClear)
            {
                _cpuEmergency = false;
                Notice?.Invoke(L.T("המעבד התקרר — חזרה לעקומות", "CPU cooled down — back to the curves"));
            }
            if (!_gpuEmergency && gpuOver)
            {
                _gpuEmergency = true;
                Log.Warn($"Thermal emergency (GPU {gpu:0} °C, hot spot {hot:0} °C): GPU cooling to 100%");
                Notice?.Invoke(L.T($"⚠ כרטיס המסך ב-{gpu:0}°C — מאווררי הכרטיס שאינם על עקומה הועלו ל-100%", $"⚠ Graphics card at {gpu:0} °C — card fans not on a curve raised to 100%"));
            }
            else if (_gpuEmergency && gpuClear)
            {
                _gpuEmergency = false;
                Notice?.Invoke(L.T("כרטיס המסך התקרר — חזרה לעקומות", "Graphics card cooled down — back to the curves"));
            }

            foreach (var ch in Channels)
            {
                if (ch.Blocked) continue;
                var st = _fanState[ch.Id];
                ChannelAssignment a;
                FanCurve? curve = null;
                lock (_cfg)
                {
                    a = (_cfg.ActiveProfile.Channels.TryGetValue(ch.Id, out var x) ? x : new ChannelAssignment()).Clone();
                    if (a.Mode == FanMode.Curve) curve = _cfg.FindCurve(a.CurveId)?.Clone();
                }

                // The emergency only steps in where nothing reacts to the heat: a channel whose curve follows the hot
                // part already ramps by itself, so it is left alone. What remains is the hardware that physically cools
                // that part while sitting on a fixed speed / auto: radiator fans for the CPU, the card's fans for the GPU.
                // The pump always stays where the user set it — above that it adds noise, not cooling.
                bool followsCpu = curve?.Sources.Any(x => x.StartsWith("@cpu", StringComparison.Ordinal)) == true;
                bool followsGpu = curve?.Sources.Any(x => x.StartsWith("@gpu", StringComparison.Ordinal)) == true;
                // System (case) fans are the exception: with the card at its limit they go to 100% even on a GPU
                // curve, because moving the hot air out of the case is what helps the card most.
                bool caseFan = ch.Kind == ChannelKind.Motherboard && (followsGpu || a.Mode == FanMode.Fixed);
                bool emergency = (_cpuEmergency && ch.Kind == ChannelKind.AioFans && !followsCpu)
                              || (_gpuEmergency && ((ch.Kind == ChannelKind.Gpu && !followsGpu) || caseFan));
                ch.Emergency = emergency;
                double? target = null; // null = auto
                ch.SourceTemp = null;

                bool identify = _identifyUntil.TryGetValue(ch.Id, out var until) && until > DateTime.UtcNow;
                if (emergency || identify) target = 100;
                else if (a.Mode == FanMode.Fixed) target = a.FixedPct;
                else if (a.Mode == FanMode.Curve)
                {
                    if (curve == null) target = null;
                    else
                    {
                        double t = double.NaN;
                        foreach (var src in curve.Sources)
                        {
                            double v = Value(src);
                            if (!double.IsNaN(v) && (double.IsNaN(t) || v > t)) t = v;
                        }
                        if (double.IsNaN(t))
                        {
                            target = 100;
                            ch.LastError = L.T("חיישן המקור לא זמין — מאוורר ב-100% ליתר ביטחון", "Source sensor unavailable — fan at 100% to be safe");
                        }
                        else
                        {
                            if (double.IsNaN(st.HystTemp) || t > st.HystTemp || t < st.HystTemp - curve.Hysteresis) st.HystTemp = t;
                            ch.SourceTemp = t;
                            double want = curve.Evaluate(st.HystTemp);
                            if (double.IsNaN(st.Pct)) st.Pct = want;
                            else if (want > st.Pct) st.Pct = Math.Min(want, st.Pct + curve.StepUpPerSec * dt);
                            else st.Pct = Math.Max(want, st.Pct - curve.StepDownPerSec * dt);
                            target = st.Pct;
                        }
                    }
                }

                if (a.Mode != FanMode.Curve || emergency) st.Pct = double.NaN;

                try
                {
                    // Zero RPM (Afterburner behaviour): this card refuses manual duty below its minimum (30%),
                    // so a curve value under the minimum hands the fans to the driver, whose fan-stop parks them.
                    // Stop below 80% of the minimum, restart at the minimum — no flapping around the edge.
                    bool zeroRpm = false;
                    if (ch.ZeroMeansAuto && target != null && !emergency && !identify)
                        zeroRpm = ch.InAuto ? target < ch.MinPct : target < ch.MinPct * 0.8;
                    if (target == null || zeroRpm)
                    {
                        ch.TargetPct = null;
                        if (!ch.InAuto)
                        {
                            ch.ApplyAuto();
                            ch.InAuto = true;
                            ch.AppliedPct = null;
                            ch.LastWrite = DateTime.UtcNow;
                        }
                        else if (ch.LastWrite == DateTime.MinValue)
                        {
                            // First tick after start/unblock: make sure the hardware really is in auto
                            ch.ApplyAuto();
                            ch.LastWrite = DateTime.UtcNow;
                        }
                        if (a.Mode != FanMode.Curve || !double.IsNaN(ch.SourceTemp ?? double.NaN)) ch.LastError = null;
                        continue;
                    }

                    int pct = (int)Math.Round(Math.Clamp(target.Value, ch.MinPct, 100));
                    ch.TargetPct = pct;
                    bool due = ch.InAuto || ch.AppliedPct == null || Math.Abs(ch.AppliedPct.Value - pct) >= 1 || DateTime.UtcNow - ch.LastWrite > ch.Refresh;
                    if (due)
                    {
                        ch.Apply(pct);
                        ch.AppliedPct = pct;
                        ch.InAuto = false;
                        ch.LastWrite = DateTime.UtcNow;
                    }
                    if (a.Mode != FanMode.Curve || ch.SourceTemp != null) ch.LastError = null;
                }
                catch (Exception ex)
                {
                    if (ch.LastError != ex.Message) Log.Warn($"Fan {ch.Id}: {ex.Message}");
                    ch.LastError = ex.Message;
                    ch.LastWrite = DateTime.UtcNow;
                }
            }
        }

        /// <summary>Spins one channel to 100% for a few seconds so the user can see/hear which fan it is.</summary>
        public void Identify(string channelId, int seconds = 6)
        {
            _identifyUntil[channelId] = DateTime.UtcNow.AddSeconds(seconds);
            Poke();
        }

        public bool IsIdentifying(string channelId) => _identifyUntil.TryGetValue(channelId, out var u) && u > DateTime.UtcNow;

        /// <summary>Immediately re-evaluate fans (e.g. after a profile switch).</summary>
        public void ReapplyNow()
        {
            Run(() =>
            {
                foreach (var ch in Channels) { ch.LastWrite = DateTime.UtcNow - TimeSpan.FromHours(1); _fanState[ch.Id].Pct = double.NaN; _fanState[ch.Id].HystTemp = double.NaN; }
                _lightSignature = "";
                TickFans(1);
                TickLighting();
            });
        }

        // ───────────────────────────────────── lighting ─────────────────────────────────────

        public static string LerpColor(double t)
        {
            // cold cyan → green → amber → red
            (double r, double g, double b)[] stops = { (0, 200, 255), (0, 255, 140), (255, 170, 0), (255, 30, 30) };
            t = Math.Clamp(t, 0, 1) * (stops.Length - 1);
            int i = Math.Min((int)t, stops.Length - 2);
            double f = t - i;
            var a = stops[i];
            var b2 = stops[i + 1];
            int R = (int)(a.r + (b2.r - a.r) * f), G = (int)(a.g + (b2.g - a.g) * f), B = (int)(a.b + (b2.b - a.b) * f);
            return $"#{R:X2}{G:X2}{B:X2}";
        }

        private void TickLighting()
        {
            if (Aio == null || Channels.Any(c => c.ConflictGroup == "lconnect" && c.Blocked)) return;
            LightingConfig lc;
            lock (_cfg) lc = new LightingConfig
            {
                Mode = _cfg.Lighting.Mode, Pump = _cfg.Lighting.Pump.Clone(), Fans = _cfg.Lighting.Fans.Clone(),
                SyncFansToPump = _cfg.Lighting.SyncFansToPump, ThermalSource = _cfg.Lighting.ThermalSource,
                ThermalCold = _cfg.Lighting.ThermalCold, ThermalHot = _cfg.Lighting.ThermalHot
            };
            if (lc.Mode == "unchanged") return;

            LightEffect pump = lc.Pump, fans = lc.Fans;
            bool mb = lc.Mode == "mbsync";
            if (lc.Mode == "off")
            {
                pump = new LightEffect { Mode = 3, Off = true, Colors = new() { "#000000" } };
                fans = new LightEffect { Mode = 3, Off = true, Colors = new() { "#000000" } };
            }
            if (lc.Mode == "thermal")
            {
                double t = Value(lc.ThermalSource);
                if (double.IsNaN(t)) return;
                double f = (t - lc.ThermalCold) / Math.Max(1, lc.ThermalHot - lc.ThermalCold);
                f = Math.Round(Math.Clamp(f, 0, 1) * 20) / 20; // 5% steps → few USB writes
                string c = LerpColor(f);
                pump = new LightEffect { Mode = 3, Brightness = lc.Pump.Brightness, Scope = 2, Colors = new() { c, c, c, c } };
                fans = new LightEffect { Mode = 3, Brightness = lc.Fans.Brightness, Colors = new() { c, c, c, c } };
            }

            string sig = $"{lc.Mode}|{System.Text.Json.JsonSerializer.Serialize(pump)}|{System.Text.Json.JsonSerializer.Serialize(fans)}|{lc.SyncFansToPump}";
            if (sig == _lightSignature && (DateTime.UtcNow - _lastLightWrite).TotalMinutes < 5) return;
            if ((DateTime.UtcNow - _lastLightWrite).TotalMilliseconds < 800 && lc.Mode == "thermal") return;
            try
            {
                Aio.SetPumpLight(pump, mb);
                Aio.SetFanLight(fans, mb, lc.SyncFansToPump && lc.Mode == "custom");
                _lightSignature = sig;
                _lastLightWrite = DateTime.UtcNow;
            }
            catch (Exception ex) { Log.Warn("Lighting write: " + ex.Message); _lastLightWrite = DateTime.UtcNow; }
        }

        // ───────────────────────────────────── GPU tuning ─────────────────────────────────────

        public bool GpuBlocked => Conflicts.Get("afterburner").Running;

        /// <summary>Applies a tune; returns a list of human-readable problems (empty = success).</summary>
        public Task<List<string>> ApplyGpuTune(GpuTune t) => Run(() =>
        {
            var errors = new List<string>();
            if (Gpu == null) { errors.Add(L.T("כרטיס NVIDIA לא זמין", "NVIDIA card not available")); return errors; }
            if (GpuBlocked) { errors.Add(L.T("MSI Afterburner פתוח — סגור אותו קודם (הוא דורס את ההגדרות)", "MSI Afterburner is open — close it first (it overrides these settings)")); return errors; }
            void Do(string what, string? err) { if (err != null) errors.Add($"{what}: {err}"); }
            Do("Power Limit", Gpu.SetPowerLimit(t.PowerLimitW));
            Do("Clock offsets", Gpu.SetOffsets(t.CoreOffsetMhz, t.MemOffsetMhz));
            if (t.TempLimitC > 0) Do("Temp Limit", Gpu.SetTempLimit(t.TempLimitC));
            if (Gpu.Private?.CurveSupported == true)
            {
                Do("V/F curve", Gpu.Private.WriteDeltas(t.CurveDeltas ?? new Dictionary<int, int>()));
                _capTune = t.CapMv != null ? t.Clone() : null;
                KeepVoltageCap();
                LogCurve("applied");
            }
            GpuLive = Gpu.Read(true);
            Log.Info($"GPU tune applied PL={t.PowerLimitW} core={t.CoreOffsetMhz} mem={t.MemOffsetMhz} temp={t.TempLimitC} curvePts={t.CurveDeltas?.Count(kv => kv.Value != 0) ?? 0} errors={errors.Count}");
            return errors;
        });

        private GpuTune? _capTune;

        /// <summary>
        /// Keeps a flat-topped curve flat: every point above the cap must run exactly the clock of the cap point.
        /// Measured on this RTX 3080: after a temperature step the points below ~1 V dropped 15 MHz while the points
        /// above did not, so 1905 MHz moved from 950 mV to 1031 mV. One status read (~3 ms) every 5 s; a write only
        /// when the live curve drifted.
        /// </summary>
        private void KeepVoltageCap()
        {
            var t = _capTune;
            var p = Gpu?.Private;
            if (t?.CapMv == null || p == null || GpuBlocked || t.CurveDeltas == null) return;
            try
            {
                var pts = p.ReadCurve();
                if (pts.Count == 0) return;
                var cap = pts.OrderBy(x => Math.Abs(x.VoltageMv - t.CapMv.Value)).First();
                double top = cap.FreqMhz;
                var above = pts.Where(x => x.VoltageMv > cap.VoltageMv).ToList();
                if (above.All(x => Math.Abs(x.FreqMhz - top) < 1)) return;
                var deltas = pts.ToDictionary(x => x.Index, x => (int)Math.Round(x.DeltaMhz));
                foreach (var x in above) deltas[x.Index] = Math.Clamp((int)Math.Round(top - x.BaseMhz), -1000, 1000);
                string? err = p.WriteDeltas(deltas);
                int moved = above.Count(x => Math.Abs(x.FreqMhz - top) >= 1);
                Log.Info($"V/F cap kept at {cap.VoltageMv:0.0} mV / {top:0} MHz: {moved} points re-flattened (max was {above.Max(x => x.FreqMhz):0} MHz)" + (err != null ? " error " + err : ""));
                if (err != null) _capTune = null;
            }
            catch (Exception ex) { Log.Warn("V/F cap: " + ex.Message); _capTune = null; }
        }

        /// <summary>Read-back after a write, so the log shows what the card really runs (cap point, peak, peak voltage).</summary>
        private void LogCurve(string when)
        {
            try
            {
                var pts = Gpu?.Private?.ReadCurve();
                if (pts == null || pts.Count == 0) return;
                double max = pts.Max(x => x.FreqMhz);
                var first = pts.First(x => x.FreqMhz >= max);
                Log.Info($"V/F curve {when}: peak {max:0} MHz first reached at {first.VoltageMv:0.0} mV, {pts.Count(x => x.DeltaMhz != 0)} edited points");
            }
            catch (Exception ex) { Log.Warn("V/F read-back: " + ex.Message); }
        }

        /// <summary>Live V/F curve (empty when the card/driver does not expose it).</summary>
        public Task<List<NvApiPrivate.CurvePoint>> ReadCurve() => Run(() => Gpu?.Private?.ReadCurve() ?? new List<NvApiPrivate.CurvePoint>());

        // ───────────────────────────────────── shutdown ─────────────────────────────────────

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            if (_thread.IsAlive && !_thread.Join(4000)) Log.Warn("Hub thread did not stop in time");
        }

        private void Shutdown()
        {
            // Never leave fans parked at a low manual duty with nobody watching the temperatures.
            double exitPct;
            lock (_cfg) exitPct = _cfg.LianLiExitFanPct;
            foreach (var ch in Channels)
            {
                if (ch.Blocked) continue;
                try
                {
                    switch (ch.Kind)
                    {
                        case ChannelKind.Pump: ch.Apply(100); break;
                        case ChannelKind.AioFans: ch.Apply((int)Math.Max(exitPct, ch.AppliedPct ?? 0)); break;
                        default: if (!ch.InAuto) ch.ApplyAuto(); break;
                    }
                }
                catch (Exception ex) { Log.Warn($"Restore {ch.Id}: {ex.Message}"); }
            }
            try { Gpu?.Dispose(); } catch { }
            try { Aio?.Dispose(); } catch { }
            try { _lhm?.Close(); } catch { }
            Log.Info("Hub stopped, hardware handed back");
        }
    }
}
