using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PulseWatch.Core;

namespace PulseWatch
{
    public partial class App : Application
    {
        private const string MutexName = @"Global\PulseWatchControlCenter";
        private const string ShowEventName = @"Global\PulseWatchControlCenter.Show";

        private Mutex? _mutex;
        private EventWaitHandle? _showEvent;
        private DispatcherTimer? _saveTimer;

        public static AppConfig Config { get; private set; } = new();
        public static SensorHub Hub { get; private set; } = null!;
        public static bool StartedInTray { get; private set; }

        /// <summary>Debounced config save (UI edits arrive in bursts while dragging).</summary>
        public static void SaveSoon()
        {
            var app = (App)Current;
            app._saveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(800), DispatcherPriority.Background, (_, _) =>
            {
                app._saveTimer!.Stop();
                lock (Config) ConfigStore.Save(Config);
            }, app.Dispatcher);
            app._saveTimer.Stop();
            app._saveTimer.Start();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, MutexName, out bool first);
            if (!first)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

            DispatcherUnhandledException += (_, a) => { Log.Error("UI exception", a.Exception); a.Handled = true; };
            AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Fatal", a.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Task exception", a.Exception); a.SetObserved(); };

            base.OnStartup(e);
            Log.Info("──── PulseWatch 2.0 starting ────");
            Config = ConfigStore.Load();
            int lang = Array.IndexOf(e.Args, "--lang");
            if (lang >= 0 && lang + 1 < e.Args.Length) L.Set(e.Args[lang + 1]); // override for this run only
            NvidiaSettings.Relocalize();
            StartedInTray = e.Args.Contains("--tray") || Config.StartMinimized;

            if (Config.CloseConflictsOnLaunch)
            {
                Conflicts.Refresh(Config);
                foreach (var app in Conflicts.Apps.Where(a => a.Running))
                    Log.Info(Conflicts.Close(app, includeOptionalServices: false));
            }

            Hub = new SensorHub(Config);
            Hub.Start();

            var win = new MainWindow();
            MainWindow = win;
            if (!StartedInTray) win.Show();

            new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    Dispatcher.BeginInvoke(() => win.ShowFromTray());
            }) { IsBackground = true, Name = "ShowSignal" }.Start();

            _ = StartupApplyAsync();

            int shots = Array.IndexOf(e.Args, "--shots");
            if (shots >= 0 && shots + 1 < e.Args.Length)
            {
                string dir = e.Args[shots + 1];
                win.Show();
                Dispatcher.BeginInvoke(async () =>
                {
                    try { await win.CaptureAll(dir); }
                    catch (Exception ex) { Log.Error("Capture", ex); }
                    win.ExitApp();
                });
            }
        }

        /// <summary>
        /// Switches the UI language. Sensor and channel names are created when the hardware is opened,
        /// so the cleanest switch is a quick restart (fans are handed back and retaken within seconds).
        /// </summary>
        public static void RestartWithLanguage(string code)
        {
            lock (Config) Config.Language = code;
            lock (Config) ConfigStore.Save(Config);
            var app = (App)Current;
            try { Hub.Dispose(); } catch { }
            try { app._mutex?.ReleaseMutex(); app._mutex?.Dispose(); app._mutex = null; } catch { }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.ExePath) { UseShellExecute = false });
            Instance?.ExitApp();
        }

        private static PulseWatch.MainWindow? Instance => PulseWatch.MainWindow.Instance;

        private static async Task StartupApplyAsync()
        {
            while (!Hub.Ready) await Task.Delay(200);
            try
            {
                GpuTune? tune;
                bool apply;
                lock (Config) { tune = Config.Gpu.Applied?.Clone(); apply = Config.Gpu.ApplyOnStartup; }
                if (apply && tune != null && Hub.Gpu != null && !Hub.GpuBlocked)
                {
                    var errs = await Hub.ApplyGpuTune(tune);
                    Log.Info(errs.Count == 0 ? "Startup GPU tune applied" : "Startup GPU tune: " + string.Join("; ", errs));
                }
            }
            catch (Exception ex) { Log.Error("Startup GPU apply", ex); }

            await Task.Run(() =>
            {
                try
                {
                    System.Collections.Generic.Dictionary<string, int> vib;
                    lock (Config) vib = new(Config.Vibrance);
                    foreach (var (dev, level) in vib) NvidiaSettings.SetVibrance(dev, level);
                }
                catch (Exception ex) { Log.Warn("Startup vibrance: " + ex.Message); }
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { Hub?.Dispose(); } catch (Exception ex) { Log.Error("Hub dispose", ex); }
            lock (Config) ConfigStore.Save(Config);
            Log.Info("──── PulseWatch exit ────");
            try { _mutex?.ReleaseMutex(); } catch { }
            base.OnExit(e);
        }
    }
}
