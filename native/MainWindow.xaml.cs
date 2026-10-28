using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PulseWatch.Core;
using PulseWatch.Views;
using WinForms = System.Windows.Forms;

namespace PulseWatch
{
    public interface IView
    {
        void Refresh();
        void OnShown();
    }

    public partial class MainWindow : Window
    {
        private readonly Dictionary<string, Func<UserControl>> _factories;
        private readonly Dictionary<string, UserControl> _views = new();
        private IView? _current;
        private string _currentNav = "NavDashboard";
        private bool _renderPending;
        private bool _exiting;
        private int _frame;
        private WinForms.NotifyIcon? _tray;
        private readonly DispatcherTimer _toastTimer;
        private readonly DispatcherTimer _releaseTimer;
        private string _conflictSig = "";

        public static MainWindow? Instance { get; private set; }

        [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr hProcess);

        public MainWindow()
        {
            InitializeComponent();
            Instance = this;
            FitToScreen();
            _factories = new()
            {
                ["NavDashboard"] = () => new DashboardView(),
                ["NavCooling"] = () => new CoolingView(),
                ["NavGpu"] = () => new GpuView(),
                ["NavNvidia"] = () => new NvidiaView(),
                ["NavLighting"] = () => new LightingView(),
                ["NavSensors"] = () => new SensorsView(),
                ["NavAutomation"] = () => new AutomationView(),
                ["NavSystem"] = () => new SystemView(),
            };
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
            // after a while in the tray, drop every page so WPF can release its visuals and bitmaps
            _releaseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _releaseTimer.Tick += (_, _) => { _releaseTimer.Stop(); ReleaseViews(); };

            App.Hub.Updated += OnHubUpdated;
            App.Hub.Notice += msg => Dispatcher.BeginInvoke(() => ShowToast(msg, msg.Contains("⚠") ? ToastKind.Warn : ToastKind.Info));

            bool admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            AdminDot.Fill = (Brush)FindResource(admin ? "Good" : "Hot");
            AdminText.Text = admin ? L.T("מנהל מערכת · שליטה מלאה", "Administrator · full control") : L.T("ללא הרשאות מנהל — קריאה בלבד", "Not elevated — read only");
            HubStatus.Text = App.Hub.StatusText;

            SyncProfileBar();
            Navigate("NavDashboard");
            SetupTray();
            Automation = new Automation(this);
            StateChanged += (_, _) =>
            {
                MaxBtn.Content = WindowState == WindowState.Maximized ? "" : "";
                Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
                UpdateActivity();
            };
            IsVisibleChanged += (_, _) => UpdateActivity();
            UpdateActivity();
        }

        /// <summary>
        /// With display scaling the default size can be taller than the screen, which pushes the title bar
        /// (minimize / maximize / close) off the top. Always fit the work area; when maximized, stay off the taskbar.
        /// </summary>
        private void FitToScreen()
        {
            var wa = SystemParameters.WorkArea;
            Width = Math.Min(Width, wa.Width * 0.94);
            Height = Math.Min(Height, wa.Height * 0.94);
            MinWidth = Math.Min(MinWidth, wa.Width);
            MinHeight = Math.Min(MinHeight, wa.Height);
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + (wa.Height - Height) / 2;
            WindowStartupLocation = WindowStartupLocation.Manual;
            MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
        }

        // ───────────── activity: sample less and free memory while nobody is looking ─────────────

        private void UpdateActivity()
        {
            bool active = IsVisible && WindowState != WindowState.Minimized;
            App.Hub.UiActive = active;
            if (active)
            {
                _releaseTimer.Stop();
                if (_current == null) Navigate(_currentNav);
                App.Hub.Poke();
            }
            else _releaseTimer.Start();
        }

        private void ReleaseViews()
        {
            if (IsVisible && WindowState != WindowState.Minimized) return;
            Host.Content = null;
            _views.Clear();
            _current = null;
            App.Hub.WantAllSensors = false;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            try { EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
            Log.Info("Window hidden — pages released");
        }

        // ───────────── navigation ─────────────

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && _factories != null) Navigate(rb.Name);
        }

        public void Navigate(string navName, Action<UserControl>? configure = null)
        {
            _currentNav = navName;
            if (!_views.TryGetValue(navName, out var view))
            {
                view = _factories[navName]();
                _views[navName] = view;
            }
            if (FindName(navName) is RadioButton rb && rb.IsChecked != true) rb.IsChecked = true;
            PageTitle.Text = (FindName(navName) as RadioButton)?.Content as string ?? "";
            Host.Content = view;
            _current = view as IView;
            App.Hub.WantAllSensors = navName is "NavSensors" or "NavSystem";
            configure?.Invoke(view);
            _current?.OnShown();
            _current?.Refresh();
        }

        // ───────────── live refresh ─────────────

        private void OnHubUpdated()
        {
            if (_renderPending) return;
            _renderPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Render));
        }

        private double _renderMs;
        private int _renderCount;

        private void Render()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { RenderCore(); }
            finally
            {
                _renderMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (++_renderCount >= 60)
                {
                    Log.Info($"PERF-UI view={_currentNav} visible={IsVisible} avg refresh {_renderMs / _renderCount:0.00} ms");
                    _renderMs = 0; _renderCount = 0;
                }
            }
        }

        /// <summary>Alerts, per-game profiles, hotkeys and OSD (run every update, also while in the tray).</summary>
        public Automation? Automation { get; private set; }

        /// <summary>A Windows notification from the tray icon (shown even while the window is closed).</summary>
        public void Notify(string title, string text, bool warn = true)
        {
            try { _tray?.ShowBalloonTip(8000, title, text, warn ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info); }
            catch (Exception ex) { Log.Warn("Notify: " + ex.Message); }
        }

        private void RenderCore()
        {
            _renderPending = false;
            _frame++;
            try { Automation?.Tick(); } catch (Exception ex) { Log.Warn("Automation: " + ex.Message); }
            var hub = App.Hub;
            float cpu = hub.Value("@cpu.temp"), gpu = hub.Value("@gpu.temp");

            if (IsVisible && WindowState != WindowState.Minimized)
            {
                TopCpu.Text = float.IsNaN(cpu) ? "—" : $"{cpu:0}°";
                TopGpu.Text = float.IsNaN(gpu) ? "—" : $"{gpu:0}°";
                PaintPill(TopCpuPill, TopCpuLbl, TopCpu, cpu, 75, 88);
                PaintPill(TopGpuPill, TopGpuLbl, TopGpu, gpu, 75, 84);
                if (_frame % 5 == 1) HubStatus.Text = hub.StatusText.Replace("   •   ", "\n");
                EmergencyBanner.Visibility = hub.Emergency ? Visibility.Visible : Visibility.Collapsed;
                if (hub.Emergency) EmergencyText.Text = hub.EmergencyText;

                var running = Conflicts.Apps.Where(a => a.Running).ToList();
                string sig = string.Join(",", running.Select(a => a.Key));
                if (sig != _conflictSig)
                {
                    _conflictSig = sig;
                    ConflictBanners.ItemsSource = running;
                }

                try { _current?.Refresh(); }
                catch (Exception ex) { Log.Error("View refresh", ex); }
            }

            if (_tray != null && _frame % 3 == 0)
            {
                string tip = $"PulseWatch · {App.Config.ActiveProfile.DisplayName}\nCPU {Fmt(cpu)}°  GPU {Fmt(gpu)}°";
                _tray.Text = tip.Length > 127 ? tip[..127] : tip;
            }
        }

        private static string Fmt(float v) => float.IsNaN(v) ? "—" : v.ToString("0");

        /// <summary>Top-bar pill: green / amber / red text, tinted background and outline by temperature.</summary>
        private static void PaintPill(Border pill, TextBlock label, TextBlock value, double v, double warn, double hot)
        {
            string key = double.IsNaN(v) ? "" : v >= hot ? "Hot" : v >= warn ? "Warn" : "Good";
            if (pill.Tag as string == key) return;
            pill.Tag = key;
            var app = Application.Current;
            if (key == "")
            {
                pill.Background = (Brush)app.FindResource("CardHi");
                pill.BorderBrush = Brushes.Transparent;
                value.Foreground = label.Foreground = (Brush)app.FindResource("Muted");
                return;
            }
            var fg = (Brush)app.FindResource(key);
            pill.Background = (Brush)app.FindResource(key + "Soft");
            var c = ((SolidColorBrush)fg).Color;
            pill.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B));
            value.Foreground = fg;
            label.Foreground = new SolidColorBrush(Color.FromArgb(0xCC, c.R, c.G, c.B));
        }

        public static Brush TempBrush(double v, double warn, double hot)
        {
            if (double.IsNaN(v)) return (Brush)Application.Current.FindResource("Muted");
            return (Brush)Application.Current.FindResource(v >= hot ? "Hot" : v >= warn ? "Warn" : "Text");
        }

        // ───────────── profiles ─────────────

        private bool _syncingProfile;

        public void SyncProfileBar()
        {
            _syncingProfile = true;
            foreach (var rb in ProfileBar.Children.OfType<RadioButton>())
                rb.IsChecked = (string)rb.Tag == App.Config.ActiveProfileId;
            _syncingProfile = false;
        }

        private void Profile_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingProfile || sender is not RadioButton rb) return;
            SetProfile((string)rb.Tag);
        }

        public void SetProfile(string id)
        {
            lock (App.Config)
            {
                if (App.Config.ActiveProfileId == id) return;
                App.Config.ActiveProfileId = id;
            }
            App.SaveSoon();
            App.Hub.ReapplyNow();
            SyncProfileBar();
            ShowToast(L.T($"פרופיל \"{App.Config.ActiveProfile.DisplayName}\" הופעל — כל המאווררים עודכנו", $"\"{App.Config.ActiveProfile.DisplayName}\" profile active — all fans updated"));
            _current?.OnShown();
            _current?.Refresh();
            RebuildTrayMenu();
        }

        // ───────────── conflicts ─────────────

        private async void TakeOver_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string key) return;
            b.IsEnabled = false;
            var app = Conflicts.Get(key);
            string msg = await Task.Run(() => Conflicts.Close(app, includeOptionalServices: false));
            Conflicts.Refresh(App.Config);
            _conflictSig = "?";
            ShowToast(msg);
            App.Hub.Poke();
        }

        // ───────────── toast ─────────────

        public enum ToastKind { Info, Warn, Error }

        public void ShowToast(string text, ToastKind kind = ToastKind.Info)
        {
            ToastText.Text = text;
            ToastIcon.Text = kind == ToastKind.Info ? "" : "";
            ToastIcon.Foreground = (Brush)FindResource(kind == ToastKind.Info ? "Good" : kind == ToastKind.Warn ? "Warn" : "Hot");
            Toast.Visibility = Visibility.Visible;
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(kind == ToastKind.Info ? 4 : 8);
            _toastTimer.Start();
        }

        // ───────────── debug: render every page to PNG (--shots <dir>) ─────────────

        public async Task CaptureAll(string dir)
        {
            System.IO.Directory.CreateDirectory(dir);
            while (!App.Hub.Ready) await Task.Delay(200);
            await Task.Delay(5000);
            foreach (var nav in _factories.Keys.ToList())
            {
                try { await CaptureOne(dir, nav); }
                catch (Exception ex) { Log.Error("Capture " + nav, ex); }
            }
        }

        private async Task CaptureOne(string dir, string nav)
        {
            Navigate(nav);
            for (int i = 0; i < 4; i++) { await Task.Delay(700); Render(); }
            UpdateLayout();
            var src = PresentationSource.FromVisual(this);
            double sx = src?.CompositionTarget?.TransformToDevice.M11 ?? 1, sy = src?.CompositionTarget?.TransformToDevice.M22 ?? 1;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(Root.ActualWidth * sx), (int)(Root.ActualHeight * sy), 96 * sx, 96 * sy, PixelFormats.Pbgra32);
            var bg = new DrawingVisual();
            using (var dc = bg.RenderOpen()) dc.DrawRectangle((Brush)FindResource("Bg"), null, new Rect(0, 0, Root.ActualWidth, Root.ActualHeight));
            bmp.Render(bg);
            bmp.Render(Root);
            // RenderTargetBitmap ignores the RTL mirror transform of the parent window — flip it back
            System.Windows.Media.Imaging.BitmapSource img = L.En ? bmp : new System.Windows.Media.Imaging.TransformedBitmap(bmp, new ScaleTransform(-1, 1));
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
            using (var fs = System.IO.File.Create(System.IO.Path.Combine(dir, nav + ".png"))) enc.Save(fs);

            // full-length page (content below the fold)
            if (Host.Content is FrameworkElement page && page is UserControl uc && uc.Content is ScrollViewer sv && sv.Content is FrameworkElement body && body.ActualHeight > sv.ViewportHeight + 10)
            {
                var full = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(body.ActualWidth * sx), (int)(body.ActualHeight * sy), 96 * sx, 96 * sy, PixelFormats.Pbgra32);
                var bg2 = new DrawingVisual();
                using (var dc = bg2.RenderOpen()) dc.DrawRectangle((Brush)FindResource("Bg"), null, new Rect(0, 0, body.ActualWidth, body.ActualHeight));
                full.Render(bg2);
                full.Render(body);
                System.Windows.Media.Imaging.BitmapSource img2 = L.En ? full : new System.Windows.Media.Imaging.TransformedBitmap(full, new ScaleTransform(-1, 1));
                var enc2 = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc2.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img2));
                using var fs2 = System.IO.File.Create(System.IO.Path.Combine(dir, nav + "_full.png"));
                enc2.Save(fs2);
            }
        }

        // ───────────── window chrome & tray ─────────────

        private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_exiting && App.Config.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnClosing(e);
            if (!_exiting) ExitApp();
        }

        public void ShowFromTray()
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Topmost = true; Topmost = false;
            UpdateActivity();
        }

        public void ExitApp()
        {
            _exiting = true;
            try { Automation?.Dispose(); } catch { }
            try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
            Close();
            Application.Current.Shutdown();
        }

        private void SetupTray()
        {
            try
            {
                var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/pulsewatch.ico"));
                _tray = new WinForms.NotifyIcon
                {
                    Icon = res != null ? new System.Drawing.Icon(res.Stream) : System.Drawing.SystemIcons.Application,
                    Text = "PulseWatch",
                    Visible = true
                };
                _tray.MouseClick += (_, a) => { if (a.Button == WinForms.MouseButtons.Left) Dispatcher.BeginInvoke(ShowFromTray); };
                RebuildTrayMenu();
            }
            catch (Exception ex) { Log.Error("Tray setup", ex); }
        }

        private void RebuildTrayMenu()
        {
            if (_tray == null) return;
            var menu = new WinForms.ContextMenuStrip { RightToLeft = L.En ? WinForms.RightToLeft.No : WinForms.RightToLeft.Yes, ShowImageMargin = false };
            menu.Items.Add(L.T("פתח את PulseWatch", "Open PulseWatch"), null, (_, _) => Dispatcher.BeginInvoke(ShowFromTray));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            foreach (var p in App.Config.Profiles)
            {
                var item = new WinForms.ToolStripMenuItem(L.T("פרופיל: ", "Profile: ") + p.DisplayName) { Checked = p.Id == App.Config.ActiveProfileId };
                string id = p.Id;
                item.Click += (_, _) => Dispatcher.BeginInvoke(() => SetProfile(id));
                menu.Items.Add(item);
            }
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(L.T("יציאה (מחזיר מאווררים לאוטומטי)", "Exit (fans back to automatic)"), null, (_, _) => Dispatcher.BeginInvoke(ExitApp));
            var old = _tray.ContextMenuStrip;
            _tray.ContextMenuStrip = menu;
            old?.Dispose();
        }
    }
}
