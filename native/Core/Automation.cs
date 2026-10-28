using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Interop;
using PulseWatch.Views;

namespace PulseWatch.Core
{
    /// <summary>
    /// Everything that runs on its own while PulseWatch sits in the tray: temperature alerts, per-game profiles,
    /// global hotkeys and the OSD. Driven from the UI thread once per hub update (1 s); costs a few dictionary lookups.
    /// </summary>
    public sealed class Automation : IDisposable
    {
        private readonly MainWindow _win;
        private readonly Hotkeys _hotkeys;
        private OsdWindow? _osd;
        private FpsMonitor? _fps;

        public Automation(MainWindow win)
        {
            _win = win;
            _hotkeys = new Hotkeys(OnHotkey);
            ApplySettings();
        }

        /// <summary>Re-reads the settings (hotkeys on/off, OSD on/off and position).</summary>
        public void ApplySettings()
        {
            bool hk, osd, fps;
            lock (App.Config) { hk = App.Config.HotkeysEnabled; osd = App.Config.Osd.Enabled; fps = osd && App.Config.Osd.ShowFps; }
            _hotkeys.Enabled = hk;
            // the frame counter only runs while the OSD shows it
            if (fps && _fps == null) { var m = new FpsMonitor(); _fps = m.Start() ? m : null; }
            else if (!fps && _fps != null) { _fps.Dispose(); _fps = null; }
            if (osd) { _osd ??= new OsdWindow(); _osd.Fps = _fps; _osd.Show(); _osd.Update(); _osd.Place(); }
            else if (_osd != null) { _osd.Close(); _osd = null; }
        }

        public void Tick()
        {
            CheckAlerts();
            CheckGames();
            _osd?.Update();
        }

        public void Dispose()
        {
            _hotkeys.Dispose();
            _fps?.Dispose();
            _fps = null;
            _osd?.Close();
            _osd = null;
        }

        // ───────────── alerts ─────────────

        private readonly Dictionary<string, bool> _alarmed = new();
        private readonly Dictionary<string, DateTime> _lastAlert = new();

        private void CheckAlerts()
        {
            AlertConfig a;
            lock (App.Config) a = App.Config.Alerts;
            if (!a.Enabled) return;
            Check("cpu", "@cpu.temp", a.CpuC, L.T("המעבד", "CPU"));
            Check("gpu", "@gpu.temp", a.GpuC, L.T("ליבת כרטיס המסך", "GPU core"));
            Check("hot", "@gpu.hotspot", a.HotspotC, L.T("Hot Spot של כרטיס המסך", "GPU hot spot"));
            Check("vram", "@gpu.memtemp", a.VramC, L.T("זיכרון כרטיס המסך (VRAM)", "GPU memory (VRAM)"));
            Check("nvme", "@nvme.temp", a.NvmeC, L.T("כונן NVMe", "NVMe drive"));
        }

        /// <summary>
        /// One notification when a value crosses its limit, then silence until it has dropped 5 °C below it
        /// (and at most one per 10 minutes per sensor), so a value hovering at the limit does not spam.
        /// </summary>
        private void Check(string key, string sensor, double limit, string what)
        {
            if (limit <= 0) return;
            double v = App.Hub.Value(sensor);
            if (double.IsNaN(v)) return;
            bool alarmed = _alarmed.TryGetValue(key, out var x) && x;
            if (!alarmed && v >= limit)
            {
                _alarmed[key] = true;
                if (_lastAlert.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(10)) return;
                _lastAlert[key] = DateTime.UtcNow;
                string title = L.T($"טמפרטורה גבוהה: {what}", $"High temperature: {what}");
                string text = L.T($"{v:0}°C (הסף שלך {limit:0}°C). בדוק את המאווררים ואת העומס.", $"{v:0} °C (your limit {limit:0} °C). Check the fans and the load.");
                Log.Warn($"Alert {key}: {v:0} >= {limit:0}");
                _win.Notify(title, text);
            }
            else if (alarmed && v < limit - 5) _alarmed[key] = false;
        }

        // ───────────── per-game profiles ─────────────

        private GameRule? _activeRule;
        private string? _profileBefore;
        private bool _gpuChanged;
        private HashSet<string>? _lastNames;

        private void CheckGames()
        {
            var names = Conflicts.ProcessNames;
            if (ReferenceEquals(names, _lastNames)) return;   // the process list refreshes every 5 s
            _lastNames = names;
            bool enabled;
            List<GameRule> rules;
            lock (App.Config) { enabled = App.Config.GameRulesEnabled; rules = App.Config.GameRules.ToList(); }
            var match = enabled ? rules.FirstOrDefault(r => r.Exe.Length > 0 && names.Contains(Strip(r.Exe))) : null;
            if (match?.Exe == _activeRule?.Exe) return;
            if (_activeRule != null) EndRule();
            if (match != null) StartRule(match);
        }

        public static string Strip(string exe) => exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;

        private void StartRule(GameRule r)
        {
            _activeRule = r;
            lock (App.Config) _profileBefore = App.Config.ActiveProfileId;
            Log.Info($"Game rule: {r.Exe} started (profile {r.ProfileId ?? "-"}, GPU slot {r.GpuSlot?.ToString() ?? "-"})");
            if (r.ProfileId != null && App.Config.Profiles.Any(p => p.Id == r.ProfileId)) _win.SetProfile(r.ProfileId);
            _gpuChanged = false;
            if (r.GpuSlot is int slot and >= 1 and <= 5)
            {
                GpuTune? t;
                lock (App.Config) t = App.Config.Gpu.Slots[slot - 1]?.Clone();
                if (t != null) { _gpuChanged = true; _ = ApplyGpu(t, L.T($"{r.Exe}: פרופיל OC {slot} הוחל", $"{r.Exe}: OC profile {slot} applied")); }
            }
            _win.Notify(L.T("פרופיל משחק", "Game profile"), L.T($"{r.Exe} רץ — ההגדרות שלו הופעלו.", $"{r.Exe} is running — its settings are active."), warn: false);
        }

        private void EndRule()
        {
            var r = _activeRule!;
            _activeRule = null;
            Log.Info($"Game rule: {r.Exe} ended, restoring");
            if (r.ProfileId != null && _profileBefore != null) _win.SetProfile(_profileBefore);
            if (_gpuChanged)
            {
                // back to what the user applied by hand (or stock when nothing was applied)
                GpuTune? t;
                int? pl, temp;
                lock (App.Config) { t = App.Config.Gpu.Applied?.Clone(); pl = App.Config.Gpu.DefaultPowerLimitW; temp = App.Config.Gpu.DefaultTempLimitC; }
                t ??= new GpuTune { PowerLimitW = pl ?? App.Hub.Gpu?.Limits.PowerDefaultW ?? 0, TempLimitC = temp ?? 83 };
                _ = ApplyGpu(t, L.T($"{r.Exe} נסגר — הגדרות כרטיס המסך שוחזרו", $"{r.Exe} closed — graphics card settings restored"));
            }
        }

        private async Task ApplyGpu(GpuTune t, string ok)
        {
            if (App.Hub.Gpu == null || App.Hub.GpuBlocked) return;
            var errs = await App.Hub.ApplyGpuTune(t);
            Log.Info(errs.Count == 0 ? ok : "Game GPU tune: " + string.Join("; ", errs));
        }

        // ───────────── hotkeys ─────────────

        /// <summary>Ctrl+Alt+1…4 cooling profiles · Ctrl+Alt+F1…F5 OC profiles · Ctrl+Alt+O OSD.</summary>
        public static readonly (int id, uint vk, string he, string en)[] Keys =
        {
            (1, 0x31, "Ctrl+Alt+1 · פרופיל שקט", "Ctrl+Alt+1 · Silent profile"),
            (2, 0x32, "Ctrl+Alt+2 · פרופיל מאוזן", "Ctrl+Alt+2 · Balanced profile"),
            (3, 0x33, "Ctrl+Alt+3 · פרופיל ביצועים", "Ctrl+Alt+3 · Performance profile"),
            (4, 0x34, "Ctrl+Alt+4 · פרופיל מקסימום", "Ctrl+Alt+4 · Max profile"),
            (11, 0x70, "Ctrl+Alt+F1…F5 · פרופילי OC 1–5 של כרטיס המסך", "Ctrl+Alt+F1…F5 · graphics card OC profiles 1–5"),
            (12, 0x71, "", ""), (13, 0x72, "", ""), (14, 0x73, "", ""), (15, 0x74, "", ""),
            (20, 0x4F, "Ctrl+Alt+O · הצג / הסתר OSD", "Ctrl+Alt+O · show / hide the OSD"),
        };

        private void OnHotkey(int id)
        {
            string[] profiles = { Defaults.Silent, Defaults.Balanced, Defaults.Performance, Defaults.Max };
            if (id is >= 1 and <= 4) _win.SetProfile(profiles[id - 1]);
            else if (id is >= 11 and <= 15)
            {
                int slot = id - 10;
                GpuTune? t;
                lock (App.Config) t = App.Config.Gpu.Slots[slot - 1]?.Clone();
                if (t == null) { _win.Notify(L.T("פרופיל OC ריק", "Empty OC profile"), L.T($"בחריץ {slot} לא נשמר פרופיל.", $"Nothing is saved in slot {slot}."), warn: false); return; }
                _ = Task.Run(async () =>
                {
                    var errs = await App.Hub.ApplyGpuTune(t);
                    if (errs.Count == 0) lock (App.Config) App.Config.Gpu.Applied = t.Clone();
                    _win.Dispatcher.BeginInvoke(() =>
                    {
                        App.SaveSoon();
                        _win.Notify(L.T("כרטיס מסך", "Graphics card"), errs.Count == 0 ? L.T($"פרופיל OC {slot} הוחל", $"OC profile {slot} applied") : string.Join("\n", errs), warn: errs.Count > 0);
                    });
                });
            }
            else if (id == 20)
            {
                lock (App.Config) App.Config.Osd.Enabled = !App.Config.Osd.Enabled;
                App.SaveSoon();
                ApplySettings();
            }
        }
    }

    /// <summary>System-wide hotkeys through a hidden message-only window (works while the main window is closed).</summary>
    public sealed class Hotkeys : IDisposable
    {
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        private const uint ModAlt = 1, ModCtrl = 2, ModNoRepeat = 0x4000;
        private const int WmHotkey = 0x0312;

        private readonly HwndSource _src;
        private readonly Action<int> _handler;
        private readonly List<int> _registered = new();
        private bool _enabled;

        public Hotkeys(Action<int> handler)
        {
            _handler = handler;
            _src = new HwndSource(new HwndSourceParameters("PulseWatchHotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
            _src.AddHook(Hook);
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (value == _enabled) return;
                _enabled = value;
                if (value)
                {
                    var failed = new List<string>();
                    foreach (var k in Automation.Keys)
                    {
                        if (RegisterHotKey(_src.Handle, k.id, ModCtrl | ModAlt | ModNoRepeat, k.vk)) _registered.Add(k.id);
                        else failed.Add(k.id.ToString());
                    }
                    if (failed.Count > 0) Log.Warn("Hotkeys taken by another program: " + string.Join(",", failed));
                }
                else
                {
                    foreach (int id in _registered) UnregisterHotKey(_src.Handle, id);
                    _registered.Clear();
                }
            }
        }

        private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmHotkey) { handled = true; _handler(wParam.ToInt32()); }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            Enabled = false;
            _src.RemoveHook(Hook);
            _src.Dispose();
        }
    }
}
