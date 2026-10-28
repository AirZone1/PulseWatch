using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PulseWatch.Core;

namespace PulseWatch.Views
{
    public partial class SystemView : UserControl, IView
    {
        private bool _loading;
        private bool _specsLoaded;
        private string _conflictSig = "";
        private int _tick;

        public SystemView() { InitializeComponent(); }

        public async void OnShown()
        {
            _loading = true;
            lock (App.Config)
            {
                AutoClose.IsChecked = App.Config.CloseConflictsOnLaunch;
                StartMin.IsChecked = App.Config.StartMinimized;
                CloseToTray.IsChecked = App.Config.MinimizeToTray;
                PollCombo.SelectedItem = PollCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == App.Config.PollMs.ToString()) ?? PollCombo.Items[1];
                HddCombo.SelectedItem = HddCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == App.Config.HddPollSeconds.ToString()) ?? HddCombo.Items[1];
                HddSleep.IsChecked = App.Config.HddKeepAsleep;
                NvmeCombo.SelectedItem = NvmeCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == App.Config.NvmePollSeconds.ToString()) ?? NvmeCombo.Items[1];
                SafeCpu.Maximum = App.Hub.HwCpuLimitC;
                SafeGpu.Maximum = App.Hub.HwGpuLimitC;
                SafeCpu.Value = App.Config.SafetyCpuC;
                SafeGpu.Value = App.Config.SafetyGpuC;
                ExitFan.Value = App.Config.LianLiExitFanPct;
            }
            LangHe.IsChecked = !L.En;
            LangEn.IsChecked = L.En;
            UpdateLabels();
            _loading = false;

            About.Text = L.T(
                "PulseWatch 2.0 · מרכז שליטה מאוחד לקירור, כרטיס מסך ותאורה.\n" +
                "מבוסס על: LibreHardwareMonitor + דרייבר PawnIO (חיישני לוח אם ו-CPU), NVIDIA NVML ו-NvAPI (כוונון, עקומת מתח והגדרות דרייבר), " +
                "HidSharp ופרוטוקול ה-Galahad II מפרויקט הקוד הפתוח lian-li-linux.\n" + $"הגדרות: {AppPaths.DataDir}",
                "PulseWatch 2.0 · one control center for cooling, graphics card and lighting.\n" +
                "Built on LibreHardwareMonitor + the PawnIO driver (motherboard and CPU sensors), NVIDIA NVML and NvAPI (tuning, V/F curve, driver settings), " +
                "HidSharp and the Galahad II protocol from the open-source lian-li-linux project.\n" + $"Settings: {AppPaths.DataDir}");

            bool startup = await Task.Run(StartupTask.IsEnabled);
            var plans = await Task.Run(PowerPlans.List);
            _loading = true;
            StartWithWindows.IsChecked = startup;
            PowerCombo.ItemsSource = plans;
            PowerCombo.SelectedItem = plans.FirstOrDefault(p => p.Active);
            _loading = false;

            _conflictSig = "";
            BuildConflicts();
            if (!_specsLoaded) { _specsLoaded = true; await LoadSpecs(); }
            BuildDrives();
        }

        public void Refresh()
        {
            if (++_tick % 3 == 0) BuildConflicts();
            if (_tick % 10 == 0) BuildDrives();
        }

        private void Lang_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton rb) return;
            string code = (string)rb.Tag;
            if (code == L.Code) return;
            MainWindow.Instance?.ShowToast(code == "en" ? "Switching to English — PulseWatch restarts…" : "עובר לעברית — PulseWatch מופעל מחדש…");
            Dispatcher.BeginInvoke(new Action(() => App.RestartWithLanguage(code)), System.Windows.Threading.DispatcherPriority.Background);
        }

        // ───────────── specs ─────────────

        private static string Wmi(string cls, string prop, string? where = null)
        {
            try
            {
                using var s = new ManagementObjectSearcher($"SELECT {prop} FROM {cls}" + (where != null ? " WHERE " + where : ""));
                foreach (var o in s.Get()) return o[prop]?.ToString()?.Trim() ?? "";
            }
            catch { }
            return "";
        }

        private async Task LoadSpecs()
        {
            var rows = await Task.Run(() =>
            {
                var list = new List<(string k, string v)>();
                string cpu = Wmi("Win32_Processor", "Name");
                string cores = Wmi("Win32_Processor", "NumberOfCores");
                list.Add((L.T("מעבד", "Processor"), $"{cpu}\n" + L.T($"{cores} ליבות · {Environment.ProcessorCount} תהליכונים", $"{cores} cores · {Environment.ProcessorCount} threads")));
                list.Add((L.T("לוח אם", "Motherboard"), $"{Wmi("Win32_BaseBoard", "Manufacturer")} {Wmi("Win32_BaseBoard", "Product")}\nBIOS {Wmi("Win32_BIOS", "SMBIOSBIOSVersion")}"));
                try
                {
                    using var s = new ManagementObjectSearcher("SELECT Capacity, ConfiguredClockSpeed, Manufacturer, PartNumber FROM Win32_PhysicalMemory");
                    var sticks = s.Get().Cast<ManagementObject>().ToList();
                    double gb = sticks.Sum(m => Convert.ToDouble(m["Capacity"] ?? 0)) / 1073741824.0;
                    var first = sticks.FirstOrDefault();
                    list.Add((L.T("זיכרון", "Memory"), $"{gb:0} GB · {sticks.Count} {L.T("מקלות", "sticks")} · {first?["ConfiguredClockSpeed"]} MT/s\n{first?["Manufacturer"]?.ToString()?.Trim()} {first?["PartNumber"]?.ToString()?.Trim()}"));
                }
                catch { }
                var gpu = App.Hub.Gpu;
                if (gpu != null) list.Add((L.T("כרטיס מסך", "Graphics card"), $"{gpu.Name}\n{L.T("דרייבר", "Driver")} {gpu.Driver} · {(App.Hub.GpuLive?.VramTotalMb ?? 0) / 1024:0} GB VRAM"));
                if (App.Hub.Aio != null) list.Add((L.T("קירור נוזלי", "Liquid cooler"), $"Lian Li {App.Hub.Aio.Model}\n{L.T("קושחה", "Firmware")} {App.Hub.Aio.Firmware}"));
                if (App.Hub.SuperIoChip != null) list.Add((L.T("בקר מאווררים", "Fan controller"), $"{App.Hub.SuperIoChip} · {App.Hub.Channels.Count(c => c.Kind is ChannelKind.Motherboard or ChannelKind.NvmeFan)} {L.T("ערוצי PWM", "PWM channels")}"));
                list.Add((L.T("מערכת הפעלה", "Operating system"), $"{Wmi("Win32_OperatingSystem", "Caption")}\nBuild {Environment.OSVersion.Version.Build}"));
                return list;
            });
            Specs.Children.Clear();
            foreach (var (k, v) in rows) Specs.Children.Add(Row(k, v));
        }

        private FrameworkElement Row(string key, string value)
        {
            var g = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = key, Style = (Style)FindResource("Caption") });
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19 };
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            return g;
        }

        private void BuildDrives()
        {
            Drives.Children.Clear();
            var all = App.Hub.AllSensors.Where(s => s.HardwareKind == "Storage" && !s.Id.StartsWith("virt/")).ToList();
            foreach (var hw in all.GroupBy(s => s.Hardware))
            {
                SensorInfo? Get(Func<SensorInfo, bool> f) => hw.FirstOrDefault(f);
                var temp = Get(s => s.Type == "Temperature" && (s.Name.Contains("Composite") || s.Name == "Temperature"));
                var life = Get(s => s.Type == "Level" && s.Name == "Life");
                var hours = Get(s => s.Name == "Power On Hours");
                var written = Get(s => s.Type == "Data" && s.Name == "Data Written");
                var total = Get(s => s.Name == "Total Space");
                var used = Get(s => s.Name == "Used Space");

                var parts = new List<string>();
                bool asleep = App.Hub.SleepingDrives.Keys.Any(k => hw.Any(s => s.Id.StartsWith(k + "/")));
                if (asleep) parts.Add(L.T("💤 במצב שינה — מוצג הערך האחרון", "💤 asleep — showing the last reading"));
                if (total?.HasValue == true) parts.Add($"{total.Value / 1000:0.#} TB");
                if (used?.HasValue == true) parts.Add(L.T($"{used.Value:0}% בשימוש", $"{used.Value:0}% used"));
                if (hours?.HasValue == true) parts.Add(L.T($"{hours.Value:0} שעות פעולה", $"{hours.Value:0} power-on hours"));
                if (written?.HasValue == true) parts.Add(L.T($"{written.Value / 1000:0.0} TB נכתבו", $"{written.Value / 1000:0.0} TB written"));

                var card = new Border { Style = (Style)FindResource("SubCard"), Margin = new Thickness(0, 8, 0, 0) };
                var dp = new DockPanel();
                var right = new StackPanel { Orientation = Orientation.Horizontal };
                DockPanel.SetDock(right, Dock.Right);
                if (life?.HasValue == true)
                    right.Children.Add(new TextBlock { Text = L.T($"בריאות {life.Value:0}%", $"Health {life.Value:0}%"), Foreground = Ui.Res(life.Value >= 80 ? "Good" : life.Value >= 50 ? "Warn" : "Hot"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center });
                right.Children.Add(new TextBlock { Text = temp?.HasValue == true ? $"{temp.Value:0}°C" : "—", Style = (Style)FindResource("Num"), Foreground = MainWindow.TempBrush(temp?.Value ?? float.NaN, 55, 65), VerticalAlignment = VerticalAlignment.Center });
                dp.Children.Add(right);
                var left = new StackPanel();
                left.Children.Add(new TextBlock { Text = hw.Key.Trim(), FontWeight = FontWeights.SemiBold, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Left });
                left.Children.Add(new TextBlock { Text = string.Join("  ·  ", parts), Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap });
                dp.Children.Add(left);
                card.Child = dp;
                Drives.Children.Add(card);
            }
        }

        // ───────────── conflicts ─────────────

        private void BuildConflicts()
        {
            string sig = string.Join(",", Conflicts.Apps.Select(a => a.Key + a.Running));
            if (sig == _conflictSig) return;
            _conflictSig = sig;
            ConflictsHost.Children.Clear();
            foreach (var app in Conflicts.Apps)
            {
                var card = new Border { Style = (Style)FindResource("SubCard"), Margin = new Thickness(0, 8, 0, 0) };
                var dp = new DockPanel();
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(buttons, Dock.Right);
                CheckBox? optional = null;
                if (app.OptionalServices.Length > 0 && app.Running)
                {
                    optional = new CheckBox { Content = L.T("כולל EasyTune", "incl. EasyTune"), FontSize = 11.5, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = L.T("עוצר גם את שירות EasyTune Engine של GIGABYTE (מנוע הקירור של GCC)", "Also stops GIGABYTE's EasyTune Engine service (GCC's cooling engine)") };
                    buttons.Children.Add(optional);
                }
                if (app.Running)
                {
                    var close = new Button { Content = L.T("סגור והשתלט", "Close and take over"), Style = (Style)FindResource("Primary"), Padding = new Thickness(12, 5, 12, 5) };
                    close.Click += async (_, _) =>
                    {
                        close.IsEnabled = false;
                        bool opt = optional?.IsChecked == true;
                        string msg = await Task.Run(() => Conflicts.Close(app, opt));
                        Conflicts.Refresh(App.Config);
                        MainWindow.Instance?.ShowToast(msg);
                        _conflictSig = "";
                        BuildConflicts();
                    };
                    buttons.Children.Add(close);
                }
                else
                {
                    var restore = new Button { Content = L.T("החזר שליטה", "Hand back control"), Padding = new Thickness(12, 5, 12, 5),
                        ToolTip = L.T("מפעיל מחדש את התוכנה ואת השירותים שלה. PulseWatch יעבור לקריאה בלבד עבור החומרה הזו.", "Restarts the app and its services. PulseWatch switches to read-only for that hardware.") };
                    restore.Click += async (_, _) =>
                    {
                        restore.IsEnabled = false;
                        string msg = await Task.Run(() => Conflicts.Restore(app));
                        MainWindow.Instance?.ShowToast(msg);
                        await Task.Delay(2500);
                        Conflicts.Refresh(App.Config);
                        _conflictSig = "";
                        BuildConflicts();
                    };
                    buttons.Children.Add(restore);
                }
                dp.Children.Add(buttons);
                var left = new StackPanel();
                var title = new StackPanel { Orientation = Orientation.Horizontal };
                title.Children.Add(new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Ui.Res(app.Running ? "Warn" : "Good"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                title.Children.Add(new TextBlock { Text = app.Title, FontWeight = FontWeights.SemiBold });
                left.Children.Add(title);
                left.Children.Add(new TextBlock
                {
                    Text = app.Running ? L.T($"פועל ושולט ב{app.Affects}", $"Running — controls {app.Affects}") : L.T($"סגור — PulseWatch שולט ב{app.Affects}", $"Closed — PulseWatch controls {app.Affects}"),
                    Style = (Style)FindResource("Caption"), Margin = new Thickness(16, 3, 0, 0), TextWrapping = TextWrapping.Wrap
                });
                dp.Children.Add(left);
                card.Child = dp;
                ConflictsHost.Children.Add(card);
            }
        }

        // ───────────── settings ─────────────

        private void UpdateLabels()
        {
            if (SafeCpuVal == null || SafeGpuVal == null || ExitFanVal == null) return;
            SafeCpuVal.Text = SafeCpu.Value >= SafeCpu.Maximum - 0.1 ? L.T($"{SafeCpu.Value:0}°C · גבול החומרה", $"{SafeCpu.Value:0} °C · hardware limit") : $"{SafeCpu.Value:0}°C";
            SafeGpuVal.Text = SafeGpu.Value >= SafeGpu.Maximum - 0.1 ? L.T($"{SafeGpu.Value:0}°C · גבול החומרה", $"{SafeGpu.Value:0} °C · hardware limit") : $"{SafeGpu.Value:0}°C";
            ExitFanVal.Text = $"{ExitFan.Value:0}%";
        }

        private void Setting_Changed(object sender, EventArgs e)
        {
            UpdateLabels();
            if (_loading || !IsLoaded) return;
            lock (App.Config)
            {
                App.Config.CloseConflictsOnLaunch = AutoClose.IsChecked == true;
                App.Config.StartMinimized = StartMin.IsChecked == true;
                App.Config.MinimizeToTray = CloseToTray.IsChecked == true;
                if (PollCombo.SelectedItem is ComboBoxItem it && int.TryParse((string)it.Tag, out int ms)) App.Config.PollMs = ms;
                if (HddCombo.SelectedItem is ComboBoxItem hi && int.TryParse((string)hi.Tag, out int hs)) App.Config.HddPollSeconds = hs;
                App.Config.HddKeepAsleep = HddSleep.IsChecked == true;
                if (NvmeCombo.SelectedItem is ComboBoxItem ni && int.TryParse((string)ni.Tag, out int ns)) App.Config.NvmePollSeconds = ns;
                App.Config.SafetyCpuC = SafeCpu.Value;
                App.Config.SafetyGpuC = SafeGpu.Value;
                App.Config.LianLiExitFanPct = ExitFan.Value;
            }
            App.SaveSoon();
        }

        private async void Startup_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            bool on = StartWithWindows.IsChecked == true;
            string? err = await Task.Run(() => on ? StartupTask.Enable() : StartupTask.Disable());
            if (err != null) MainWindow.Instance?.ShowToast(L.T("לא ניתן לעדכן הפעלה אוטומטית: ", "Could not update auto-start: ") + err, MainWindow.ToastKind.Error);
            else MainWindow.Instance?.ShowToast(on ? L.T("PulseWatch יופעל אוטומטית בכניסה ל-Windows (ממוזער למגש)", "PulseWatch will start with Windows (minimized to the tray)") : L.T("ההפעלה האוטומטית בוטלה", "Auto-start disabled"));
        }

        private async void Power_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || PowerCombo.SelectedItem is not PowerPlan p) return;
            bool ok = await Task.Run(() => PowerPlans.SetActive(p.Guid));
            MainWindow.Instance?.ShowToast(ok ? L.T($"תוכנית חשמל: {p.Name}", $"Power plan: {p.Name}") : L.T("לא ניתן להחליף תוכנית חשמל", "Could not change the power plan"), ok ? MainWindow.ToastKind.Info : MainWindow.ToastKind.Error);
        }

        private void OpenData_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });
        private void OpenLog_Click(object sender, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true }); } catch { } }
        private void Exit_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.ExitApp();
    }
}
