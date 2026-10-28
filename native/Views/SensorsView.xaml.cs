using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PulseWatch.Controls;
using PulseWatch.Core;

namespace PulseWatch.Views
{
    public sealed class SensorRow : Observable
    {
        public SensorInfo Info { get; }
        public string Group { get; }
        public bool Key { get; }
        public string Help { get; }
        private readonly string _friendly;
        private string _value = "", _min = "", _max = "";
        private Brush _valueBrush = Brushes.White;
        private bool _pinned;

        public SensorRow(SensorInfo info, bool pinned)
        {
            Info = info;
            Group = info.Hardware.Trim();
            var g = SensorGlossary.Describe(info);
            _friendly = g.Name;
            Key = g.Key;
            Help = string.IsNullOrEmpty(g.Help) ? $"{info.Name}  ·  {info.Id}" : $"{g.Help}\n\n{info.Name}  ·  {info.Id}";
            _pinned = pinned;
        }

        /// <summary>User label, otherwise the plain-language name.</summary>
        public string Name
        {
            get => string.IsNullOrWhiteSpace(Info.Label) ? _friendly : Info.Label!;
            set
            {
                string v = (value ?? "").Trim();
                Info.Label = string.IsNullOrEmpty(v) || v == _friendly || v == Info.Name ? null : v;
                lock (App.Config)
                {
                    if (Info.Label == null) App.Config.SensorLabels.Remove(Info.Id);
                    else App.Config.SensorLabels[Info.Id] = Info.Label;
                }
                App.SaveSoon();
                Raise();
            }
        }

        public string Value { get => _value; set => Set(ref _value, value); }
        public string Min { get => _min; set => Set(ref _min, value); }
        public string Max { get => _max; set => Set(ref _max, value); }
        public Brush ValueBrush { get => _valueBrush; set => Set(ref _valueBrush, value); }

        public bool Pinned
        {
            get => _pinned;
            set
            {
                if (!Set(ref _pinned, value)) return;
                lock (App.Config)
                {
                    App.Config.GraphSensors.RemoveAll(id => id == Info.Id || (id.StartsWith("@") && App.Hub.AliasTarget(id) == Info.Id));
                    if (value) App.Config.GraphSensors.Add(Info.Id);
                }
                App.SaveSoon();
            }
        }

        public void Update()
        {
            Value = Info.HasValue ? $"{Info.Format(Info.Value)} {Info.Unit}" : "—";
            Min = float.IsNaN(Info.Min) ? "" : Info.Format(Info.Min);
            Max = float.IsNaN(Info.Max) ? "" : Info.Format(Info.Max);
            ValueBrush = Info.Type == "Temperature" && Info.HasValue
                ? Ui.Res(Info.Value >= 85 ? "Hot" : Info.Value >= 70 ? "Warn" : "Text")
                : Ui.Res("Text");
        }
    }

    public partial class SensorsView : UserControl, IView
    {
        private readonly ObservableCollection<SensorRow> _rows = new();
        private ICollectionView? _view;
        private int _count = -1;
        private string _typeFilter = "key";
        private int _span = 900;
        private string _chartSig = "";
        private readonly Dictionary<string, (TimeChart chart, WrapPanel legend)> _charts = new();

        public SensorsView() { InitializeComponent(); }

        public void OnShown() => BuildRows();

        private void BuildRows()
        {
            var sensors = App.Hub.AllSensors;
            if (sensors.Count == _count) return;
            _count = sensors.Count;
            HashSet<string> pinned;
            lock (App.Config) pinned = App.Config.GraphSensors.Select(id => id.StartsWith("@") ? App.Hub.AliasTarget(id) : id).ToHashSet();
            _rows.Clear();
            foreach (var s in sensors.Where(s => s.Type is not ("SmallData" or "Data" or "Factor" or "Throughput") || s.Id.StartsWith("virt/")))
                _rows.Add(new SensorRow(s, pinned.Contains(s.Id)));
            var cvs = new CollectionViewSource { Source = _rows };
            cvs.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SensorRow.Group)));
            _view = cvs.View;
            _view.Filter = Filter;
            List.ItemsSource = _view;
        }

        private bool Filter(object o)
        {
            if (o is not SensorRow r) return false;
            string q = Search.Text.Trim();
            if (_typeFilter == "key") { if (!r.Key && q.Length == 0) return false; }
            else if (_typeFilter.Length > 0 && !_typeFilter.Split(',').Contains(r.Info.Type)) return false;
            if (q.Length == 0) return true;
            return r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Group.Contains(q, StringComparison.OrdinalIgnoreCase) || r.Info.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

        private void Type_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb) { _typeFilter = (string)rb.Tag; _view?.Refresh(); }
        }

        private void Span_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && int.TryParse((string)rb.Tag, out int s)) { _span = s; if (IsLoaded) Refresh(); }
        }

        private void ResetMinMax_Click(object sender, RoutedEventArgs e)
        {
            foreach (var s in App.Hub.AllSensors) s.ResetMinMax();
            Refresh();
        }

        private List<SensorInfo> PinnedSensors()
        {
            List<string> ids;
            lock (App.Config) ids = App.Config.GraphSensors.ToList();
            return ids.Select(id => App.Hub.Sensor(id)).OfType<SensorInfo>().GroupBy(s => s.Id).Select(g => g.First()).ToList();
        }

        private static string Label(SensorInfo s)
        {
            string n = string.IsNullOrWhiteSpace(s.Label) ? SensorGlossary.Describe(s).Name : s.Label!;
            return n.Length > 30 ? n[..30] + "…" : n;
        }

        public void Refresh()
        {
            BuildRows();
            foreach (var r in _rows) r.Update();

            var pinned = PinnedSensors();
            EmptyHint.Visibility = pinned.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            var groups = pinned.GroupBy(s => s.Unit).ToList();
            string sig = string.Join("|", groups.Select(g => g.Key + ":" + string.Join(",", g.Select(s => s.Id))));
            if (sig != _chartSig)
            {
                _chartSig = sig;
                Charts.Children.Clear();
                _charts.Clear();
                foreach (var g in groups)
                {
                    var box = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
                    box.Children.Add(new TextBlock { Text = UnitTitle(g.Key, g.First().Type), Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 6) });
                    var chart = new TimeChart { Height = 190, MinSpan = g.First().Type switch { "Temperature" => 15, "Fan" => 300, "Clock" => 300, "Load" or "Control" => 20, _ => 1 } };
                    var legend = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
                    box.Children.Add(chart);
                    box.Children.Add(legend);
                    Charts.Children.Add(box);
                    _charts[g.Key] = (chart, legend);
                }
            }
            int ci = 0;
            foreach (var g in groups)
            {
                var (chart, legend) = _charts[g.Key];
                var series = g.Select(s => new ChartSeries
                {
                    Label = Label(s),
                    Color = Ui.Palette[ci++ % Ui.Palette.Length],
                    Unit = s.Unit,
                    Values = s.History.Last(Ui.Samples(_span)),
                    Format = s.Type == "Voltage" ? "0.000" : "0.#"
                }).ToList();
                chart.SpanSeconds = _span;
                chart.SetSeries(series);
                ChartLegend.Update(legend, chart, series, " " + g.Key);
            }
        }

        private static string UnitTitle(string unit, string type) => type switch
        {
            "Temperature" => L.T("טמפרטורות (°C)", "Temperatures (°C)"),
            "Fan" => L.T("מאווררים (RPM)", "Fans (RPM)"),
            "Voltage" => L.T("מתחים (V)", "Voltages (V)"),
            "Power" => L.T("הספק (W)", "Power (W)"),
            "Clock" => L.T("תדרים (MHz)", "Clocks (MHz)"),
            "Load" => L.T("עומס (%)", "Load (%)"),
            "Control" => L.T("עוצמת מאוורר (%)", "Fan duty (%)"),
            _ => unit
        };

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var pinned = PinnedSensors();
            if (pinned.Count == 0) { MainWindow.Instance?.ShowToast(L.T("סמן חיישנים ★ כדי לייצא", "Star ★ some sensors to export"), MainWindow.ToastKind.Warn); return; }
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"pulsewatch-{DateTime.Now:yyyyMMdd-HHmm}.csv", Filter = "CSV|*.csv" };
            if (dlg.ShowDialog() != true) return;
            int n = Ui.Samples(_span);
            var cols = pinned.Select(s => s.History.Last(n)).ToList();
            int poll;
            lock (App.Config) poll = App.Config.PollMs;
            var now = App.Hub.LastSample;
            var sb = new StringBuilder();
            sb.AppendLine("time," + string.Join(",", pinned.Select(s => $"\"{s.Hardware.Trim()} - {Label(s)} ({s.Unit})\"")));
            for (int i = 0; i < n; i++)
            {
                if (cols.All(c => float.IsNaN(c[i]))) continue;
                var t = now.AddMilliseconds(-(n - 1 - i) * (double)poll);
                sb.Append(t.ToString("yyyy-MM-dd HH:mm:ss"));
                foreach (var c in cols) sb.Append(',').Append(float.IsNaN(c[i]) ? "" : c[i].ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
                sb.AppendLine();
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            MainWindow.Instance?.ShowToast(L.T("נשמר: ", "Saved: ") + dlg.FileName);
        }
    }
}
