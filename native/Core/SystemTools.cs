using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace PulseWatch.Core
{
    /// <summary>Vendor apps that drive the same hardware. Running both makes them fight over every fan.</summary>
    public sealed class ConflictApp
    {
        public string Key { get; init; } = "";
        public string Title { get; init; } = "";
        public string AffectsHe { get; init; } = "";
        public string AffectsEn { get; init; } = "";
        public string Affects => L.T(AffectsHe, AffectsEn);
        public string[] Processes { get; init; } = Array.Empty<string>();
        /// <summary>Processes whose presence means the app is actively controlling hardware.</summary>
        public string[] ControlProcesses { get; init; } = Array.Empty<string>();
        public string[] Services { get; init; } = Array.Empty<string>();
        public string[] OptionalServices { get; init; } = Array.Empty<string>();
        public string DefaultExe { get; init; } = "";
        public bool Running { get; internal set; }
        public string? ExePath { get; internal set; }
    }

    public static class Conflicts
    {
        /// <summary>Names of all running processes from the last <see cref="Refresh"/> (every 5 s); used by per-game profiles.</summary>
        public static volatile HashSet<string> ProcessNames = new(StringComparer.OrdinalIgnoreCase);

        public static readonly ConflictApp[] Apps =
        {
            new()
            {
                Key = "afterburner", Title = "MSI Afterburner", AffectsHe = "מאווררי כרטיס המסך, Power Limit ו-OC", AffectsEn = "the GPU fans, power limit and overclock",
                Processes = new[] { "MSIAfterburner" }, ControlProcesses = new[] { "MSIAfterburner" },
                DefaultExe = @"C:\Program Files (x86)\MSI Afterburner\MSIAfterburner.exe"
            },
            new()
            {
                Key = "lconnect", Title = "Lian Li L-Connect 3", AffectsHe = "משאבה, מאווררי רדיאטור ותאורת ה-Galahad II", AffectsEn = "the Galahad II pump, radiator fans and lighting",
                Processes = new[] { "L-Connect 3", "L-Connect-Service", "L-Connect-Service-Watcher" },
                ControlProcesses = new[] { "L-Connect 3", "L-Connect-Service" },
                Services = new[] { "LConnectServiceWatcher", "LConnectService" },
                DefaultExe = @"C:\Program Files\Lian-Li\L-Connect 3\L-Connect 3.exe"
            },
            new()
            {
                Key = "gcc", Title = "GIGABYTE Control Center", AffectsHe = "מאווררי לוח האם (Smart Fan)", AffectsEn = "the motherboard fans (Smart Fan)",
                Processes = new[] { "GCC" }, ControlProcesses = new[] { "GCC" },
                OptionalServices = new[] { "EasyTuneEngineService" },
                DefaultExe = @"C:\Program Files\GIGABYTE\Control Center\GCC.exe"
            }
        };

        public static ConflictApp Get(string key) => Apps.First(a => a.Key == key);

        /// <summary>One process snapshot for all apps (GetProcessesByName enumerates every process on each call).</summary>
        public static void Refresh(AppConfig cfg)
        {
            var procs = Process.GetProcesses();
            ProcessNames = new HashSet<string>(procs.Select(p => p.ProcessName), StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var app in Apps)
                {
                    var hits = procs.Where(p => app.ControlProcesses.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)).ToList();
                    app.Running = hits.Count > 0;
                    if (app.Running && app.ExePath == null)
                    {
                        // reading MainModule is expensive — only until the path is known
                        var main = hits.FirstOrDefault(p => string.Equals(p.ProcessName, app.Processes[0], StringComparison.OrdinalIgnoreCase));
                        try
                        {
                            string? path = main?.MainModule?.FileName;
                            if (!string.IsNullOrEmpty(path))
                            {
                                app.ExePath = path;
                                lock (cfg) cfg.ConflictAppPaths[app.Key] = path;
                            }
                        }
                        catch { }
                    }
                    if (app.ExePath == null)
                    {
                        lock (cfg) app.ExePath = cfg.ConflictAppPaths.TryGetValue(app.Key, out var saved) ? saved : null;
                    }
                }
            }
            finally
            {
                foreach (var p in procs) p.Dispose();
            }
        }

        /// <summary>Stops the app's services and processes so PulseWatch owns the hardware.</summary>
        public static string Close(ConflictApp app, bool includeOptionalServices)
        {
            var services = includeOptionalServices ? app.Services.Concat(app.OptionalServices) : app.Services;
            foreach (var svc in services) RunSc("stop", svc);
            var deadline = DateTime.UtcNow.AddSeconds(6);
            while (DateTime.UtcNow < deadline)
            {
                bool any = false;
                foreach (var name in app.Processes)
                {
                    foreach (var p in Process.GetProcessesByName(name))
                    {
                        any = true;
                        try { p.Kill(true); } catch { }
                        p.Dispose();
                    }
                }
                if (!any) break;
                Thread.Sleep(400);
            }
            bool still = app.Processes.Any(n => Process.GetProcessesByName(n).Length > 0);
            Log.Info($"Closed conflict app {app.Key}, still running: {still}");
            return still
                ? L.T($"{app.Title}: חלק מהתהליכים עדיין רצים", $"{app.Title}: some processes are still running")
                : L.T($"{app.Title} נסגר — PulseWatch שולט עכשיו ב{app.Affects}", $"{app.Title} closed — PulseWatch now controls {app.Affects}");
        }

        /// <summary>Gives control back: restarts services and launches the vendor app again.</summary>
        public static string Restore(ConflictApp app)
        {
            foreach (var svc in app.OptionalServices.Reverse().Concat(app.Services.Reverse())) RunSc("start", svc);
            string exe = app.ExePath ?? app.DefaultExe;
            if (File.Exists(exe))
            {
                try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true }); }
                catch (Exception ex) { return L.T($"לא ניתן להפעיל את {app.Title}: {ex.Message}", $"Could not start {app.Title}: {ex.Message}"); }
            }
            return L.T($"{app.Title} הופעל מחדש. השליטה ב{app.Affects} עברה אליו.", $"{app.Title} restarted — it now controls {app.Affects}.");
        }

        private static void RunSc(string verb, string service)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("sc.exe", $"{verb} \"{service}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
                p?.WaitForExit(4000);
            }
            catch (Exception ex) { Log.Warn($"sc {verb} {service}: {ex.Message}"); }
        }
    }

    public static class StartupTask
    {
        private const string TaskName = "PulseWatch Control Center";

        public static bool IsEnabled()
        {
            var (code, _) = Run("schtasks.exe", $"/Query /TN \"{TaskName}\"");
            return code == 0;
        }

        /// <summary>Logon task with highest privileges — starts elevated without a UAC prompt.</summary>
        public static string? Enable()
        {
            var (code, output) = Run("schtasks.exe", $"/Create /TN \"{TaskName}\" /TR \"\\\"{AppPaths.ExePath}\\\" --tray\" /SC ONLOGON /RL HIGHEST /DELAY 0000:15 /F");
            return code == 0 ? null : output;
        }

        public static string? Disable()
        {
            var (code, output) = Run("schtasks.exe", $"/Delete /TN \"{TaskName}\" /F");
            return code == 0 ? null : output;
        }

        internal static (int code, string output) Run(string exe, string args)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(exe, args)
                {
                    CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
                })!;
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(8000);
                return (p.ExitCode, o.Trim());
            }
            catch (Exception ex) { return (-1, ex.Message); }
        }
    }

    public sealed class PowerPlan
    {
        public string Guid { get; init; } = "";
        public string Name { get; init; } = "";
        public bool Active { get; init; }
        public override string ToString() => Name;
    }

    public static class PowerPlans
    {
        public static List<PowerPlan> List()
        {
            var list = new List<PowerPlan>();
            var (_, output) = StartupTask.Run("powercfg.exe", "/list");
            foreach (Match m in Regex.Matches(output, @"([0-9a-fA-F\-]{36})\s+\((.+?)\)\s*(\*)?"))
                list.Add(new PowerPlan { Guid = m.Groups[1].Value, Name = m.Groups[2].Value, Active = m.Groups[3].Success });
            return list;
        }

        public static bool SetActive(string guid) => StartupTask.Run("powercfg.exe", $"/setactive {guid}").code == 0;
    }
}
