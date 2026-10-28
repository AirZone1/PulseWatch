using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PulseWatch.Core;

namespace PulseWatch.Controls
{
    public sealed class ChartSeries
    {
        public string Label { get; init; } = "";
        public Color Color { get; init; }
        public float[] Values { get; set; } = Array.Empty<float>();
        public string Unit { get; init; } = "";
        public string Format { get; init; } = "0.#";
    }

    /// <summary>
    /// Lightweight multi-series time chart with hover crosshair. Newest sample on the right.
    /// Series can be hidden or isolated (see <see cref="ChartLegend"/>); the Y axis fits the visible ones.
    /// </summary>
    public sealed class TimeChart : FrameworkElement
    {
        private List<ChartSeries> _series = new();
        private Point? _hover;
        private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x6B, 0x76, 0x8A)));
        private static readonly Brush TipBg = Frozen(new SolidColorBrush(Color.FromArgb(240, 0x1A, 0x23, 0x32)));
        private static readonly Pen GridPen = FrozenPen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
        private static readonly Pen TipBorder = FrozenPen(new SolidColorBrush(Color.FromRgb(0x2A, 0x35, 0x4A)), 1);
        private static readonly Pen Cross = FrozenPen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
        private static readonly Dictionary<Color, (Brush solid, Pen line, Pen thin, Brush fill)> Paint = new();

        public double? YMin { get; set; }
        public double? YMax { get; set; }
        /// <summary>Minimum visible span, so a flat line does not fill the whole height.</summary>
        public double MinSpan { get; set; } = 10;
        public bool Compact { get; set; }
        public bool FillFirst { get; set; } = true;
        /// <summary>Seconds covered by the series (for the x-axis labels).</summary>
        public double SpanSeconds { get; set; } = 300;
        /// <summary>Labels of series the user switched off.</summary>
        public HashSet<string> Hidden { get; } = new();

        public TimeChart()
        {
            FlowDirection = FlowDirection.LeftToRight;
            ClipToBounds = true;
            SnapsToDevicePixels = true;
        }

        private static Brush Frozen(Brush b) { b.Freeze(); return b; }
        private static Pen FrozenPen(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }

        private static (Brush solid, Pen line, Pen thin, Brush fill) PaintFor(Color c)
        {
            if (Paint.TryGetValue(c, out var p)) return p;
            var solid = Frozen(new SolidColorBrush(c));
            var line = new Pen(solid, 1.8) { LineJoin = PenLineJoin.Round }; line.Freeze();
            var thin = new Pen(solid, 1.6) { LineJoin = PenLineJoin.Round }; thin.Freeze();
            var fill = Frozen(new LinearGradientBrush(Color.FromArgb(70, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B), 90));
            return Paint[c] = (solid, line, thin, fill);
        }

        public IReadOnlyList<ChartSeries> Series => _series;

        public void SetSeries(IEnumerable<ChartSeries> series)
        {
            _series = series.ToList();
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e) { _hover = e.GetPosition(this); InvalidateVisual(); }
        protected override void OnMouseLeave(MouseEventArgs e) { _hover = null; InvalidateVisual(); }

        private FormattedText Text(string s, double size, Brush b, FlowDirection dir = FlowDirection.LeftToRight) =>
            new(s, L.Culture, dir, Face, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 20 || h < 20) return;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

            double padL = Compact ? 0 : 38, padR = Compact ? 0 : 8, padT = Compact ? 2 : 8, padB = Compact ? 2 : 20;
            var plot = new Rect(padL, padT, Math.Max(1, w - padL - padR), Math.Max(1, h - padT - padB));
            var visible = _series.Where(s => !Hidden.Contains(s.Label)).ToList();

            // y-range from the visible series only, so isolating one line stretches it
            double lo = double.MaxValue, hi = double.MinValue;
            foreach (var s in visible)
                foreach (var v in s.Values)
                    if (!float.IsNaN(v)) { if (v < lo) lo = v; if (v > hi) hi = v; }
            if (lo == double.MaxValue) { lo = 0; hi = 1; }
            if (YMin.HasValue) lo = Math.Min(lo, YMin.Value);
            if (YMax.HasValue) hi = Math.Max(hi, YMax.Value);
            if (hi - lo < MinSpan) { double mid = (hi + lo) / 2; lo = mid - MinSpan / 2; hi = mid + MinSpan / 2; if (YMin.HasValue && lo < YMin) { lo = YMin.Value; hi = lo + MinSpan; } }
            double step = NiceStep((hi - lo) / 4);
            lo = Math.Floor(lo / step) * step;
            hi = Math.Ceiling(hi / step) * step;
            if (YMin.HasValue) lo = Math.Max(lo, YMin.Value);
            if (hi <= lo) hi = lo + step;

            double Y(double v) => plot.Bottom - (v - lo) / (hi - lo) * plot.Height;

            if (!Compact)
            {
                for (double g = lo; g <= hi + step / 2; g += step)
                {
                    double y = Math.Round(Y(g)) + 0.5;
                    dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                    var ft = Text(g.ToString(step < 1 ? "0.#" : "0", CultureInfo.InvariantCulture), 10.5, LabelBrush);
                    dc.DrawText(ft, new Point(plot.Left - ft.Width - 6, y - ft.Height / 2));
                }
                const int ticks = 4;
                for (int i = 0; i <= ticks; i++)
                {
                    double x = plot.Left + plot.Width * i / ticks;
                    double secAgo = SpanSeconds * (ticks - i) / ticks;
                    var ft = Text(i == ticks ? L.T("עכשיו", "now") : FormatAgo(secAgo), 10.5, LabelBrush);
                    double tx = Math.Clamp(x - ft.Width / 2, plot.Left, plot.Right - ft.Width);
                    dc.DrawText(ft, new Point(tx, plot.Bottom + 4));
                }
            }

            int idx = 0;
            foreach (var s in _series)
            {
                bool first = idx++ == 0;
                if (Hidden.Contains(s.Label)) continue;
                var vals = s.Values;
                int n = vals.Length;
                if (n < 2) continue;
                var paint = PaintFor(s.Color);
                bool doFill = FillFirst && (first || visible.Count == 1);

                int buckets = (int)Math.Max(2, Math.Min(n, plot.Width));
                var geo = new StreamGeometry();
                var fill = doFill ? new StreamGeometry() : null;
                using (var ctx = geo.Open())
                {
                    StreamGeometryContext? fctx = fill?.Open();
                    bool started = false;
                    double lastX = 0;
                    for (int b = 0; b < buckets; b++)
                    {
                        int a0 = (int)((long)b * n / buckets), a1 = Math.Max(a0 + 1, (int)((long)(b + 1) * n / buckets));
                        double sum = 0; int cnt = 0;
                        for (int k = a0; k < a1 && k < n; k++) if (!float.IsNaN(vals[k])) { sum += vals[k]; cnt++; }
                        if (cnt == 0)
                        {
                            if (started) fctx?.LineTo(new Point(lastX, plot.Bottom), false, false);
                            started = false;
                            continue;
                        }
                        var p = new Point(plot.Left + plot.Width * (b + 0.5) / buckets, Y(sum / cnt));
                        if (!started)
                        {
                            ctx.BeginFigure(p, false, false);
                            if (fctx != null) { fctx.BeginFigure(new Point(p.X, plot.Bottom), true, true); fctx.LineTo(p, false, false); }
                            started = true;
                        }
                        else
                        {
                            ctx.LineTo(p, true, true);
                            fctx?.LineTo(p, false, false);
                        }
                        lastX = p.X;
                    }
                    if (started) fctx?.LineTo(new Point(lastX, plot.Bottom), false, false);
                    fctx?.Close();
                }
                geo.Freeze();
                if (fill != null) { fill.Freeze(); dc.DrawGeometry(paint.fill, null, fill); }
                dc.DrawGeometry(null, Compact ? paint.thin : paint.line, geo);
            }

            if (_hover is Point hp && plot.Contains(hp) && visible.Count > 0 && !Compact)
            {
                dc.DrawLine(Cross, new Point(hp.X, plot.Top), new Point(hp.X, plot.Bottom));
                double frac = (hp.X - plot.Left) / plot.Width;
                double secAgo = SpanSeconds * (1 - frac);
                var lines = new List<(string text, Brush b)> { (secAgo < 1 ? L.T("עכשיו", "now") : L.T($"לפני {FormatAgo(secAgo)}", $"{FormatAgo(secAgo)} ago"), LabelBrush) };
                foreach (var s in visible)
                {
                    if (s.Values.Length == 0) continue;
                    int k = Math.Clamp((int)(frac * s.Values.Length), 0, s.Values.Length - 1);
                    float v = s.Values[k];
                    if (float.IsNaN(v)) continue;
                    var paint = PaintFor(s.Color);
                    lines.Add(($"{s.Label}: {v.ToString(s.Format, CultureInfo.InvariantCulture)} {s.Unit}", paint.solid));
                    dc.DrawEllipse(paint.solid, null, new Point(hp.X, Y(v)), 3.5, 3.5);
                }
                var dir = L.En ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
                var texts = lines.Select(l => Text(l.text, 12, l.b, dir)).ToList();
                double tw = texts.Max(t => t.Width) + 20, th = texts.Sum(t => t.Height + 2) + 12;
                double tx = hp.X + 14 + tw > plot.Right ? hp.X - 14 - tw : hp.X + 14;
                double ty = Math.Clamp(hp.Y - th / 2, plot.Top, Math.Max(plot.Top, plot.Bottom - th));
                dc.DrawRoundedRectangle(TipBg, TipBorder, new Rect(tx, ty, tw, th), 8, 8);
                double yy = ty + 6;
                foreach (var t in texts) { dc.DrawText(t, new Point(L.En ? tx + 10 : tx + tw - 10, yy)); yy += t.Height + 2; }
            }
        }

        private static string FormatAgo(double sec)
        {
            if (sec >= 120) return L.T($"{sec / 60:0} דק'", $"{sec / 60:0} min");
            return L.T($"{sec:0} שנ'", $"{sec:0} s");
        }

        private static double NiceStep(double raw)
        {
            if (raw <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double n = raw / mag;
            double nice = n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10;
            return nice * mag;
        }
    }

    /// <summary>
    /// Legend under a chart. Click an item to hide/show its line, double-click to show only that line
    /// (double-click again to bring everything back). Elements are reused between refreshes.
    /// </summary>
    public static class ChartLegend
    {
        private sealed class Item
        {
            public StackPanel Root = null!;
            public TextBlock Value = null!;
            public Ellipse Dot = null!;
        }

        private sealed class Holder { public string Sig = ""; public Dictionary<string, Item> Items = new(); }

        // weak: legends of released views must not be kept alive
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Panel, Holder> State = new();

        public static void Update(Panel host, TimeChart? chart, IList<ChartSeries> series, string unit)
        {
            string sig = string.Join("|", series.Select(s => s.Label + s.Color));
            var st = State.GetOrCreateValue(host);
            if (st.Sig != sig || host.Children.Count != series.Count)
            {
                host.Children.Clear();
                var items = new Dictionary<string, Item>();
                foreach (var s in series)
                {
                    var it = new Item
                    {
                        Root = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 2), Background = Brushes.Transparent, Cursor = chart != null ? Cursors.Hand : null },
                        Dot = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(s.Color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
                        Value = new TextBlock { Style = (Style)Application.Current.FindResource("Num"), FontSize = 12.5 }
                    };
                    bool latin = s.Label.Length > 0 && s.Label[0] < 0x0590;
                    it.Root.Children.Add(it.Dot);
                    it.Root.Children.Add(new TextBlock { Text = s.Label, Style = (Style)Application.Current.FindResource("Caption"), Margin = new Thickness(0, 0, 6, 0), FlowDirection = latin ? FlowDirection.LeftToRight : FlowDirection.RightToLeft });
                    it.Root.Children.Add(it.Value);
                    if (chart != null)
                    {
                        string label = s.Label;
                        it.Root.ToolTip = L.T("לחיצה: הסתר/הצג · לחיצה כפולה: הצג רק את זה", "Click: hide/show · Double-click: show only this");
                        it.Root.MouseLeftButtonDown += (_, e) =>
                        {
                            if (e.ClickCount == 2)
                            {
                                var others = chart.Series.Select(x => x.Label).Where(l => l != label).ToList();
                                bool alreadySolo = others.All(chart.Hidden.Contains) && !chart.Hidden.Contains(label);
                                chart.Hidden.Clear();
                                if (!alreadySolo) foreach (var o in others) chart.Hidden.Add(o);
                            }
                            else if (!chart.Hidden.Remove(label)) chart.Hidden.Add(label);
                            Restyle(host, chart);
                            chart.InvalidateVisual();
                            e.Handled = true;
                        };
                    }
                    host.Children.Add(it.Root);
                    items[s.Label] = it;
                }
                st.Sig = sig;
                st.Items = items;
            }
            foreach (var s in series)
            {
                if (!st.Items.TryGetValue(s.Label, out var it)) continue;
                float last = s.Values.Length > 0 ? s.Values[^1] : float.NaN;
                it.Value.Text = float.IsNaN(last) ? "—" : $"{last:0}{unit}";
            }
            if (chart != null) Restyle(host, chart);
        }

        private static void Restyle(Panel host, TimeChart chart)
        {
            if (!State.TryGetValue(host, out var st)) return;
            foreach (var (label, it) in st.Items) it.Root.Opacity = chart.Hidden.Contains(label) ? 0.35 : 1;
        }
    }
}
