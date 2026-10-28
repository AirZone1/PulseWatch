using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PulseWatch.Core;

namespace PulseWatch.Views
{
    public partial class NvidiaView : UserControl, IView
    {
        private bool _loaded;
        private bool _building;
        private readonly Dictionary<uint, FrameworkElement> _editors = new();
        private readonly Dictionary<uint, Border> _changedPills = new();

        public NvidiaView() { InitializeComponent(); }

        public async void OnShown()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                await Task.Run(NvidiaSettings.Load);
                var displays = await Task.Run(NvidiaSettings.Displays);
                DriverText.Text = L.T($"דרייבר {NvidiaSettings.DriverVersion}", $"Driver {NvidiaSettings.DriverVersion}");
                BuildSettings();
                BuildDisplays(displays);
                LoadingText.Visibility = Visibility.Collapsed;
                int hz = RefreshRate();
                if (hz > 0) GsyncBtn.ToolTip = L.T($"V-Sync מופעל · Low Latency מופעל · הגבלת FPS ל-{hz - 3} (רענון {hz}Hz פחות 3)", $"V-Sync on · Low Latency on · FPS cap {hz - 3} ({hz} Hz refresh minus 3)");
            }
            catch (Exception ex)
            {
                Log.Error("NVIDIA settings load", ex);
                LoadingText.Text = L.T("לא ניתן לגשת להגדרות הדרייבר של NVIDIA: ", "Cannot access the NVIDIA driver settings: ") + ex.Message;
                _loaded = false;
            }
        }

        public void Refresh() { }

        // ───────────── settings ─────────────

        private void BuildSettings()
        {
            _building = true;
            SettingsHost.Children.Clear();
            _editors.Clear();
            _changedPills.Clear();
            foreach (var group in NvidiaSettings.Defs.Where(d => d.Available).GroupBy(d => d.Category))
            {
                var card = new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 16, 0, 0) };
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock { Text = group.Key, Style = (Style)FindResource("H2"), Margin = new Thickness(0, 0, 0, 4) });
                bool first = true;
                foreach (var def in group)
                {
                    if (!first) stack.Children.Add(new Border { Height = 1, Background = Ui.Res("Line"), Margin = new Thickness(0, 12, 0, 0) });
                    first = false;
                    stack.Children.Add(BuildRow(def));
                }
                card.Child = stack;
                SettingsHost.Children.Add(card);
            }
            _building = false;
        }

        private FrameworkElement BuildRow(NvSettingDef def)
        {
            var grid = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = def.Title, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            var pill = new Border { Style = (Style)FindResource("Pill"), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(8, 1, 8, 1), Child = new TextBlock { Text = L.T("שונה", "changed"), FontSize = 11, Foreground = Ui.Res("Accent") } };
            pill.Visibility = def.IsSet && def.Current != def.Default ? Visibility.Visible : Visibility.Collapsed;
            _changedPills[def.Id] = pill;
            titleRow.Children.Add(pill);
            left.Children.Add(titleRow);
            left.Children.Add(new TextBlock { Text = def.Help, Style = (Style)FindResource("Hint"), Margin = new Thickness(0, 3, 0, 0) });
            grid.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(right, 1);

            if (def.Range is (uint min, uint max))
            {
                var box = new TextBox { Width = 70, FlowDirection = FlowDirection.LeftToRight, TextAlignment = TextAlignment.Center, Text = def.Current == 0 ? "" : def.Current.ToString(), Tag = def };
                var zero = new CheckBox { Content = def.ZeroLabel, IsChecked = def.Current == 0, Margin = new Thickness(0, 0, 12, 0), Tag = def, VerticalAlignment = VerticalAlignment.Center };
                box.IsEnabled = def.Current != 0;
                zero.Checked += (_, _) => { box.IsEnabled = false; if (!_building) ApplySetting(def, 0); };
                zero.Unchecked += (_, _) => { box.IsEnabled = true; box.Focus(); if (string.IsNullOrEmpty(box.Text)) box.Text = "141"; };
                void commit()
                {
                    if (_building || zero.IsChecked == true) return;
                    if (uint.TryParse(box.Text, out uint v) && v >= 20 && v <= max) ApplySetting(def, v);
                    else box.Text = def.Current == 0 ? "" : def.Current.ToString();
                }
                box.LostFocus += (_, _) => commit();
                box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { commit(); Keyboard.ClearFocus(); } };
                right.Children.Add(zero);
                right.Children.Add(box);
                right.Children.Add(new TextBlock { Text = def.RangeUnit, Style = (Style)FindResource("Caption"), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                _editors[def.Id] = box;
            }
            else
            {
                var combo = new ComboBox { Width = 230, Tag = def };
                foreach (var o in def.Options) combo.Items.Add(o);
                var match = def.Options.FirstOrDefault(o => o.Value == def.Current);
                if (match == null)
                {
                    match = new NvOption { Value = def.Current, Label = L.T($"ערך מותאם (0x{def.Current:X})", $"Custom value (0x{def.Current:X})") };
                    combo.Items.Add(match);
                }
                combo.SelectedItem = match;
                combo.SelectionChanged += (_, _) => { if (!_building && combo.SelectedItem is NvOption o) ApplySetting(def, o.Value); };
                right.Children.Add(combo);
                _editors[def.Id] = combo;
            }

            var reset = new Button { Content = "", Style = (Style)FindResource("IconBtn"), ToolTip = L.T("ברירת מחדל של הדרייבר", "Driver default"), Margin = new Thickness(6, 0, 0, 0) };
            reset.Click += async (_, _) =>
            {
                try { await Task.Run(() => NvidiaSettings.Reset(def)); Rebuild(); MainWindow.Instance?.ShowToast(L.T($"\"{def.Title}\" הוחזר לברירת מחדל", $"\"{def.Title}\" restored to default")); }
                catch (Exception ex) { MainWindow.Instance?.ShowToast(L.T("שגיאה: ", "Error: ") + ex.Message, MainWindow.ToastKind.Error); }
            };
            right.Children.Add(reset);
            grid.Children.Add(right);
            return grid;
        }

        private async void ApplySetting(NvSettingDef def, uint value)
        {
            try
            {
                await Task.Run(() => NvidiaSettings.Set(def, value));
                if (_changedPills.TryGetValue(def.Id, out var pill)) pill.Visibility = value != def.Default ? Visibility.Visible : Visibility.Collapsed;
                string label = def.Options.FirstOrDefault(o => o.Value == value)?.Label ?? (value == 0 ? def.ZeroLabel : $"{value} {def.RangeUnit}");
                MainWindow.Instance?.ShowToast($"{def.Title}: {label}");
            }
            catch (Exception ex)
            {
                Log.Error("NVIDIA setting write", ex);
                MainWindow.Instance?.ShowToast(L.T("שגיאה בשמירת ההגדרה לדרייבר: ", "Could not save the setting to the driver: ") + ex.Message, MainWindow.ToastKind.Error);
            }
        }

        private void Rebuild()
        {
            try { NvidiaSettings.Load(); } catch { }
            BuildSettings();
        }

        private async void ResetAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await Task.Run(NvidiaSettings.ResetAll);
                Rebuild();
                MainWindow.Instance?.ShowToast(L.T("כל ההגדרות הגלובליות של NVIDIA הוחזרו לברירת מחדל", "All global NVIDIA settings restored to defaults"));
            }
            catch (Exception ex) { MainWindow.Instance?.ShowToast(L.T("שגיאה: ", "Error: ") + ex.Message, MainWindow.ToastKind.Error); }
        }

        private async Task ApplyMany(string title, params (uint id, uint value)[] items)
        {
            try
            {
                await Task.Run(() =>
                {
                    foreach (var (id, value) in items)
                    {
                        var def = NvidiaSettings.Defs.FirstOrDefault(d => d.Id == id && d.Available);
                        if (def != null) NvidiaSettings.Set(def, value);
                    }
                });
                Rebuild();
                MainWindow.Instance?.ShowToast(title + L.T(" — הוחל", " — applied"));
            }
            catch (Exception ex) { MainWindow.Instance?.ShowToast(L.T("שגיאה: ", "Error: ") + ex.Message, MainWindow.ToastKind.Error); }
        }

        private async void PresetCompetitive_Click(object sender, RoutedEventArgs e) =>
            await ApplyMany(L.T("משחקים תחרותיים", "Competitive gaming"), (0x007BA09E, 1), (0x1057EB71, 1), (0x00A879CF, 0x08416747), (0x0064B541, 1), (0x00CE2691, 10));

        private async void PresetGsync_Click(object sender, RoutedEventArgs e)
        {
            int hz = RefreshRate();
            var list = new List<(uint, uint)> { (0x00A879CF, 0x47814940), (0x007BA09E, 1) };
            if (hz > 30) list.Add((0x10835002, (uint)(hz - 3)));
            await ApplyMany("G-SYNC", list.ToArray());
        }

        private async void PresetQuality_Click(object sender, RoutedEventArgs e) =>
            await ApplyMany(L.T("איכות", "Quality"), (0x10D2BB16, 1), (0x101E61A9, 16), (0x00CE2691, 0xFFFFFFF6));

        // ───────────── vibrance ─────────────

        private void BuildDisplays(List<NvDisplay> displays)
        {
            DisplaysHost.Children.Clear();
            if (displays.Count == 0)
            {
                DisplaysHost.Children.Add(new TextBlock { Text = L.T("לא נמצאו מסכים המחוברים לכרטיס NVIDIA.", "No displays connected to the NVIDIA card."), Style = (Style)FindResource("Caption") });
                return;
            }
            foreach (var d in displays)
            {
                var sp = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                var head = new DockPanel();
                var val = new TextBlock { Text = $"{d.Vibrance}%", Style = (Style)FindResource("Num"), FontSize = 14 };
                DockPanel.SetDock(val, Dock.Right);
                head.Children.Add(val);
                head.Children.Add(new TextBlock { Text = d.Title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
                sp.Children.Add(head);
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
                var reset = new Button { Content = "", Style = (Style)FindResource("IconBtn"), ToolTip = L.T("50% (ברירת מחדל)", "50% (default)") };
                DockPanel.SetDock(reset, Dock.Right);
                var slider = new Slider { Minimum = d.VibranceMin, Maximum = d.VibranceMax, Value = d.Vibrance, TickFrequency = 1, IsSnapToTickEnabled = true };
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    int level = (int)slider.Value;
                    Task.Run(() => { try { NvidiaSettings.SetVibrance(d.DeviceName, level); } catch (Exception ex) { Log.Warn("Vibrance: " + ex.Message); } });
                    lock (App.Config)
                    {
                        if (level == d.VibranceDefault) App.Config.Vibrance.Remove(d.DeviceName);
                        else App.Config.Vibrance[d.DeviceName] = level;
                    }
                    App.SaveSoon();
                };
                slider.ValueChanged += (_, _) => { val.Text = $"{slider.Value:0}%"; timer.Stop(); timer.Start(); };
                reset.Click += (_, _) => slider.Value = d.VibranceDefault;
                row.Children.Add(reset);
                row.Children.Add(slider);
                sp.Children.Add(row);
                DisplaysHost.Children.Add(sp);
            }
        }

        // ───────────── helpers ─────────────

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);

        private static int RefreshRate()
        {
            try
            {
                var scr = System.Windows.Forms.Screen.PrimaryScreen;
                if (scr == null) return 0;
                var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                return EnumDisplaySettings(scr.DeviceName, -1, ref dm) ? dm.dmDisplayFrequency : 0;
            }
            catch { return 0; }
        }
    }
}
