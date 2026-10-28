using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PulseWatch.Core;

namespace PulseWatch.Views
{
    public partial class AutomationView : UserControl, IView
    {
        private bool _loading = true;

        private sealed record Choice(string? Id, string Text) { public override string ToString() => Text; }
        private sealed record SlotChoice(int? Slot, string Text) { public override string ToString() => Text; }
        private sealed record ScreenChoice(string Device, string Text) { public override string ToString() => Text; }

        private static List<ScreenChoice> Screens()
        {
            var list = new List<ScreenChoice>();
            int n = 1;
            foreach (var s in System.Windows.Forms.Screen.AllScreens.OrderByDescending(x => x.Primary))
            {
                string size = $"{s.Bounds.Width}×{s.Bounds.Height}";
                int idx = s.Primary ? 1 : ++n;
                list.Add(new ScreenChoice(s.Primary ? "" : s.DeviceName, s.Primary ? L.T($"מסך ראשי ({size})", $"Main screen ({size})") : L.T($"מסך {idx} ({size})", $"Screen {idx} ({size})")));
            }
            return list;
        }

        public AutomationView()
        {
            InitializeComponent();
            foreach (var k in Automation.Keys.Where(k => k.he.Length > 0))
                KeysHost.Children.Add(new TextBlock { Text = L.T(k.he, k.en), Margin = new Thickness(0, 3, 0, 3), FontSize = 13 });
        }

        public void OnShown()
        {
            _loading = true;
            lock (App.Config)
            {
                var c = App.Config;
                GamesOn.IsChecked = c.GameRulesEnabled;
                HotkeysOn.IsChecked = c.HotkeysEnabled;
                AlertsOn.IsChecked = c.Alerts.Enabled;
                ACpu.Value = c.Alerts.CpuC;
                AGpu.Value = c.Alerts.GpuC;
                AHot.Value = c.Alerts.HotspotC;
                AVram.Value = c.Alerts.VramC;
                ANvme.Value = c.Alerts.NvmeC;
                OsdOn.IsChecked = c.Osd.Enabled;
                CornerTl.IsChecked = c.Osd.Corner == "tl";
                CornerTc.IsChecked = c.Osd.Corner == "tc";
                CornerBc.IsChecked = c.Osd.Corner == "bc";
                LayoutRows.IsChecked = c.Osd.Layout != "line";
                LayoutLine.IsChecked = c.Osd.Layout == "line";
                SizeS.IsChecked = c.Osd.Size < 11;
                SizeM.IsChecked = c.Osd.Size is >= 11 and < 14;
                SizeL.IsChecked = c.Osd.Size >= 14;
                CornerTr.IsChecked = c.Osd.Corner == "tr";
                CornerBl.IsChecked = c.Osd.Corner == "bl";
                CornerBr.IsChecked = c.Osd.Corner == "br";
                OsdFps.IsChecked = c.Osd.ShowFps;
                var screens = Screens();
                OsdScreen.ItemsSource = screens;
                OsdScreen.SelectedItem = screens.FirstOrDefault(x => x.Device == c.Osd.Screen) ?? screens.FirstOrDefault();
                var fpsChoices = new List<ScreenChoice> { new("fg", L.T("המשחק שבחזית", "The game in the foreground")) };
                fpsChoices.AddRange(screens.Select(x => new ScreenChoice(x.Device.Length == 0 ? System.Windows.Forms.Screen.PrimaryScreen!.DeviceName : x.Device, L.T("מה שרץ על ", "What runs on ") + x.Text)));
                FpsSource.ItemsSource = fpsChoices;
                FpsSource.SelectedItem = fpsChoices.FirstOrDefault(x => x.Device == c.Osd.FpsSource) ?? fpsChoices[0];
                BuildOsdItems(c.Osd.Items);
                OsdOpacity.Value = c.Osd.Opacity;
            }
            NewProfile.ItemsSource = ProfileChoices();
            NewProfile.SelectedIndex = 0;
            NewSlot.ItemsSource = SlotChoices();
            NewSlot.SelectedIndex = 0;
            BuildRules();
            UpdateLabels();
            _loading = false;
            _ = LoadRunningApps();
        }

        public void Refresh()
        {
            var names = Conflicts.ProcessNames;
            List<GameRule> rules;
            lock (App.Config) rules = App.Config.GameRules.ToList();
            var active = rules.FirstOrDefault(r => names.Contains(Automation.Strip(r.Exe)));
            GameState.Text = active != null ? L.T($"פעיל עכשיו: {active.Exe}", $"Active now: {active.Exe}")
                           : rules.Count == 0 ? L.T("עוד לא הוגדרו משחקים.", "No games set up yet.")
                           : L.T("אף אחד מהמשחקים ברשימה לא רץ כרגע.", "None of the listed games is running.");
        }

        private static List<Choice> ProfileChoices()
        {
            var list = new List<Choice> { new(null, L.T("קירור: ללא שינוי", "Cooling: no change")) };
            lock (App.Config) list.AddRange(App.Config.Profiles.Select(p => new Choice(p.Id, L.T("קירור: ", "Cooling: ") + p.DisplayName)));
            return list;
        }

        private static List<SlotChoice> SlotChoices()
        {
            var list = new List<SlotChoice> { new(null, L.T("OC: ללא שינוי", "OC: no change")) };
            lock (App.Config)
                for (int i = 1; i <= 5; i++)
                {
                    var s = App.Config.Gpu.Slots[i - 1];
                    list.Add(new SlotChoice(i, s == null ? L.T($"OC {i} (ריק)", $"OC {i} (empty)") : $"OC {i} · {s.PowerLimitW} W"));
                }
            return list;
        }

        /// <summary>Programs with a window right now, as suggestions for a new rule.</summary>
        private async Task LoadRunningApps()
        {
            var names = await Task.Run(() =>
            {
                var r = new List<string>();
                foreach (var p in Process.GetProcesses())
                {
                    try { if (p.MainWindowHandle != IntPtr.Zero && p.ProcessName is not ("PulseWatch" or "explorer" or "ApplicationFrameHost" or "TextInputHost")) r.Add(p.ProcessName); }
                    catch { }
                    finally { p.Dispose(); }
                }
                return r.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            });
            names.Insert(0, L.T("בחר תוכנה שרצה עכשיו…", "Pick a running program…"));
            Running.ItemsSource = names;
            Running.SelectedIndex = 0;
        }

        private void Running_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (Running.SelectedIndex > 0 && Running.SelectedItem is string name) NewExe.Text = name;
        }

        private void BuildRules()
        {
            RulesHost.Children.Clear();
            List<GameRule> rules;
            lock (App.Config) rules = App.Config.GameRules.ToList();
            var profiles = ProfileChoices();
            var slots = SlotChoices();
            foreach (var rule in rules)
            {
                var r = rule;
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                var del = new Button { Content = "", Style = (Style)FindResource("IconBtn"), ToolTip = L.T("הסר", "Remove") };
                del.Click += (_, _) =>
                {
                    lock (App.Config) App.Config.GameRules.Remove(r);
                    App.SaveSoon();
                    BuildRules();
                };
                DockPanel.SetDock(del, Dock.Right);
                row.Children.Add(del);
                var prof = new ComboBox { Width = 190, Margin = new Thickness(8, 0, 0, 0), ItemsSource = profiles, SelectedItem = profiles.FirstOrDefault(p => p.Id == r.ProfileId) ?? profiles[0] };
                prof.SelectionChanged += (_, _) => { lock (App.Config) r.ProfileId = (prof.SelectedItem as Choice)?.Id; App.SaveSoon(); };
                var slot = new ComboBox { Width = 150, Margin = new Thickness(8, 0, 0, 0), ItemsSource = slots, SelectedItem = slots.FirstOrDefault(s => s.Slot == r.GpuSlot) ?? slots[0] };
                slot.SelectionChanged += (_, _) => { lock (App.Config) r.GpuSlot = (slot.SelectedItem as SlotChoice)?.Slot; App.SaveSoon(); };
                DockPanel.SetDock(slot, Dock.Right);
                DockPanel.SetDock(prof, Dock.Right);
                row.Children.Add(slot);
                row.Children.Add(prof);
                row.Children.Add(new TextBlock { Text = r.Exe, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                RulesHost.Children.Add(row);
            }
        }

        private void AddRule_Click(object sender, RoutedEventArgs e)
        {
            string exe = Automation.Strip((NewExe.Text ?? "").Trim());
            if (exe.Length == 0) { MainWindow.Instance?.ShowToast(L.T("בחר או הקלד שם תוכנה", "Pick or type a program name"), MainWindow.ToastKind.Warn); return; }
            var prof = (NewProfile.SelectedItem as Choice)?.Id;
            var slot = (NewSlot.SelectedItem as SlotChoice)?.Slot;
            if (prof == null && slot == null) { MainWindow.Instance?.ShowToast(L.T("בחר פרופיל קירור ו/או פרופיל OC", "Pick a cooling and/or OC profile"), MainWindow.ToastKind.Warn); return; }
            lock (App.Config)
            {
                App.Config.GameRules.RemoveAll(r => string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase));
                App.Config.GameRules.Add(new GameRule { Exe = exe, ProfileId = prof, GpuSlot = slot });
            }
            App.SaveSoon();
            NewExe.Text = "";
            BuildRules();
            Refresh();
        }

        private void TestAlert_Click(object sender, RoutedEventArgs e) =>
            MainWindow.Instance?.Notify(L.T("PulseWatch · בדיקה", "PulseWatch · test"), L.T("כך תיראה התראת טמפרטורה.", "This is what a temperature alert looks like."));

        private readonly Dictionary<string, CheckBox> _osdBoxes = new();

        /// <summary>One checkbox per OSD value, grouped like the overlay itself (CPU / GPU / system).</summary>
        private void BuildOsdItems(List<string> chosen)
        {
            OsdItemsHost.Children.Clear();
            _osdBoxes.Clear();
            foreach (var (group, title) in new[] { ("cpu", L.T("מעבד", "CPU")), ("gpu", L.T("כרטיס מסך", "Graphics card")), ("sys", L.T("מערכת", "System")) })
            {
                OsdItemsHost.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 8, 0, 4) });
                var wrap = new WrapPanel();
                foreach (var it in OsdWindow.Items.Where(i => i.Group == group))
                {
                    var cb = new CheckBox { Content = L.T(it.He, it.En), Margin = new Thickness(0, 0, 14, 6), IsChecked = chosen.Contains(it.Key) };
                    cb.Checked += Setting_Changed;
                    cb.Unchecked += Setting_Changed;
                    _osdBoxes[it.Key] = cb;
                    wrap.Children.Add(cb);
                }
                OsdItemsHost.Children.Add(wrap);
            }
        }

        private void UpdateLabels()
        {
            if (ACpuVal == null || OsdOpacityVal == null) return;
            ACpuVal.Text = $"{ACpu.Value:0}°C";
            AGpuVal.Text = $"{AGpu.Value:0}°C";
            AHotVal.Text = $"{AHot.Value:0}°C";
            AVramVal.Text = $"{AVram.Value:0}°C";
            ANvmeVal.Text = $"{ANvme.Value:0}°C";
            OsdOpacityVal.Text = $"{OsdOpacity.Value * 100:0}%";
        }

        private void Setting_Changed(object sender, EventArgs e)
        {
            UpdateLabels();
            if (_loading || !IsLoaded) return;
            lock (App.Config)
            {
                var c = App.Config;
                c.GameRulesEnabled = GamesOn.IsChecked == true;
                c.HotkeysEnabled = HotkeysOn.IsChecked == true;
                c.Alerts.Enabled = AlertsOn.IsChecked == true;
                c.Alerts.CpuC = ACpu.Value;
                c.Alerts.GpuC = AGpu.Value;
                c.Alerts.HotspotC = AHot.Value;
                c.Alerts.VramC = AVram.Value;
                c.Alerts.NvmeC = ANvme.Value;
                c.Osd.Enabled = OsdOn.IsChecked == true;
                c.Osd.Corner = CornerTr.IsChecked == true ? "tr" : CornerTc.IsChecked == true ? "tc" : CornerBl.IsChecked == true ? "bl"
                             : CornerBc.IsChecked == true ? "bc" : CornerBr.IsChecked == true ? "br" : "tl";
                c.Osd.Layout = LayoutLine.IsChecked == true ? "line" : "rows";
                c.Osd.Size = SizeS.IsChecked == true ? 10 : SizeL.IsChecked == true ? 15 : 12;
                c.Osd.ShowFps = OsdFps.IsChecked == true;
                if (OsdScreen.SelectedItem is ScreenChoice sc) c.Osd.Screen = sc.Device;
                if (FpsSource.SelectedItem is ScreenChoice fs) c.Osd.FpsSource = fs.Device;
                c.Osd.Items = _osdBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
                c.Osd.Opacity = OsdOpacity.Value;
            }
            App.SaveSoon();
            MainWindow.Instance?.Automation?.ApplySettings();
        }
    }
}
