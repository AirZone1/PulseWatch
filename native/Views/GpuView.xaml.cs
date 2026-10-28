using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PulseWatch.Controls;
using PulseWatch.Core;

namespace PulseWatch.Views
{
    public partial class GpuView : UserControl, IView
    {
        private const string FanChannelId = "gpu:fans";
        private bool _init;
        private bool _loading;
        private GpuTune? _hw;
        private Dictionary<int, int> _hwCurve = new();
        private DateTime _lastCurveRead = DateTime.MinValue;
        private bool _curveLoading;

        public GpuView()
        {
            InitializeComponent();
            for (int i = 1; i <= 5; i++) SaveSlotCombo.Items.Add(i.ToString());
            SaveSlotCombo.SelectedIndex = 0;
            for (int mv = 700; mv <= 1100; mv += 25) UvVolt.Items.Add(mv.ToString());
            UvVolt.SelectedItem = "875";
            Curve.Changed += UpdateCurveState;
            Curve.RangeChanged += Curve_RangeChanged;
            Curve.OffsetDragStarted += () => _dragStartUv = _uvMv;
            Curve.OffsetDragged += lowered => SetUndervolt(Math.Clamp(_dragStartUv + lowered, -50, 200), live: true);
            Curve.OffsetDragEnded += () => SetUndervolt(_uvMv, live: false);
            FanEditor.PointsChanged += FanEditor_PointsChanged;
            FanEditor.PointsChanging += () => FanEditor.InvalidateVisual();
            _curveIntro = L.T(
                "שתי תצוגות של אותה עקומה:\n• מתח לכל תדר (ברירת מחדל): כמה מתח הכרטיס צריך לכל תדר. Undervolt = הקו התכלת יורד מתחת לקו המקווקו; השטח הירוק הוא המתח שנחסך. הקו נגמר בתדר ובמתח המקסימליים.\n" +
                "• תצוגת Afterburner: תדר לכל מתח, כמו ב-MSI Afterburner. שם אותו Undervolt נראה כקו שעלה — כי NVIDIA שומרת את העקומה כתדר לכל נקודת מתח. שם אפשר לגרור נקודות.\n" +
                "הכרטיס בוחר בכל רגע את התדר הגבוה ביותר שמגבלות ההספק והטמפרטורה מאפשרות.",
                "Two views of the same curve:\n• Voltage per clock (default): how much voltage the card needs for each clock. Undervolt = the cyan line drops below the dashed one; the green area is the voltage saved. The line ends at the highest clock and voltage.\n" +
                "• Afterburner view: clock per voltage, as in MSI Afterburner. There the same undervolt looks like the line went up — because NVIDIA stores the curve as a clock per voltage point. Points can be dragged there.\n" +
                "At any moment the card picks the highest clock its power and temperature limits allow.");
        }

        private NvidiaGpu? Gpu => App.Hub.Gpu;
        private string _curveIntro = "";

        public void OnShown()
        {
            if (Gpu == null)
            {
                // the hub opens the card a moment after start: until then this is "connecting", not "missing",
                // and the page must not stay disabled once the card shows up (a disabled page does not even scroll)
                bool starting = !App.Hub.Ready;
                GpuTitle.Text = starting ? L.T("מתחבר לכרטיס המסך…", "Connecting to the graphics card…") : L.T("לא נמצא כרטיס NVIDIA", "No NVIDIA card found");
                GpuSub.Text = starting ? "" : L.T("NVML לא זמין — בדוק שהדרייבר של NVIDIA מותקן.", "NVML unavailable — check the NVIDIA driver is installed.");
                IsEnabled = starting;
                return;
            }
            IsEnabled = true;
            InitFromHardware();
            BuildSlots();
            LoadFan();
            lock (App.Config) ApplyOnStart.IsChecked = App.Config.Gpu.ApplyOnStartup;
            _ = LoadCurveAsync(keepEdits: false);
        }

        private void InitFromHardware()
        {
            var g = Gpu;
            var live = App.Hub.GpuLive;
            if (g == null || live == null) return;
            _loading = true;
            var l = g.Limits;
            GpuTitle.Text = g.Name;
            GpuSub.Text = L.T($"דרייבר {g.Driver}   ·   {l.FanCount} מאווררים   ·   בוסט מקסימלי {l.MaxCoreClock} MHz", $"Driver {g.Driver}   ·   {l.FanCount} fans   ·   max boost {l.MaxCoreClock} MHz");
            PlSlider.Minimum = l.PowerMinW;
            PlSlider.Maximum = l.PowerMaxW;
            TlSlider.Minimum = l.TempLimitMin;
            TlSlider.Maximum = l.TempLimitMax;
            TempHelp.Help = L.T(
                $"יעד רך: מעל הטמפרטורה הזו GPU Boost מוריד תדרים בהדרגה כדי לא להתחמם יותר (טווח {l.TempLimitMin}–{l.TempLimitMax}°C).\n\n" +
                $"הגנות קבועות בכרטיס (לא ניתנות לשינוי):\n• האטה חדה בחומרה: {l.SlowdownTemp}°C\n• כיבוי לשמירה על הכרטיס: {l.ShutdownTemp}°C\n• זיכרון GDDR6X מאט בסביבות 110°C\n\n" +
                "ובנוסף PulseWatch מעלה את כל המאווררים ל-100% מעל סף ה-GPU שבהגדרות (ברירת מחדל 87°C).",
                $"Soft target: above this temperature GPU Boost lowers clocks gradually to stay cool (range {l.TempLimitMin}–{l.TempLimitMax} °C).\n\n" +
                $"Fixed protections in the card (cannot be changed):\n• Hardware slowdown: {l.SlowdownTemp} °C\n• Shutdown to protect the card: {l.ShutdownTemp} °C\n• GDDR6X memory throttles around 110 °C\n\n" +
                "On top of that PulseWatch raises every fan to 100% above the GPU threshold in Settings (default 87 °C).");
            SetRanges();
            _hw = new GpuTune { PowerLimitW = (int)Math.Round(live.PowerLimitW), CoreOffsetMhz = live.CoreOffset, MemOffsetMhz = live.MemOffset, TempLimitC = live.TempLimit };
            if (!_init) LoadTune(_hw, curve: false);
            _init = true;
            _loading = false;
            UpdateTexts();
        }

        private void SetRanges()
        {
            var l = Gpu!.Limits;
            bool wide = WideRange.IsChecked == true;
            CoreSlider.Minimum = wide ? l.CoreOffsetMin : Math.Max(l.CoreOffsetMin, -300);
            CoreSlider.Maximum = wide ? l.CoreOffsetMax : Math.Min(l.CoreOffsetMax, 300);
            MemSlider.Minimum = wide ? l.MemOffsetMin : Math.Max(l.MemOffsetMin, -500);
            MemSlider.Maximum = wide ? l.MemOffsetMax : Math.Min(l.MemOffsetMax, 1500);
        }

        private void WideRange_Changed(object sender, RoutedEventArgs e) { if (Gpu != null) { SetRanges(); UpdateTexts(); } }

        private void LoadTune(GpuTune t, bool curve)
        {
            bool was = _loading;
            _loading = true;
            PlSlider.Value = t.PowerLimitW;
            if (t.TempLimitC > 0) TlSlider.Value = t.TempLimitC;
            if (t.CoreOffsetMhz < CoreSlider.Minimum || t.CoreOffsetMhz > CoreSlider.Maximum || t.MemOffsetMhz < MemSlider.Minimum || t.MemOffsetMhz > MemSlider.Maximum)
            {
                WideRange.IsChecked = true;
                SetRanges();
            }
            CoreSlider.Value = t.CoreOffsetMhz;
            MemSlider.Value = t.MemOffsetMhz;
            if (curve)
            {
                foreach (var p in Curve.Points) p.DeltaMhz = t.CurveDeltas != null && t.CurveDeltas.TryGetValue(p.Index, out var d) ? d : 0;
                Curve.InvalidateVisual();
                UpdateCurveState();
            }
            _loading = was;
            UpdateTexts();
        }

        private GpuTune Current() => new()
        {
            PowerLimitW = (int)Math.Round(PlSlider.Value),
            TempLimitC = (int)Math.Round(TlSlider.Value),
            CoreOffsetMhz = (int)Math.Round(CoreSlider.Value),
            MemOffsetMhz = (int)Math.Round(MemSlider.Value),
            CurveDeltas = Curve.Points.Count > 0 ? Curve.Deltas() : null,
            CapMv = CapOf(Curve.Points)
        };

        /// <summary>The voltage where a flat-topped curve reaches its top clock (every point above runs the same clock), else null.</summary>
        private static double? CapOf(IReadOnlyList<VfCurveEditor.Pt> pts)
        {
            if (pts.Count == 0 || pts.All(p => p.DeltaMhz == 0)) return null;
            double top = pts.Max(p => p.Target);
            var first = pts.OrderBy(p => p.Mv).First(p => p.Target >= top - 0.5);
            var above = pts.Where(p => p.Mv > first.Mv).ToList();
            // the stock curve is flat by itself near its own top; only a cap well below that counts
            double stockTopMv = pts.Where(p => p.BaseMhz >= pts.Max(q => q.BaseMhz)).Min(p => p.Mv);
            return above.Count > 0 && first.Mv < stockTopMv - 1 && above.All(p => Math.Abs(p.Target - top) < 0.5) ? first.Mv : null;
        }

        private void UpdateTexts()
        {
            if (Gpu == null || PlBox == null || TlBox == null || CoreBox == null || MemBox == null || DirtyPill == null) return;
            var l = Gpu.Limits;
            var t = Current();
            if (!PlBox.IsKeyboardFocused) PlBox.Text = t.PowerLimitW.ToString();
            if (!TlBox.IsKeyboardFocused) TlBox.Text = t.TempLimitC.ToString();
            if (!CoreBox.IsKeyboardFocused) CoreBox.Text = (t.CoreOffsetMhz > 0 ? "+" : "") + t.CoreOffsetMhz;
            if (!MemBox.IsKeyboardFocused) MemBox.Text = (t.MemOffsetMhz > 0 ? "+" : "") + t.MemOffsetMhz;
            int def = l.PowerDefaultW > 0 ? l.PowerDefaultW : 1;
            PlPct.Text = L.T($"{t.PowerLimitW * 100.0 / def:0}% מברירת המחדל ({l.PowerDefaultW}W)", $"{t.PowerLimitW * 100.0 / def:0}% of default ({l.PowerDefaultW} W)");
            PlHint.Help = L.T($"כמה וואט הכרטיס מורשה למשוך. טווח {l.PowerMinW}–{l.PowerMaxW}W, ברירת מחדל {l.PowerDefaultW}W. הורדה = פחות חום ורעש (ובעומס מעט פחות ביצועים), העלאה = בוסט גבוה יותר בעומס.",
                              $"How many watts the card may draw. Range {l.PowerMinW}–{l.PowerMaxW} W, default {l.PowerDefaultW} W. Lower = less heat and noise (slightly less performance under load), higher = more boost under load.");
            bool dirty = _hw != null && (t.PowerLimitW != _hw.PowerLimitW || t.TempLimitC != _hw.TempLimitC || t.CoreOffsetMhz != _hw.CoreOffsetMhz || t.MemOffsetMhz != _hw.MemOffsetMhz || CurveDirty());
            DirtyPill.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_loading) UpdateTexts();
        }

        private void ValBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CommitBox((TextBox)sender); Keyboard.ClearFocus(); }
        }

        private void ValBox_LostFocus(object sender, RoutedEventArgs e) => CommitBox((TextBox)sender);

        private void CommitBox(TextBox box)
        {
            if (!int.TryParse(box.Text.Replace("+", "").Trim(), out int v)) { UpdateTexts(); return; }
            Slider s = (string)box.Tag switch { "pl" => PlSlider, "tl" => TlSlider, "core" => CoreSlider, _ => MemSlider };
            if ((v < s.Minimum || v > s.Maximum) && (s == CoreSlider || s == MemSlider)) { WideRange.IsChecked = true; SetRanges(); }
            s.Value = Math.Clamp(v, s.Minimum, s.Maximum);
            UpdateTexts();
        }

        private async Task ApplyTune(GpuTune t, string okMessage)
        {
            ApplyBtn.IsEnabled = false;
            try
            {
                var errors = await App.Hub.ApplyGpuTune(t);
                if (errors.Count == 0)
                {
                    lock (App.Config) App.Config.Gpu.Applied = t.Clone();
                    App.SaveSoon();
                    MainWindow.Instance?.ShowToast(okMessage);
                }
                else MainWindow.Instance?.ShowToast(string.Join("\n", errors), MainWindow.ToastKind.Error);
                var live = App.Hub.GpuLive;
                if (live != null) _hw = new GpuTune { PowerLimitW = (int)Math.Round(live.PowerLimitW), CoreOffsetMhz = live.CoreOffset, MemOffsetMhz = live.MemOffset, TempLimitC = live.TempLimit };
                await LoadCurveAsync(keepEdits: false);
                UpdateTexts();
            }
            finally { ApplyBtn.IsEnabled = true; }
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            var t = Current();
            int pts = t.CurveDeltas?.Count ?? 0;
            await ApplyTune(t, L.T($"הוחל: Power {t.PowerLimitW}W · Core {t.CoreOffsetMhz:+0;-0;0} · Mem {t.MemOffsetMhz:+0;-0;0} · Temp {t.TempLimitC}°C" + (pts > 0 ? $" · עקומה ({pts} נקודות)" : ""),
                                         $"Applied: Power {t.PowerLimitW} W · Core {t.CoreOffsetMhz:+0;-0;0} · Mem {t.MemOffsetMhz:+0;-0;0} · Temp {t.TempLimitC} °C" + (pts > 0 ? $" · curve ({pts} points)" : "")));
        }

        private void Revert_Click(object sender, RoutedEventArgs e)
        {
            if (_hw != null) LoadTune(_hw, curve: false);
            foreach (var p in Curve.Points) p.DeltaMhz = _hwCurve.TryGetValue(p.Index, out var d) ? d : 0;
            Curve.InvalidateVisual();
            UpdateCurveState();
        }

        private async void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (Gpu == null) return;
            int temp;
            lock (App.Config) temp = App.Config.Gpu.DefaultTempLimitC ?? 83;
            var t = new GpuTune { PowerLimitW = Gpu.Limits.PowerDefaultW, CoreOffsetMhz = 0, MemOffsetMhz = 0, TempLimitC = temp, CurveDeltas = null };
            LoadTune(t, curve: true);
            await ApplyTune(t, L.T("כרטיס המסך הוחזר להגדרות היצרן (כולל העקומה)", "Graphics card back to factory settings (curve included)"));
        }

        // ───────────── V/F curve ─────────────

        private async Task LoadCurveAsync(bool keepEdits)
        {
            if (Gpu?.Private?.CurveSupported != true)
            {
                CurveTools.Visibility = Visibility.Collapsed;
                Curve.Visibility = Visibility.Collapsed;
                CurveNote.Text = L.T("הדרייבר/הכרטיס לא חושפים את עקומת המתח.", "This card/driver does not expose the voltage curve.");
                return;
            }
            if (_curveLoading) return;
            _curveLoading = true;
            try
            {
                var pts = await App.Hub.ReadCurve();
                if (pts.Count == 0) return;
                _lastCurveRead = DateTime.UtcNow;
                var pending = keepEdits && Curve.Points.Count > 0 ? Curve.Points.ToDictionary(p => p.Index, p => p.DeltaMhz) : null;
                _hwCurve = pts.ToDictionary(p => p.Index, p => (int)Math.Round(p.DeltaMhz));
                Curve.SetCurve(pts.Select(p => new VfCurveEditor.Pt
                {
                    Index = p.Index,
                    Mv = p.VoltageMv,
                    // the curve status includes the global core offset; the dashed line is the true stock curve
                    BaseMhz = p.BaseMhz - (App.Hub.GpuLive?.CoreOffset ?? 0),
                    DeltaMhz = pending != null && pending.TryGetValue(p.Index, out var d) ? d : (int)Math.Round(p.DeltaMhz)
                }));
                UpdateCurveState();
            }
            catch (Exception ex) { Log.Warn("Curve read: " + ex.Message); }
            finally { _curveLoading = false; }
        }

        private bool CurveDirty() => Curve.Points.Any(p => p.DeltaMhz != (_hwCurve.TryGetValue(p.Index, out var d) ? d : 0));

        private void CurveView_Checked(object sender, RoutedEventArgs e)
        {
            if (Curve == null || ManualTools == null) return;
            Curve.VoltageView = ViewVolt.IsChecked == true;
            ManualTools.Visibility = Curve.VoltageView ? Visibility.Collapsed : Visibility.Visible;
            UpdateSummary();
        }

        /// <summary>The three plain numbers above the graph and the legend, for the curve as it is in the editor.</summary>
        private void UpdateSummary()
        {
            if (SumClock == null || Curve.Points.Count == 0) return;
            var (sf, smv) = Curve.StockTop();
            var (nf, nmv) = Curve.NewTop();
            bool edited = Curve.Points.Any(p => p.DeltaMhz != 0);
            SumClock.Text = $"{nf:0} MHz";
            SumClockSub.Text = !edited || Math.Abs(nf - sf) < 1 ? L.T("כמו במקור", "same as stock") : L.T($"במקור {sf:0} MHz ({nf - sf:+0;−0})", $"stock {sf:0} MHz ({nf - sf:+0;−0})");
            SumVolt.Text = $"{nmv:0.##} mV";
            SumVoltSub.Text = !edited || Math.Abs(nmv - smv) < 0.5 ? L.T("כמו במקור", "same as stock") : L.T($"במקור {smv:0.##} mV ({nmv - smv:+0.##;−0.##})", $"stock {smv:0.##} mV ({nmv - smv:+0.##;−0.##})");
            double avg = Curve.AverageSavingMv();
            bool same = !edited || double.IsNaN(avg) || Math.Abs(avg) < 1;
            SumSave.Text = same ? L.T("כמו במקור", "Same as stock") : avg > 0 ? L.T($"−{avg:0} mV", $"−{avg:0} mV") : $"+{-avg:0} mV";
            SumSave.Foreground = (Brush)FindResource(same ? "Text" : avg > 0 ? "Good" : "Hot");
            SumSaveSub.Text = same ? "" : avg > 0 ? L.T("בממוצע, באותם תדרים", "on average, at the same clocks") : L.T("יותר מתח מהמקור באותם תדרים", "more voltage than stock at the same clocks");
            CurveLegend.Text = Curve.VoltageView
                ? L.T("כמה מתח צריך כל תדר. מקווקו = מקור · תכלת = שלך · ירוק = מתח שנחסך. גרור את הקו התכלת למטה/למעלה כדי לקבוע ידנית (סמן טווח קודם כדי להזיז רק חלק).",
                      "How much voltage each clock needs. Dashed = stock · cyan = yours · green = voltage saved. Drag the cyan line down/up to set it by hand (mark a range first to move only part).")
                : L.T("כמו ב-Afterburner: תדר לכל מתח. גרור נקודה למעלה/למטה · Shift+גרירה מזיזה הכל.",
                      "Like Afterburner: clock per voltage. Drag a point up/down · Shift+drag moves everything.");
        }

        private void UpdateCurveState()
        {
            UpdatePresetDesc();
            UpdateSummary();
            int edited = Curve.Points.Count(p => p.DeltaMhz != 0);
            bool dirty = CurveDirty();
            CurveDirtyPill.Visibility = edited > 0 || dirty ? Visibility.Visible : Visibility.Collapsed;
            CurveDirtyText.Text = dirty ? L.T($"{edited} נקודות ערוכות · לא הוחל", $"{edited} edited points · not applied")
                                        : L.T($"{edited} נקודות ערוכות · פעיל", $"{edited} edited points · active");
            CurveNote.Text = "";
            CurveHelp.Help = _curveIntro + "\n\n" + L.T(
                "ריחוף מעל נקודה מראה את התדר, המתח, וכמה מתח המקור היה צריך לאותו תדר. גרירה על שטח ריק מסמנת טווח. עיגול צהוב = איפה הכרטיס עובד עכשיו (רק בעומס). " +
                "עם תקרת מתח PulseWatch בודק כל 5 שניות שהחלק העליון נשאר שטוח (העקומה המקורית זזה ב-15 MHz עם הטמפרטורה). השינויים נכנסים לתוקף בלחיצה על \"החל על הכרטיס\".",
                "Hovering a point shows its clock, voltage and how much voltage stock needed for that clock. Dragging across an empty area marks a range. Yellow ring = where the card runs now (under load only). " +
                "With a voltage ceiling PulseWatch checks every 5 seconds that the top stays flat (the stock curve moves by 15 MHz with temperature). Changes take effect when you press Apply to card.");
            UpdateTexts();
        }

        private static int Snap(double mhz) => (int)(Math.Round(mhz / 15.0) * 15);

        private void Undervolt_Click(object sender, RoutedEventArgs e)
        {
            if (Curve.Points.Count == 0 || !double.TryParse(UvVolt.SelectedItem as string, out double v) || !int.TryParse(UvFreq.Text, out int f)) return;
            var at = BuildCap(v, f);
            MainWindow.Instance?.ShowToast(L.T($"העקומה נבנתה: מקסימום {f} MHz ב-{at.Mv:0} mV. לחץ \"החל\" כדי להפעיל, ובדוק יציבות במשחק.",
                                               $"Curve built: max {f} MHz at {at.Mv:0} mV. Press Apply to activate and check stability in a game."));
        }

        /// <summary>
        /// Voltage cap: the clock <paramref name="f"/> is reached already at voltage <paramref name="v"/> and everything
        /// above is flattened to f — so the card never asks for more than v. Points below v keep their stock clocks
        /// (capped at f), so the curve never runs ahead of stock at low voltage.
        /// </summary>
        private VfCurveEditor.Pt BuildCap(double v, double f)
        {
            var at = Curve.Points.OrderBy(p => Math.Abs(p.Mv - v)).First();
            foreach (var p in Curve.Points)
            {
                double target = p.Mv < at.Mv ? Math.Min(p.BaseMhz, f) : f;
                p.DeltaMhz = Math.Clamp(Snap(target - p.BaseMhz), -1000, 1000);
            }
            Curve.InvalidateVisual();
            UpdateCurveState();
            return at;
        }

        // ───────────── undervolt (voltage offset) ─────────────

        private double _uvMv = 25, _uvCap, _dragStartUv;
        private bool _settingUv;

        /// <summary>
        /// Sets the undervolt amount (mV, positive = less voltage) from a chip or from dragging the line, and — when
        /// dragging — rebuilds the curve right away so the line follows the mouse. Applying still needs "Apply to card".
        /// </summary>
        private void SetUndervolt(double mv, bool live)
        {
            _uvMv = mv;
            _settingUv = true;
            foreach (var rb in UvChips()) rb.IsChecked = TryTag(rb, out double t) && Math.Abs(t - mv) < 0.01;
            _settingUv = false;
            if (live && Curve.Points.Count > 0)
            {
                var range = RangeSel.IsChecked == true ? Curve.Range : null;
                var plan = PlanUndervolt(_uvMv, range, _uvCap);
                foreach (var p in Curve.Points) p.DeltaMhz = Math.Clamp(Snap(plan[p.Index] - p.BaseMhz), -1000, 1000);
                CoreSlider.Value = 0;
                Curve.InvalidateVisual();
            }
            UpdateCurveState();
        }

        private IEnumerable<RadioButton> UvChips() =>
            UvValue?.Parent is Panel host ? host.Children.OfType<Border>().SelectMany(b => (b.Child as Panel)?.Children.OfType<RadioButton>() ?? Enumerable.Empty<RadioButton>())
                                                         .Where(r => r.GroupName == "uvmv") : Enumerable.Empty<RadioButton>();

        private double StockMax => Curve.Points.Count == 0 ? double.NaN : Curve.Points.Max(p => p.BaseMhz);

        /// <summary>
        /// Undervolt as a voltage offset: inside the chosen range every point gets the stock clock of the point
        /// <paramref name="mv"/> higher, i.e. each clock is reached that many mV lower — the clocks themselves do not
        /// change, the curve moves left. Above the range the curve never dips below what the range reached (the GPU
        /// expects a rising curve). An optional ceiling flattens everything above it to the clock at the ceiling.
        /// Returns the per-point targets (MHz) without touching the editor.
        /// </summary>
        private Dictionary<int, double> PlanUndervolt(double mv, (double from, double to)? range, double cap)
        {
            var pts = Curve.Points.OrderBy(p => p.Mv).ToList();
            double StockAtOrTop(double v) => v >= pts[^1].Mv ? pts[^1].BaseMhz : pts.OrderBy(p => Math.Abs(p.Mv - v)).First().BaseMhz;
            var t = new Dictionary<int, double>();
            double run = 0;
            foreach (var p in pts)
            {
                bool inRange = range == null || (p.Mv >= range.Value.from - 0.1 && p.Mv <= range.Value.to + 0.1);
                double f = inRange ? StockAtOrTop(p.Mv + mv) : p.BaseMhz;
                run = Math.Max(run, f);
                t[p.Index] = run;
            }
            if (cap > 0)
            {
                var c = pts.OrderBy(p => Math.Abs(p.Mv - cap)).First();
                double top = t[c.Index];
                foreach (var p in pts.Where(p => p.Mv > c.Mv)) t[p.Index] = top;
            }
            return t;
        }

        private void UpdatePresetDesc()
        {
            if (PresetDesc == null || RangeText == null) return;
            if (UvValue != null) UvValue.Text = _uvMv == 0 ? "0 mV" : _uvMv > 0 ? $"−{_uvMv:0.##} mV" : $"+{-_uvMv:0.##} mV";
            if (Curve.Points.Count == 0) { PresetDesc.Text = L.T("טוען את העקומה מהכרטיס…", "Reading the curve from the card…"); return; }
            var range = RangeSel.IsChecked == true ? Curve.Range : null;
            RangeText.Text = Curve.Range is (double rf, double rt)
                ? $"{rf:0.#}–{rt:0.#} mV"
                : L.T("גרור על שטח ריק בגרף כדי לסמן טווח", "Drag across an empty area of the graph to mark a range");
            if (RangeSel.IsChecked == true && Curve.Range == null)
            {
                PresetDesc.Text = L.T("סמן טווח בגרף: לחץ על שטח ריק וגרור לרוחב.", "Mark a range on the graph: press on an empty area and drag sideways.");
                return;
            }
            if (_uvMv == 0 && _uvCap == 0) { PresetDesc.Text = L.T("בחר כמה mV להוריד או תקרת מתח.", "Pick how many mV to remove or a voltage ceiling."); return; }

            var pts = Curve.Points.OrderBy(p => p.Mv).ToList();
            var t = PlanUndervolt(_uvMv, range, _uvCap);
            double peak = t.Values.Max();
            double peakMv = pts.First(p => t[p.Index] >= peak).Mv;
            double stockPeak = StockMax, stockPeakMv = Curve.StockMvFor(stockPeak);
            var biggest = pts.Where(p => p.Mv >= Curve.MinMv).OrderByDescending(p => t[p.Index] - p.BaseMhz).First();
            double maxRaise = t[biggest.Index] - biggest.BaseMhz;
            string whereHe = range is (double a, double b) ? $"בין {a:0.#} ל-{b:0.#} mV" : "בכל העקומה";
            string whereEn = range is (double a2, double b2) ? $"Between {a2:0.#} and {b2:0.#} mV" : "On the whole curve";

            string he = "", en = "";
            if (_uvMv > 0)
            {
                he = $"{whereHe}: כל תדר יקבל {_uvMv:0.#} mV פחות. התדרים עצמם לא משתנים — בגרף הקו התכלת יורד {_uvMv:0.#} mV מתחת למקווקו. ";
                en = $"{whereEn}: every clock gets {_uvMv:0.#} mV less. The clocks themselves do not change — on the graph the cyan line drops {_uvMv:0.#} mV below the dashed one. ";
            }
            if (_uvCap > 0)
            {
                he += $"תקרה: לא מעל {peakMv:0.##} mV, ולכן שיא {peak:0} MHz (במקור {stockPeak:0} MHz ב-{stockPeakMv:0.##} mV). ";
                en += $"Ceiling: never above {peakMv:0.##} mV, so a peak of {peak:0} MHz (stock {stockPeak:0} MHz at {stockPeakMv:0.##} mV). ";
            }
            else if (peak >= stockPeak)
            {
                he += $"השיא נשאר {peak:0} MHz, עכשיו ב-{peakMv:0.##} mV במקום {stockPeakMv:0.##}. ";
                en += $"The peak stays {peak:0} MHz, now at {peakMv:0.##} mV instead of {stockPeakMv:0.##}. ";
            }
            if (maxRaise > 0)
            {
                he += $"(בתצוגת Afterburner אותו שינוי נראה כתוספת של עד +{maxRaise:0} MHz בנקודת {biggest.Mv:0.##} mV — כך NVIDIA שומרת אותו.)";
                en += $"(In the Afterburner view the same change shows as up to +{maxRaise:0} MHz at the {biggest.Mv:0.##} mV point — that is how NVIDIA stores it.)";
            }
            PresetDesc.Text = L.T(he, en);
        }

        private static bool TryTag(object sender, out double v)
        {
            v = 0;
            return sender is RadioButton rb && double.TryParse((string)rb.Tag, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        private void UvAmount_Checked(object sender, RoutedEventArgs e) { if (!_settingUv && TryTag(sender, out double v)) { _uvMv = v; UpdatePresetDesc(); } }
        private void UvCap_Checked(object sender, RoutedEventArgs e) { if (TryTag(sender, out double v)) { _uvCap = v; UpdatePresetDesc(); } }

        private void UvRange_Checked(object sender, RoutedEventArgs e)
        {
            if (RangeAll?.IsChecked == true && Curve?.Range != null) Curve.ClearRange();
            UpdatePresetDesc();
        }

        private void Curve_RangeChanged()
        {
            if (Curve.Range != null && RangeSel.IsChecked != true) RangeSel.IsChecked = true;
            else if (Curve.Range == null && RangeSel.IsChecked == true) RangeAll.IsChecked = true;
            UpdatePresetDesc();
        }

        private void LoadPreset_Click(object sender, RoutedEventArgs e)
        {
            if (Curve.Points.Count == 0) return;
            var range = RangeSel.IsChecked == true ? Curve.Range : null;
            if (RangeSel.IsChecked == true && range == null) { MainWindow.Instance?.ShowToast(L.T("סמן טווח בגרף קודם", "Mark a range on the graph first"), MainWindow.ToastKind.Warn); return; }
            var t = PlanUndervolt(_uvMv, range, _uvCap);
            foreach (var p in Curve.Points) p.DeltaMhz = Math.Clamp(Snap(t[p.Index] - p.BaseMhz), -1000, 1000);
            Curve.InvalidateVisual();
            UpdateCurveState();
            // a curve edit replaces the global core offset (otherwise the two would add up)
            CoreSlider.Value = 0;
            UpdateTexts();
            MainWindow.Instance?.ShowToast(L.T("נטען לעקומה (מהעקומה המקורית) — בדוק את הגרף ולחץ \"החל\" כדי להפעיל.", "Loaded into the curve (from the stock curve) — check the graph and press Apply to activate."));
        }

        private void LoadStock_Click(object sender, RoutedEventArgs e)
        {
            if (Gpu == null) return;
            int temp;
            lock (App.Config) temp = App.Config.Gpu.DefaultTempLimitC ?? 83;
            LoadTune(new GpuTune { PowerLimitW = Gpu.Limits.PowerDefaultW, CoreOffsetMhz = 0, MemOffsetMhz = 0, TempLimitC = temp, CurveDeltas = null }, curve: true);
            MainWindow.Instance?.ShowToast(L.T("נטענו הגדרות היצרן — לחץ \"החל\" כדי להחזיר את הכרטיס למקור.", "Factory settings loaded — press Apply to return the card to stock."));
        }

        private void Flatten_Click(object sender, RoutedEventArgs e)
        {
            int sel = Curve.Selected;
            if (sel < 0 || sel >= Curve.Points.Count) { MainWindow.Instance?.ShowToast(L.T("בחר נקודה בעקומה קודם (לחיצה עליה)", "Select a point on the curve first (click it)"), MainWindow.ToastKind.Warn); return; }
            var s = Curve.Points[sel];
            foreach (var p in Curve.Points.Where(p => p.Mv > s.Mv)) p.DeltaMhz = Math.Clamp(Snap(s.Target - p.BaseMhz), -1000, 1000);
            Curve.InvalidateVisual();
            UpdateCurveState();
        }

        private void ShiftCurve(int mhz)
        {
            foreach (var p in Curve.Points) p.DeltaMhz = Math.Clamp(p.DeltaMhz + mhz, -1000, 1000);
            Curve.InvalidateVisual();
            UpdateCurveState();
        }

        private void ShiftUp_Click(object sender, RoutedEventArgs e) => ShiftCurve(15);
        private void ShiftDown_Click(object sender, RoutedEventArgs e) => ShiftCurve(-15);

        private void ResetCurve_Click(object sender, RoutedEventArgs e)
        {
            foreach (var p in Curve.Points) p.DeltaMhz = 0;
            Curve.InvalidateVisual();
            UpdateCurveState();
        }

        // ───────────── slots ─────────────

        private void BuildSlots()
        {
            Slots.Children.Clear();
            GpuTune?[] slots;
            lock (App.Config) slots = App.Config.Gpu.Slots.Select(s => s?.Clone()).ToArray();
            for (int i = 0; i < 5; i++)
            {
                var s = i < slots.Length ? slots[i] : null;
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock { Text = (i + 1).ToString(), FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
                sp.Children.Add(new TextBlock
                {
                    Text = s == null ? L.T("ריק", "empty") : $"{s.PowerLimitW}W\n{s.CoreOffsetMhz:+0;-0;0} / {s.MemOffsetMhz:+0;-0;0}" + (s.HasCurve ? "\nV/F" : ""),
                    Style = (Style)FindResource("Caption"), FontSize = 11, TextAlignment = TextAlignment.Center, FlowDirection = FlowDirection.LeftToRight, HorizontalAlignment = HorizontalAlignment.Center
                });
                var b = new Button { Content = sp, Margin = new Thickness(4), Padding = new Thickness(6, 8, 6, 8), Tag = i, IsEnabled = s != null, ToolTip = s == null ? L.T("שמור הגדרות לחריץ הזה כדי להשתמש בו", "Save settings into this slot to use it") : L.T($"החל פרופיל {i + 1}", $"Apply profile {i + 1}") };
                b.Click += Slot_Click;
                Slots.Children.Add(b);
            }
        }

        private async void Slot_Click(object sender, RoutedEventArgs e)
        {
            int i = (int)((Button)sender).Tag;
            GpuTune? t;
            lock (App.Config) t = App.Config.Gpu.Slots[i]?.Clone();
            if (t == null) return;
            LoadTune(t, curve: true);
            await ApplyTune(t, L.T($"פרופיל OC {i + 1} הוחל", $"OC profile {i + 1} applied"));
        }

        private void SaveSlot_Click(object sender, RoutedEventArgs e)
        {
            int i = SaveSlotCombo.SelectedIndex;
            if (i < 0) return;
            lock (App.Config)
            {
                if (App.Config.Gpu.Slots.Length < 5) App.Config.Gpu.Slots = App.Config.Gpu.Slots.Concat(new GpuTune?[5]).Take(5).ToArray();
                App.Config.Gpu.Slots[i] = Current();
            }
            App.SaveSoon();
            BuildSlots();
            MainWindow.Instance?.ShowToast(L.T($"נשמר לחריץ {i + 1}", $"Saved to slot {i + 1}"));
        }

        private void ApplyOnStart_Changed(object sender, RoutedEventArgs e)
        {
            lock (App.Config) App.Config.Gpu.ApplyOnStartup = ApplyOnStart.IsChecked == true;
            App.SaveSoon();
        }

        // ───────────── fan ─────────────

        private void LoadFan()
        {
            if (Gpu == null) return;
            _loading = true;
            var a = Ui.Assignment(FanChannelId);
            FanAuto.IsChecked = a.Mode == FanMode.Auto;
            FanFixed.IsChecked = a.Mode == FanMode.Fixed;
            FanCurve.IsChecked = a.Mode == FanMode.Curve;
            FanSlider.Minimum = Gpu.Limits.FanMinPct;
            FanSlider.Value = Math.Max(Gpu.Limits.FanMinPct, a.FixedPct);
            FanFixedVal.Text = $"{FanSlider.Value:0}%";
            FanFixedPanel.Visibility = a.Mode == FanMode.Fixed ? Visibility.Visible : Visibility.Collapsed;
            FanCurvePanel.Visibility = a.Mode == FanMode.Curve ? Visibility.Visible : Visibility.Collapsed;

            List<FanCurve> curves;
            FanCurve? curve;
            lock (App.Config) { curves = App.Config.Curves.ToList(); curve = App.Config.FindCurve(a.CurveId); }
            FanCurveCombo.ItemsSource = curves;
            FanCurveCombo.SelectedItem = curve;
            LoadFanCurve(curve);
            _loading = false;
        }

        private void LoadFanCurve(FanCurve? curve)
        {
            bool was = _loading;
            _loading = true;
            lock (App.Config)
            {
                FanEditor.Points = curve?.Points.Select(p => new CurvePoint(p.T, p.P)).ToList() ?? new List<CurvePoint>();
                FanVramBox.IsChecked = curve?.Sources.Contains("@gpu.memtemp") == true;
            }
            FanEditor.MinPct = Gpu?.Limits.FanMinPct ?? 30;
            _loading = was;
        }

        private FanCurve? GpuCurve()
        {
            var a = Ui.Assignment(FanChannelId);
            lock (App.Config) return App.Config.FindCurve(a.CurveId);
        }

        private void FanCurveCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || FanCurveCombo.SelectedItem is not FanCurve c) return;
            lock (App.Config) Ui.Assignment(FanChannelId).CurveId = c.Id;
            LoadFanCurve(c);
            App.SaveSoon();
            App.Hub.Poke();
        }

        private void FanEditor_PointsChanged()
        {
            var c = GpuCurve();
            if (c == null) return;
            lock (App.Config) c.Points = FanEditor.Points.Select(p => new CurvePoint(p.T, p.P)).ToList();
            App.SaveSoon();
            App.Hub.Poke();
        }

        private void FanVram_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            var c = GpuCurve();
            if (c == null) return;
            lock (App.Config)
            {
                c.Sources.Remove("@gpu.memtemp");
                if (!c.Sources.Contains("@gpu.temp")) c.Sources.Insert(0, "@gpu.temp");
                if (FanVramBox.IsChecked == true) c.Sources.Add("@gpu.memtemp");
            }
            App.SaveSoon();
            App.Hub.Poke();
        }

        private void UpdateFanTiles(GpuLive live)
        {
            if (FanRpmTiles.Children.Count != live.FanRpm.Length)
            {
                FanRpmTiles.Children.Clear();
                for (int i = 0; i < live.FanRpm.Length; i++)
                {
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock { Text = L.T($"מאוורר {i + 1}", $"Fan {i + 1}"), Style = (Style)FindResource("Caption") });
                    sp.Children.Add(new TextBlock { Style = (Style)FindResource("Num"), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Left });
                    FanRpmTiles.Children.Add(new Border { Style = (Style)FindResource("SubCard"), Margin = new Thickness(5, 0, 5, 0), Child = sp });
                }
            }
            for (int i = 0; i < live.FanRpm.Length; i++)
                if (((Border)FanRpmTiles.Children[i]).Child is StackPanel sp && sp.Children[1] is TextBlock t)
                    t.Text = live.FanRpm[i] == 0 ? L.T("0 RPM · עצור", "0 RPM · stopped") : $"{live.FanRpm[i]} RPM";
        }

        private void FanMode_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not RadioButton rb) return;
            var mode = Enum.Parse<FanMode>((string)rb.Tag);
            lock (App.Config)
            {
                var a = Ui.Assignment(FanChannelId);
                a.Mode = mode;
                if (mode == FanMode.Curve && App.Config.FindCurve(a.CurveId) == null) a.CurveId = "gpu-balanced";
            }
            App.SaveSoon();
            App.Hub.Poke();
            LoadFan();
        }

        private void FanSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (FanFixedVal == null) return;
            FanFixedVal.Text = $"{FanSlider.Value:0}%";
            if (_loading) return;
            lock (App.Config) Ui.Assignment(FanChannelId).FixedPct = FanSlider.Value;
            App.SaveSoon();
            App.Hub.Poke();
        }

        private void EditFanCurve_Click(object sender, RoutedEventArgs e) =>
            MainWindow.Instance?.Navigate("NavCooling", v => ((CoolingView)v).Select(FanChannelId));

        // ───────────── live ─────────────

        public void Refresh()
        {
            if (Gpu == null) return;
            if (!_init) { OnShown(); if (!_init) return; }
            var live = App.Hub.GpuLive;
            if (live == null) return;

            bool blocked = App.Hub.GpuBlocked;
            BlockedBox.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
            ApplyBtn.IsEnabled = !blocked;

            MClock.Text = $"{live.ClockCore} MHz";
            MMem.Text = $"{live.ClockMem} MHz";
            MVolt.Text = double.IsNaN(live.VoltageV) ? "—" : $"{live.VoltageV:0.000} V";
            MTemp.Text = Ui.Val("@gpu.temp", "0", "°C");
            MTemp.Foreground = MainWindow.TempBrush(App.Hub.Value("@gpu.temp"), 75, 84);
            MHot.Text = Ui.Val("@gpu.hotspot", "0", "°C");
            MHot.Foreground = MainWindow.TempBrush(App.Hub.Value("@gpu.hotspot"), 88, 100);
            MMemT.Text = Ui.Val("@gpu.memtemp", "0", "°C");
            MMemT.Foreground = MainWindow.TempBrush(App.Hub.Value("@gpu.memtemp"), 90, 100);
            MPower.Text = $"{live.PowerW:0} / {live.PowerLimitW:0} W";
            MLoad.Text = $"{live.LoadGpu}%";
            MFan.Text = $"{live.FanPct}% · {Ui.Val("@gpu.fanrpm", "0")}";
            MPState.Text = $"P{live.PState}";
            MReason.Text = NvidiaGpu.DescribeReasons(live.Reasons);
            MVram.Text = $"{live.VramUsedMb / 1024:0.0} / {live.VramTotalMb / 1024:0.0} GB";
            HwState.Text = L.T($"בכרטיס כרגע:  Power {live.PowerLimitW:0}W  ·  Core {live.CoreOffset:+0;-0;0} MHz  ·  Memory {live.MemOffset:+0;-0;0} MHz  ·  Temp {live.TempLimit}°C",
                               $"On the card now:  Power {live.PowerLimitW:0} W  ·  Core {live.CoreOffset:+0;-0;0} MHz  ·  Memory {live.MemOffset:+0;-0;0} MHz  ·  Temp {live.TempLimit} °C");

            // live operating point; the base curve itself moves with temperature, so re-read it every few seconds
            // at idle (P5/P8) the card runs a separate low-power table, so the point only means something under load (P0)
            Curve.LiveMv = double.IsNaN(live.VoltageV) || live.PState != 0 ? null : live.VoltageV * 1000;
            Curve.LiveMhz = live.ClockCore;
            Curve.InvalidateVisual();
            if ((DateTime.UtcNow - _lastCurveRead).TotalSeconds > 4) _ = LoadCurveAsync(keepEdits: true);

            var a = Ui.Assignment(FanChannelId);
            var ch = App.Hub.Channels.FirstOrDefault(c => c.Id == FanChannelId);
            UpdateFanTiles(live);
            if (a.Mode == FanMode.Curve && ch != null)
            {
                FanEditor.LiveTemp = ch.SourceTemp;
                FanEditor.LivePct = ch.SourceTemp != null ? ch.AppliedPct ?? 0 : null;
                FanEditor.InvalidateVisual();
            }
            FanInfo.Text = a.Mode == FanMode.Curve
                ? L.T($"עקומה: {App.Config.FindCurve(a.CurveId)}  ·  טמפרטורת מקור {(ch?.SourceTemp is double st ? $"{st:0}°C" : "—")}  ·  כרגע {(ch?.AppliedPct is double p ? $"{p:0}%" : "אוטו (Zero RPM)")}",
                      $"Curve: {App.Config.FindCurve(a.CurveId)}  ·  source {(ch?.SourceTemp is double st2 ? $"{st2:0} °C" : "—")}  ·  now {(ch?.AppliedPct is double p2 ? $"{p2:0}%" : "auto (Zero RPM)")}")
                : a.Mode == FanMode.Auto ? L.T("הדרייבר מנהל את המאווררים (עוצרים בסרק — Zero RPM).", "The driver manages the fans (they stop at idle — Zero RPM).") : "";

            var clocks = new List<ChartSeries>();
            if (Ui.Series("@gpu.clock", L.T("ליבה", "Core"), Ui.Green, 300, "0") is { } c1) clocks.Add(c1);
            if (Ui.Series("@gpu.memclock", L.T("זיכרון", "Memory"), Ui.Violet, 300, "0") is { } c2) clocks.Add(c2);
            ClockChart.SetSeries(clocks);
            ChartLegend.Update(ClockLegend, ClockChart, clocks, " MHz");

            var temps = new List<ChartSeries>();
            if (Ui.Series("@gpu.temp", L.T("ליבה", "Core"), Ui.Green, 300) is { } t1) temps.Add(t1);
            if (Ui.Series("@gpu.hotspot", "Hot Spot", Ui.Amber, 300) is { } t2) temps.Add(t2);
            if (Ui.Series("@gpu.memtemp", "VRAM", Ui.Pink, 300) is { } t3) temps.Add(t3);
            TempChart.SetSeries(temps);
            ChartLegend.Update(TempLegend, TempChart, temps, "°");
        }
    }
}
