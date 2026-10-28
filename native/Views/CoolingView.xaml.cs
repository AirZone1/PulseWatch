using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PulseWatch.Controls;
using PulseWatch.Core;

namespace PulseWatch.Views
{
    public partial class CoolingView : UserControl, IView
    {
        private readonly ObservableCollection<ChannelRow> _rows = new();
        private string _channelSig = "";
        private FanChannel? _sel;
        private bool _loading;

        private sealed class SourceItem
        {
            public string Id { get; init; } = "";
            public string Title { get; init; } = "";
            public override string ToString() => Title;
        }

        private static readonly Dictionary<string, (double t, double p)[]> Presets = new()
        {
            ["silent"] = new[] { (40.0, 25.0), (60, 35), (72, 50), (82, 75), (90, 100) },
            ["balanced"] = new[] { (40.0, 30.0), (55, 40), (70, 60), (80, 85), (88, 100) },
            ["performance"] = new[] { (40.0, 45.0), (55, 60), (68, 80), (78, 100) },
            ["flat"] = new[] { (30.0, 50.0), (90, 50) },
            ["zero"] = new[] { (50.0, 0.0), (55, 35), (70, 60), (80, 100) },
        };

        public CoolingView()
        {
            InitializeComponent();
            ChannelList.ItemsSource = _rows;
            CurveEditor.PointsChanged += CurveEditor_PointsChanged;
            CurveEditor.PointsChanging += () => CurveEditor.InvalidateVisual();
        }

        public void OnShown()
        {
            _channelSig = "";
            ProfileHint.Text = L.T($"פרופיל: {App.Config.ActiveProfile.DisplayName}", $"Profile: {App.Config.ActiveProfile.DisplayName}");
            ProfileHint.ToolTip = L.T("לכל פרופיל (שקט / מאוזן / ביצועים / מקסימום) הגדרה נפרדת לכל ערוץ. מחליפים פרופיל מהלוח הראשי או מהמגש.",
                                      "Each profile (Silent / Balanced / Performance / Max) keeps its own setting per channel. Switch profiles from the dashboard or the tray.");
            RebuildRows();
            if (_sel != null) LoadEditor(_sel);
        }

        public void Select(string channelId)
        {
            RebuildRows();
            var row = _rows.FirstOrDefault(r => r.Channel.Id == channelId);
            if (row != null) ChannelList.SelectedItem = row;
        }

        private void RebuildRows()
        {
            bool showHidden = ShowHidden.IsChecked == true;
            List<string> hidden;
            lock (App.Config) hidden = App.Config.HiddenChannels.ToList();
            var chans = App.Hub.Channels.Where(c => showHidden || !hidden.Contains(c.Id))
                .OrderBy(c => c.Kind switch { ChannelKind.Pump => 0, ChannelKind.AioFans => 1, ChannelKind.Gpu => 2, ChannelKind.NvmeFan => 3, _ => 4 }).ToList();
            string sig = string.Join("|", chans.Select(c => c.Id));
            if (sig == _channelSig) return;
            _channelSig = sig;
            string? keep = _sel?.Id;
            _rows.Clear();
            foreach (var c in chans) { var r = new ChannelRow(c); r.Update(); _rows.Add(r); }
            var again = _rows.FirstOrDefault(r => r.Channel.Id == keep) ?? _rows.FirstOrDefault();
            ChannelList.SelectedItem = again;
            Editor.Visibility = _rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowHidden_Changed(object sender, RoutedEventArgs e) { _channelSig = ""; RebuildRows(); }

        private void ChannelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ChannelList.SelectedItem is ChannelRow r) { _sel = r.Channel; LoadEditor(r.Channel); Refresh(); }
        }

        // ───────────── editor load ─────────────

        private void LoadEditor(FanChannel ch)
        {
            _loading = true;
            try
            {
                NameBox.Text = Ui.ChannelName(ch);
                GroupText.Text = $"{ch.Group}   ·   {L.T("מזהה", "ID")}: {ch.DefaultName}";
                ChannelHelp.Help = ch.Help;
                lock (App.Config) HiddenBox.IsChecked = App.Config.HiddenChannels.Contains(ch.Id);
                var a = Ui.Assignment(ch.Id);
                ModeAuto.IsChecked = a.Mode == FanMode.Auto;
                ModeFixed.IsChecked = a.Mode == FanMode.Fixed;
                ModeCurve.IsChecked = a.Mode == FanMode.Curve;
                ModeAuto.Content = ch.Kind is ChannelKind.Pump or ChannelKind.AioFans ? "MB Sync" : L.T("אוטומטי", "Auto");
                BuildDriveCombo(ch);
                FixedSlider.Minimum = ch.MinPct;
                FixedSlider.Value = Math.Max(ch.MinPct, a.FixedPct);
                FixedValue.Text = $"{FixedSlider.Value:0}%";

                List<FanCurve> curves;
                lock (App.Config) curves = App.Config.Curves.ToList();
                CurveCombo.ItemsSource = curves;
                CurveCombo.SelectedItem = curves.FirstOrDefault(c => c.Id == a.CurveId);

                SourceCombo.ItemsSource = BuildSourceItems(placeholder: false);
                AddSourceCombo.ItemsSource = BuildSourceItems(placeholder: true);
                AddSourceCombo.SelectedIndex = 0;
                LoadCurve();
                UpdateModeUi(a.Mode);
            }
            finally { _loading = false; }
        }

        private static List<SourceItem> BuildSourceItems(bool placeholder)
        {
            var list = new List<SourceItem>();
            if (placeholder) list.Add(new SourceItem { Id = "", Title = L.T("+ הוסף חיישן נוסף…", "+ Add another sensor…") });
            foreach (var (alias, name) in SensorHub.KnownAliases)
                if (App.Hub.Sensor(alias) != null) list.Add(new SourceItem { Id = alias, Title = "★ " + name + (alias == "@cpu.temp" ? L.T("  (מומלץ ל-CPU)", "  (recommended for CPU)") : "") });
            foreach (var (model, _) in App.Hub.DriveTemps())
                list.Add(new SourceItem { Id = SensorHub.DrivePrefix + model, Title = L.T("כונן · ", "Drive · ") + model });
            foreach (var s in App.Hub.AllSensors.Where(s => s.Type == "Temperature" && !s.Id.StartsWith("virt/") && s.HardwareKind != "Storage"))
                list.Add(new SourceItem { Id = s.Id, Title = $"{s.Hardware.Trim()} · {s.DisplayName}" });
            return list;
        }

        private void SyncSourceCombo(FanCurve curve)
        {
            string? first;
            lock (App.Config) first = curve.Sources.FirstOrDefault();
            if (SourceCombo.ItemsSource is not List<SourceItem> items) return;
            bool was = _loading;
            _loading = true;
            SourceCombo.SelectedItem = items.FirstOrDefault(i => i.Id == first);
            _loading = was;
        }

        /// <summary>Picking a sensor replaces the curve's (first) source — the common case.</summary>
        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || SourceCombo.SelectedItem is not SourceItem item) return;
            var curve = CurrentCurve();
            if (curve == null) return;
            lock (App.Config)
            {
                var rest = curve.Sources.Skip(1).Where(s => s != item.Id).ToList();
                curve.Sources = new List<string> { item.Id };
                curve.Sources.AddRange(rest);
            }
            BuildSourceChips(curve);
            Commit();
            MainWindow.Instance?.ShowToast(L.T($"העקומה עוקבת עכשיו אחרי: {item.Title.TrimStart('★', ' ')}", $"The curve now follows: {item.Title.TrimStart('★', ' ')}"));
        }

        private FanCurve? CurrentCurve()
        {
            if (_sel == null) return null;
            var a = Ui.Assignment(_sel.Id);
            lock (App.Config) return App.Config.FindCurve(a.CurveId);
        }

        private void LoadCurve()
        {
            var curve = CurrentCurve();
            bool was = _loading;
            _loading = true;
            if (curve == null)
            {
                CurveEditor.Points = new List<CurvePoint>();
                SourceChips.Children.Clear();
                _loading = was;
                return;
            }
            lock (App.Config)
            {
                CurveEditor.Points = curve.Points.Select(p => new CurvePoint(p.T, p.P)).ToList();
                HystSlider.Value = curve.Hysteresis;
                UpSlider.Value = curve.StepUpPerSec;
                DownSlider.Value = curve.StepDownPerSec;
            }
            HystValue.Text = $"{HystSlider.Value:0.#}°C";
            UpValue.Text = $"{UpSlider.Value:0}%/s";
            DownValue.Text = $"{DownSlider.Value:0}%/s";
            CurveEditor.MinPct = _sel?.MinPct ?? 0;
            BuildSourceChips(curve);

            int users;
            lock (App.Config) users = App.Config.Profiles.Sum(p => p.Channels.Values.Count(x => x.Mode == FanMode.Curve && x.CurveId == curve.Id));
            CurveShared.Text = users > 1 ? L.T($"העקומה משותפת ל-{users} שיוכים (ערוצים/פרופילים) — שינוי בה משפיע על כולם. לשינוי נפרד: שכפל.",
                                                $"This curve is shared by {users} assignments (channels/profiles) — editing it changes all of them. Duplicate it to edit separately.") : "";
            _loading = was;
        }

        private void BuildSourceChips(FanCurve curve)
        {
            SourceChips.Children.Clear();
            SyncSourceCombo(curve);
            List<string> sources;
            lock (App.Config) sources = curve.Sources.ToList();
            // a single source is already shown by the selector; chips list the extra ones
            SourceChips.Visibility = sources.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var src in sources)
            {
                string title = SensorHub.KnownAliases.FirstOrDefault(a => a.Alias == src).Name ?? App.Hub.Sensor(src)?.DisplayName ?? src;
                var s = App.Hub.Sensor(src);
                if (s != null && !src.StartsWith("@")) title = $"{s.Hardware.Trim()} · {s.DisplayName}";
                if (src.StartsWith(SensorHub.DrivePrefix)) title = src[SensorHub.DrivePrefix.Length..] + L.T(" · טמפרטורה", " · temperature");
                var chip = new Border { Style = (Style)FindResource("Pill"), Margin = new Thickness(0, 2, 6, 2), Padding = new Thickness(10, 3, 4, 3) };
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = title, Foreground = Ui.Res("Accent"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                if (sources.Count > 1)
                {
                    var x = new Button { Content = "", Style = (Style)FindResource("IconBtn"), FontSize = 9, Padding = new Thickness(6, 2, 6, 2), Tag = src, ToolTip = L.T("הסר", "Remove") };
                    x.Click += RemoveSource_Click;
                    sp.Children.Add(x);
                }
                chip.Child = sp;
                SourceChips.Children.Add(chip);
            }
        }

        private void UpdateModeUi(FanMode mode)
        {
            if (_sel == null) return;
            FixedPanel.Visibility = mode == FanMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
            CurvePanel.Visibility = mode == FanMode.Curve ? Visibility.Visible : Visibility.Collapsed;
            ModeHelp.Help = mode switch
            {
                FanMode.Auto => _sel.Kind switch
                {
                    ChannelKind.Gpu => L.T("הדרייבר של NVIDIA שולט במאווררים (כולל Zero RPM בסרק) — כמו ללא Afterburner.", "The NVIDIA driver controls the fans (including Zero RPM at idle) — as without Afterburner."),
                    ChannelKind.Pump or ChannelKind.AioFans => L.T("ה-AIO יעקוב אחרי אות ה-PWM של לוח האם (MB Sync). עובד רק אם כבל הסנכרון של ה-Galahad מחובר ל-CPU_FAN.", "The AIO follows the motherboard PWM signal (MB Sync). Only works if the Galahad sync cable is plugged into CPU_FAN."),
                    _ => L.T("ה-Smart Fan של ה-BIOS שולט בערוץ הזה (ההגדרות שנשמרו ב-BIOS / GCC).", "The BIOS Smart Fan controls this header (as configured in BIOS / GCC).")
                },
                FanMode.Fixed => L.T($"מהירות קבועה ללא תלות בטמפרטורה. המינימום שהחומרה מקבלת: {_sel.MinPct}%.", $"A fixed speed regardless of temperature. Hardware minimum: {_sel.MinPct}%."),
                _ => L.T("המהירות נקבעת לפי הטמפרטורה. גרור נקודות, לחיצה כפולה מוסיפה, לחצן ימני מוחק. הקו הצהוב מראה איפה המערכת נמצאת עכשיו.",
                         "Speed follows temperature. Drag points, double-click to add, right-click to remove. The yellow line shows where the system is right now.")
            };
        }

        // ───────────── edits ─────────────

        private void Commit()
        {
            App.SaveSoon();
            App.Hub.Poke();
            foreach (var r in _rows) r.Update();
        }

        private void Mode_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading || _sel == null || sender is not RadioButton rb) return;
            var mode = Enum.Parse<FanMode>((string)rb.Tag);
            lock (App.Config)
            {
                var a = Ui.Assignment(_sel.Id);
                a.Mode = mode;
                if (mode == FanMode.Curve && App.Config.FindCurve(a.CurveId) == null)
                    a.CurveId = (_sel.Kind == ChannelKind.Gpu ? "gpu-balanced" : "cpu-balanced");
                if (App.Config.FindCurve(a.CurveId) == null) a.CurveId = App.Config.Curves.FirstOrDefault()?.Id;
            }
            LoadEditor(_sel);
            Commit();
        }

        private void FixedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            FixedValue.Text = $"{FixedSlider.Value:0}%";
            if (_loading || _sel == null) return;
            lock (App.Config) Ui.Assignment(_sel.Id).FixedPct = FixedSlider.Value;
            Commit();
        }

        private void CurveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _sel == null || CurveCombo.SelectedItem is not FanCurve c) return;
            lock (App.Config) Ui.Assignment(_sel.Id).CurveId = c.Id;
            LoadCurve();
            Commit();
        }

        private void CurveEditor_PointsChanged()
        {
            var curve = CurrentCurve();
            if (curve == null) return;
            lock (App.Config) curve.Points = CurveEditor.Points.Select(p => new CurvePoint(p.T, p.P)).ToList();
            Commit();
        }

        private void Preset_Click(object sender, RoutedEventArgs e)
        {
            var curve = CurrentCurve();
            if (curve == null || sender is not Button b || !Presets.TryGetValue((string)b.Tag, out var pts)) return;
            bool gpu = _sel?.Kind == ChannelKind.Gpu;
            lock (App.Config)
                curve.Points = pts.Select(p => new CurvePoint(gpu && (string)b.Tag != "flat" ? Math.Max(30, p.t - 5) : p.t, p.p)).ToList();
            LoadCurve();
            Commit();
        }

        private void Smoothing_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (HystValue == null || UpValue == null || DownValue == null || UpSlider == null || DownSlider == null) return;
            HystValue.Text = $"{HystSlider.Value:0.#}°C";
            UpValue.Text = $"{UpSlider.Value:0}%/s";
            DownValue.Text = $"{DownSlider.Value:0}%/s";
            if (_loading) return;
            var curve = CurrentCurve();
            if (curve == null) return;
            lock (App.Config)
            {
                curve.Hysteresis = HystSlider.Value;
                curve.StepUpPerSec = UpSlider.Value;
                curve.StepDownPerSec = DownSlider.Value;
            }
            Commit();
        }

        private void AddSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || AddSourceCombo.SelectedItem is not SourceItem item || item.Id == "") return;
            var curve = CurrentCurve();
            if (curve != null)
            {
                lock (App.Config) if (!curve.Sources.Contains(item.Id)) curve.Sources.Add(item.Id);
                BuildSourceChips(curve);
                Commit();
                MainWindow.Instance?.ShowToast(L.T("נוסף חיישן — העקומה מגיבה לחם מביניהם", "Sensor added — the curve reacts to the hottest one"));
            }
            _loading = true;
            AddSourceCombo.SelectedIndex = 0;
            _loading = false;
        }

        private void RemoveSource_Click(object sender, RoutedEventArgs e)
        {
            var curve = CurrentCurve();
            if (curve == null || sender is not Button b) return;
            lock (App.Config) if (curve.Sources.Count > 1) curve.Sources.Remove((string)b.Tag);
            BuildSourceChips(curve);
            Commit();
        }

        private void NewCurve_Click(object sender, RoutedEventArgs e)
        {
            if (_sel == null) return;
            var c = new FanCurve
            {
                Name = L.T($"עקומה {App.Config.Curves.Count + 1}", $"Curve {App.Config.Curves.Count + 1}"),
                Sources = new() { _sel.Kind == ChannelKind.Gpu ? "@gpu.temp" : "@cpu.temp" },
                Points = Presets["balanced"].Select(p => new CurvePoint(p.t, p.p)).ToList()
            };
            AssignNewCurve(c);
        }

        private void DupCurve_Click(object sender, RoutedEventArgs e)
        {
            var cur = CurrentCurve();
            if (cur == null) return;
            FanCurve copy;
            lock (App.Config) { copy = cur.Clone(); }
            copy.Id = Guid.NewGuid().ToString("N")[..8];
            copy.Name = cur + L.T(" (עותק)", " (copy)");
            AssignNewCurve(copy);
        }

        private void AssignNewCurve(FanCurve c)
        {
            if (_sel == null) return;
            lock (App.Config)
            {
                App.Config.Curves.Add(c);
                var a = Ui.Assignment(_sel.Id);
                a.Mode = FanMode.Curve;
                a.CurveId = c.Id;
            }
            LoadEditor(_sel);
            Commit();
            RenameCurve_Click(this, new RoutedEventArgs());
        }

        private void RenameCurve_Click(object sender, RoutedEventArgs e)
        {
            var c = CurrentCurve();
            if (c == null) return;
            RenameBox.Text = c.Name;
            RenamePanel.Visibility = Visibility.Visible;
            RenameBox.Focus();
            RenameBox.SelectAll();
        }

        private void RenameSave_Click(object sender, RoutedEventArgs e)
        {
            var c = CurrentCurve();
            if (c != null && !string.IsNullOrWhiteSpace(RenameBox.Text))
            {
                lock (App.Config) c.Name = RenameBox.Text.Trim();
                if (_sel != null) LoadEditor(_sel);
                Commit();
            }
            RenamePanel.Visibility = Visibility.Collapsed;
        }

        private void RenameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) RenameSave_Click(sender, e);
            if (e.Key == Key.Escape) RenamePanel.Visibility = Visibility.Collapsed;
        }

        private void DeleteCurve_Click(object sender, RoutedEventArgs e)
        {
            var c = CurrentCurve();
            if (c == null || _sel == null) return;
            int users;
            lock (App.Config)
            {
                users = App.Config.Profiles.Sum(p => p.Channels.Count(kv => kv.Value.Mode == FanMode.Curve && kv.Value.CurveId == c.Id && !(p.Id == App.Config.ActiveProfileId && kv.Key == _sel.Id)));
                if (App.Config.Curves.Count <= 1) users = int.MaxValue;
            }
            if (users > 0)
            {
                MainWindow.Instance?.ShowToast(users == int.MaxValue ? L.T("חייבת להישאר לפחות עקומה אחת", "At least one curve must remain")
                    : L.T($"העקומה בשימוש ב-{users} שיוכים נוספים — שנה אותם קודם", $"The curve is used by {users} other assignments — change those first"), MainWindow.ToastKind.Warn);
                return;
            }
            lock (App.Config)
            {
                App.Config.Curves.Remove(c);
                var a = Ui.Assignment(_sel.Id);
                a.CurveId = App.Config.Curves.First().Id;
            }
            LoadEditor(_sel);
            Commit();
        }

        private void NameBox_Commit(object sender, RoutedEventArgs e)
        {
            if (_loading || _sel == null) return;
            string name = NameBox.Text.Trim();
            lock (App.Config)
            {
                if (string.IsNullOrEmpty(name) || name == _sel.DefaultName) App.Config.ChannelLabels.Remove(_sel.Id);
                else App.Config.ChannelLabels[_sel.Id] = name;
            }
            Commit();
        }

        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { NameBox_Commit(sender, e); Keyboard.ClearFocus(); }
        }

        private void Hidden_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading || _sel == null) return;
            lock (App.Config)
            {
                App.Config.HiddenChannels.Remove(_sel.Id);
                if (HiddenBox.IsChecked == true) App.Config.HiddenChannels.Add(_sel.Id);
            }
            App.SaveSoon();
            if (HiddenBox.IsChecked == true) MainWindow.Instance?.ShowToast(L.T("הערוץ הוסתר מהרשימות (ממשיך לפעול לפי ההגדרה). \"הצג ערוצים מוסתרים\" מחזיר אותו.", "Channel hidden from lists (it keeps running as configured). \"Show hidden channels\" brings it back."));
            _channelSig = "";
            RebuildRows();
        }

        // ───────────── "drive under this fan" (M.2 fans) ─────────────

        private sealed class DriveItem
        {
            public string Alias { get; init; } = "";
            public string Title { get; init; } = "";
            public override string ToString() => Title;
        }

        /// <summary>Curves used by this channel in any profile.</summary>
        private List<FanCurve> CurvesOf(string channelId)
        {
            lock (App.Config)
                return App.Config.Profiles.Select(p => p.Channels.TryGetValue(channelId, out var a) && a.Mode == FanMode.Curve ? App.Config.FindCurve(a.CurveId) : null)
                    .OfType<FanCurve>().Distinct().ToList();
        }

        private void BuildDriveCombo(FanChannel ch)
        {
            DrivePanel.Visibility = ch.Kind == ChannelKind.NvmeFan ? Visibility.Visible : Visibility.Collapsed;
            if (ch.Kind != ChannelKind.NvmeFan) return;
            var items = new List<DriveItem> { new() { Alias = "@nvme.temp", Title = L.T("הכונן החם ביותר (כל ה-NVMe)", "Hottest drive (any NVMe)") } };
            foreach (var (model, sensor) in App.Hub.DriveTemps())
                items.Add(new DriveItem { Alias = SensorHub.DrivePrefix + model, Title = $"{model}  ·  {(sensor.HasValue ? $"{sensor.Value:0}°C" : "—")}" });
            DriveCombo.ItemsSource = items;
            string? current;
            lock (App.Config) current = CurvesOf(ch.Id).SelectMany(c => c.Sources).FirstOrDefault();
            DriveCombo.SelectedItem = items.FirstOrDefault(i => i.Alias == current) ?? items[0];
        }

        private void DriveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || _sel == null || DriveCombo.SelectedItem is not DriveItem item) return;
            lock (App.Config)
                foreach (var c in CurvesOf(_sel.Id)) c.Sources = new List<string> { item.Alias };
            LoadCurve();
            Commit();
            MainWindow.Instance?.ShowToast(L.T($"המאוורר עוקב עכשיו אחרי: {item.Title}", $"The fan now follows: {item.Title}"));
        }

        private void Identify_Click(object sender, RoutedEventArgs e)
        {
            if (_sel == null) return;
            if (_sel.Blocked) { MainWindow.Instance?.ShowToast(L.T("הערוץ בשליטת תוכנה אחרת — סגור אותה קודם", "Another app controls this channel — close it first"), MainWindow.ToastKind.Warn); return; }
            App.Hub.Identify(_sel.Id);
            MainWindow.Instance?.ShowToast(L.T($"\"{Ui.ChannelName(_sel)}\" מסתובב ב-100% ל-6 שניות — הקשב/הסתכל איזה מאוורר מאיץ", $"\"{Ui.ChannelName(_sel)}\" spins at 100% for 6 seconds — listen/look for the fan that speeds up"));
        }

        // ───────────── live ─────────────

        public void Refresh()
        {
            RebuildRows();
            foreach (var r in _rows) r.Update();
            if (_sel == null) return;
            var ch = _sel;
            LiveRpm.Text = Ui.RpmText(ch);
            LivePct.Text = ch.AppliedPct is double p ? $"{p:0}%" : Ui.PctText(ch);
            LiveTemp.Text = ch.SourceTemp is double t ? $"{t:0.0}°C" : "—";
            var a = Ui.Assignment(ch.Id);
            LiveState.Text = App.Hub.IsIdentifying(ch.Id) ? L.T("זיהוי · 100%", "Identify · 100%") : ch.Emergency ? L.T("חירום תרמי · 100%", "Thermal emergency · 100%") : ch.Blocked ? L.T("קריאה בלבד", "Read only") : Ui.ModeText(a, ch);
            LiveState.Foreground = Ui.Res(ch.Emergency ? "Hot" : ch.Blocked ? "Warn" : "Text");
            ErrorBox.Visibility = ch.LastError != null || ch.Blocked ? Visibility.Visible : Visibility.Collapsed;
            ErrorText.Text = ch.Blocked
                ? L.T($"{Conflicts.Apps.FirstOrDefault(x => x.Key == ch.ConflictGroup)?.Title} פתוח ושולט בערוץ הזה. השינויים יישמרו ויוחלו ברגע שהוא ייסגר.",
                      $"{Conflicts.Apps.FirstOrDefault(x => x.Key == ch.ConflictGroup)?.Title} is open and controls this channel. Changes are saved and applied as soon as it closes.")
                : ch.LastError ?? "";

            CurveEditor.LiveTemp = ch.SourceTemp;
            CurveEditor.LivePct = ch.SourceTemp != null ? ch.AppliedPct ?? ch.TargetPct : null;
            CurveEditor.InvalidateVisual();

            var series = new List<ChartSeries>();
            if (ch.RpmSensorId != null && Ui.Series(ch.RpmSensorId, "RPM", Ui.Violet, 600, "0") is { } rpm) series.Add(rpm);
            RpmChart.SetSeries(series);

            var src = new List<ChartSeries>();
            var curve = a.Mode == FanMode.Curve ? CurrentCurve() : null;
            if (curve != null)
            {
                List<string> sources;
                lock (App.Config) sources = curve.Sources.ToList();
                int i = 0;
                foreach (var sid in sources)
                {
                    var sensor = App.Hub.Sensor(sid);
                    if (sensor == null) continue;
                    string label = SensorHub.KnownAliases.FirstOrDefault(x => x.Alias == sid).Name ?? sensor.DisplayName;
                    if (Ui.Series(sid, label, Ui.Palette[(i++ + 2) % Ui.Palette.Length], 600) is { } s) src.Add(s);
                }
            }
            SrcChart.SetSeries(src);
            SrcChart.Visibility = src.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var legend = series.Concat(src).ToList();
            ChartLegend.Update(HistLegend, null, legend, "");
        }
    }
}
