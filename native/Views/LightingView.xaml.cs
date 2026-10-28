using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PulseWatch.Core;

namespace PulseWatch.Views
{
    public partial class LightingView : UserControl, IView
    {
        private bool _loading;

        private static readonly string[] Swatches =
        {
            "#FFFFFF", "#00C8FF", "#22D3EE", "#3B82F6", "#6366F1", "#A78BFA", "#D946EF", "#FF2D95",
            "#F43F5E", "#FF1E1E", "#FF7A00", "#FFAA00", "#FDE047", "#84CC16", "#00FF8C", "#10B981"
        };

        private static string[] Directions => new[]
        {
            L.T("עם כיוון השעון", "Clockwise"), L.T("נגד כיוון השעון", "Counter-clockwise"), L.T("למעלה", "Up"),
            L.T("למטה", "Down"), L.T("התפשטות", "Spread"), L.T("התכנסות", "Gather")
        };
        private static string[] Scopes => new[] { L.T("פנימי", "Inner"), L.T("חיצוני", "Outer"), L.T("הכל", "All") };

        public LightingView()
        {
            InitializeComponent();
        }

        private LightingConfig Lc => App.Config.Lighting;

        public void OnShown()
        {
            _loading = true;
            var aio = App.Hub.Aio;
            NoDevice.Visibility = aio == null ? Visibility.Visible : Visibility.Collapsed;
            DeviceTitle.Text = (aio != null ? L.T("תאורה · ", "Lighting · ") + aio.Model : L.T("תאורה", "Lighting"));
            DeviceSub.Text = aio != null
                ? L.T($"קושחה {aio.Firmware}. ראש המשאבה (טבעת פנימית + חיצונית) ו-3 מאווררי הרדיאטור. ההגדרות נשמרות בבקר ונשארות גם כש-PulseWatch סגור.",
                      $"Firmware {aio.Firmware}. Pump head (inner + outer ring) and the 3 radiator fans. Settings are stored in the controller and persist when PulseWatch is closed.")
                : "";
            string mode;
            lock (App.Config) mode = Lc.Mode;
            MUnchanged.IsChecked = mode == "unchanged";
            MCustom.IsChecked = mode == "custom";
            MThermal.IsChecked = mode == "thermal";
            MMb.IsChecked = mode == "mbsync";
            MOff.IsChecked = mode == "off";

            ThermalSource.ItemsSource = SensorHub.KnownAliases.Where(a => App.Hub.Sensor(a.Alias) != null).Select(a => a.Name).ToList();
            lock (App.Config)
            {
                int idx = SensorHub.KnownAliases.Where(a => App.Hub.Sensor(a.Alias) != null).Select(a => a.Alias).ToList().IndexOf(Lc.ThermalSource);
                ThermalSource.SelectedIndex = Math.Max(0, idx);
                ColdSlider.Value = Lc.ThermalCold;
                HotSlider.Value = Lc.ThermalHot;
                ThermBright.Value = Math.Max(1, Lc.Pump.Brightness);
            }
            PumpCard.Child = BuildEffectEditor(true);
            FansCard.Child = BuildEffectEditor(false);
            _loading = false;
            UpdatePanels(mode);
            UpdateThermalLabels();
        }

        private void UpdatePanels(string mode)
        {
            CustomPanel.Visibility = mode == "custom" ? Visibility.Visible : Visibility.Collapsed;
            ThermalPanel.Visibility = mode == "thermal" ? Visibility.Visible : Visibility.Collapsed;
            ModeHelp.Text = mode switch
            {
                "unchanged" => L.T("PulseWatch לא שולח פקודות תאורה — מה שהוגדר קודם (ב-L-Connect או כאן) נשאר בבקר.", "PulseWatch sends no lighting commands — whatever was set before (in L-Connect or here) stays in the controller."),
                "custom" => L.T("בחר אפקט, צבעים, בהירות ומהירות לראש המשאבה ולמאווררים. השינויים נשלחים מיד.", "Pick an effect, colours, brightness and speed for the pump head and fans. Changes are sent immediately."),
                "thermal" => L.T("PulseWatch מעדכן את הצבע כל כמה שניות לפי הטמפרטורה (רק כשהצבע משתנה, כדי לא להעמיס על ה-USB).", "PulseWatch updates the colour every few seconds from the temperature (only when it changes, to keep USB traffic low)."),
                "mbsync" => L.T("התאורה תגיע מכבל ה-ARGB של לוח האם (RGB Fusion / SignalRGB) — מתאים לסנכרון עם שאר הרכיבים.", "Lighting comes from the motherboard ARGB header (RGB Fusion / SignalRGB) — for syncing with your other parts."),
                _ => L.T("כל התאורה כבויה.", "All lighting is off.")
            };
        }

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton rb) return;
            string mode = (string)rb.Tag;
            lock (App.Config) Lc.Mode = mode;
            UpdatePanels(mode);
            Commit();
        }

        private void Commit()
        {
            App.SaveSoon();
            App.Hub.Poke();
        }

        // ───────────── effect editor (built in code, reused for pump + fans) ─────────────

        private FrameworkElement BuildEffectEditor(bool pump)
        {
            LightEffect e;
            lock (App.Config) e = pump ? Lc.Pump : Lc.Fans;
            var root = new StackPanel();
            root.Children.Add(new TextBlock { Text = pump ? L.T("ראש המשאבה", "Pump head") : L.T("מאווררי הרדיאטור", "Radiator fans"), Style = (Style)FindResource("H2") });

            if (!pump)
            {
                var sync = new CheckBox { Style = (Style)FindResource("Switch"), Content = L.T("סנכרן לאפקט של המשאבה", "Sync with the pump effect"), Margin = new Thickness(0, 10, 0, 0) };
                lock (App.Config) sync.IsChecked = Lc.SyncFansToPump;
                sync.Checked += (_, _) => { lock (App.Config) Lc.SyncFansToPump = true; Commit(); };
                sync.Unchecked += (_, _) => { lock (App.Config) Lc.SyncFansToPump = false; Commit(); };
                root.Children.Add(sync);
            }

            root.Children.Add(Label(L.T("אפקט", "Effect")));
            var mode = new ComboBox();
            foreach (var m in LianLiTrinity.Modes) mode.Items.Add(m.Name);
            mode.SelectedIndex = Math.Max(0, Array.FindIndex(LianLiTrinity.Modes, m => m.Mode == e.Mode));
            mode.SelectionChanged += (_, _) => { if (mode.SelectedIndex >= 0) { lock (App.Config) e.Mode = LianLiTrinity.Modes[mode.SelectedIndex].Mode; Commit(); } };
            root.Children.Add(mode);

            if (pump)
            {
                root.Children.Add(Label(L.T("אזור", "Zone")));
                var scope = new ComboBox();
                foreach (var s in Scopes) scope.Items.Add(s);
                scope.SelectedIndex = Math.Clamp(e.Scope, 0, 2);
                scope.SelectionChanged += (_, _) => { lock (App.Config) e.Scope = scope.SelectedIndex; Commit(); };
                root.Children.Add(scope);
            }

            root.Children.Add(Label(L.T("צבעים", "Colours")));
            var colors = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                colors.Children.Add(ColorButton(() => { lock (App.Config) return idx < e.Colors.Count ? e.Colors[idx] : "#FFFFFF"; },
                    c => { lock (App.Config) { while (e.Colors.Count <= idx) e.Colors.Add("#FFFFFF"); e.Colors[idx] = c; } Commit(); }));
            }
            root.Children.Add(colors);

            root.Children.Add(SliderRow(L.T("בהירות", "Brightness"), 0, 4, e.Brightness, v => { lock (App.Config) e.Brightness = v; Commit(); }, v => v == 0 ? L.T("כבוי", "off") : $"{v}/4"));
            root.Children.Add(SliderRow(L.T("מהירות", "Speed"), 0, 4, e.Speed, v => { lock (App.Config) e.Speed = v; Commit(); }, v => $"{v + 1}/5"));

            root.Children.Add(Label(L.T("כיוון", "Direction")));
            var dir = new ComboBox();
            foreach (var d in Directions) dir.Items.Add(d);
            dir.SelectedIndex = Math.Clamp(e.Direction, 0, 5);
            dir.SelectionChanged += (_, _) => { lock (App.Config) e.Direction = dir.SelectedIndex; Commit(); };
            root.Children.Add(dir);
            return root;
        }

        private TextBlock Label(string text) => new() { Text = text, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 14, 0, 6) };

        private FrameworkElement SliderRow(string title, int min, int max, int value, Action<int> set, Func<int, string> fmt)
        {
            var sp = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            var head = new DockPanel();
            var val = new TextBlock { Text = fmt(value), Style = (Style)FindResource("Num"), FontSize = 13 };
            DockPanel.SetDock(val, Dock.Right);
            head.Children.Add(val);
            head.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Caption") });
            sp.Children.Add(head);
            var s = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true };
            s.ValueChanged += (_, _) => { val.Text = fmt((int)s.Value); if (!_loading) set((int)s.Value); };
            sp.Children.Add(s);
            return sp;
        }

        private FrameworkElement ColorButton(Func<string> get, Action<string> set)
        {
            Brush BrushOf(string hex) { try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); } catch { return Brushes.White; } }
            var swatch = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Background = BrushOf(get()), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(2), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand };
            var popup = new Popup { PlacementTarget = swatch, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
            var panel = new Border { Background = Ui.Res("CardHi"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Margin = new Thickness(0, 6, 0, 0) };
            var stack = new StackPanel { Width = 216 };
            var wrap = new WrapPanel();
            var hexBox = new TextBox { Text = get(), FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 8, 0, 0) };
            void pick(string hex)
            {
                swatch.Background = BrushOf(hex);
                hexBox.Text = hex;
                set(hex);
            }
            foreach (var hex in Swatches)
            {
                var b = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(6), Background = BrushOf(hex), Margin = new Thickness(2), Cursor = Cursors.Hand };
                b.MouseLeftButtonUp += (_, _) => { pick(hex); popup.IsOpen = false; };
                wrap.Children.Add(b);
            }
            hexBox.KeyDown += (_, a) =>
            {
                if (a.Key != Key.Enter) return;
                string h = hexBox.Text.Trim();
                if (!h.StartsWith("#")) h = "#" + h;
                if (h.Length == 7 && int.TryParse(h[1..], System.Globalization.NumberStyles.HexNumber, null, out int _)) { pick(h.ToUpperInvariant()); popup.IsOpen = false; }
            };
            stack.Children.Add(wrap);
            stack.Children.Add(hexBox);
            panel.Child = stack;
            popup.Child = panel;
            swatch.MouseLeftButtonUp += (_, _) => { hexBox.Text = get(); popup.IsOpen = true; };
            var host = new Grid();
            host.Children.Add(swatch);
            host.Children.Add(popup);
            return host;
        }

        // ───────────── thermal ─────────────

        private void Thermal_Changed(object sender, EventArgs e)
        {
            UpdateThermalLabels();
            if (_loading || !IsLoaded || ColdSlider == null || HotSlider == null || ThermBright == null) return;
            var aliases = SensorHub.KnownAliases.Where(a => App.Hub.Sensor(a.Alias) != null).Select(a => a.Alias).ToList();
            lock (App.Config)
            {
                if (ThermalSource.SelectedIndex >= 0 && ThermalSource.SelectedIndex < aliases.Count) Lc.ThermalSource = aliases[ThermalSource.SelectedIndex];
                Lc.ThermalCold = Math.Min(ColdSlider.Value, HotSlider.Value - 5);
                Lc.ThermalHot = HotSlider.Value;
                Lc.Pump.Brightness = (int)ThermBright.Value;
                Lc.Fans.Brightness = (int)ThermBright.Value;
            }
            Commit();
        }

        private void UpdateThermalLabels()
        {
            if (ColdVal == null || HotVal == null || ThermBrightVal == null || ColdSlider == null || HotSlider == null || ThermBright == null || GradCold == null || GradHot == null) return;
            ColdVal.Text = $"{ColdSlider.Value:0}°C";
            HotVal.Text = $"{HotSlider.Value:0}°C";
            ThermBrightVal.Text = $"{ThermBright.Value:0}/4";
            GradCold.Text = $"{ColdSlider.Value:0}°C";
            GradHot.Text = $"{HotSlider.Value:0}°C";
        }

        public void Refresh()
        {
            BlockedNote.Visibility = App.Hub.Channels.Any(c => c.ConflictGroup == "lconnect" && c.Blocked) ? Visibility.Visible : Visibility.Collapsed;
            if (ThermalPanel.Visibility != Visibility.Visible) return;
            string src; double cold, hot;
            lock (App.Config) { src = Lc.ThermalSource; cold = Lc.ThermalCold; hot = Lc.ThermalHot; }
            float t = App.Hub.Value(src);
            if (float.IsNaN(t)) { ThermNowText.Text = L.T("אין קריאה מהחיישן", "No reading from the sensor"); return; }
            double f = Math.Clamp((t - cold) / Math.Max(1, hot - cold), 0, 1);
            string hex = SensorHub.LerpColor(Math.Round(f * 20) / 20);
            ThermNow.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            ThermNowText.Text = L.T($"עכשיו {t:0.0}°C → {hex}", $"Now {t:0.0} °C → {hex}");
            Canvas.SetLeft(ThermMarker, f * Math.Max(0, GradientHost.ActualWidth - 4));
        }
    }
}
