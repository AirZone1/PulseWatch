using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PulseWatch.Core;

namespace PulseWatch.Controls
{
    /// <summary>
    /// Voltage/frequency curve editor with two views.
    /// <para>Voltage view (default): MHz across, mV up — "how much voltage does each clock need". An undervolt is the
    /// line moving down, the saved voltage is shaded. Points cannot be dragged here; click selects, dragging across an
    /// empty area marks a clock range.</para>
    /// <para>Afterburner view: mV across, MHz up, exactly like MSI Afterburner. Drag a point up/down to change the clock
    /// at that voltage (15 MHz steps, like the GPU's own bins); Shift+drag moves the whole curve; click selects a
    /// point for the flatten tool; dragging across an empty area marks a voltage range.</para>
    /// The marked range is always stored as stock voltages.
    /// </summary>
    public sealed class VfCurveEditor : FrameworkElement
    {
        public sealed class Pt
        {
            public int Index;
            public double Mv;
            public double BaseMhz;
            public int DeltaMhz;
            public double Target => BaseMhz + DeltaMhz;
        }

        private List<Pt> _pts = new();
        private int _drag = -1, _hover = -1;
        private double _dragStartY, _dragStartTarget;
        private Dictionary<int, int>? _dragStartAll;
        private bool _shiftDrag;
        private double? _rangeStart;   // stock mV where a range drag began
        // voltage view: dragging the line up/down sets the undervolt by hand; the axes are frozen while dragging
        private bool _offsetDrag;
        private double _offsetStartY;
        private (double xlo, double xhi, double ylo, double yhi)? _frozen;

        public double MinMv { get; set; } = 700;
        public double MaxMv { get; set; } = 1150;
        public int Selected { get; private set; } = -1;
        public double? LiveMv { get; set; }
        public double? LiveMhz { get; set; }

        private bool _voltageView = true;
        /// <summary>True = MHz across / mV up (an undervolt makes the line go down). False = Afterburner layout.</summary>
        public bool VoltageView
        {
            get => _voltageView;
            set { _voltageView = value; _hover = -1; InvalidateVisual(); }
        }

        /// <summary>Marked range as stock voltages (mV), or null for the whole curve.</summary>
        public (double from, double to)? Range { get; private set; }

        public event Action? Changed;
        public event Action? SelectionChanged;
        public event Action? RangeChanged;
        /// <summary>Voltage view: the user grabbed the line. Then <see cref="OffsetDragged"/> with mV lowered since the grab (6.25 mV steps, negative = raised).</summary>
        public event Action? OffsetDragStarted;
        public event Action<double>? OffsetDragged;
        public event Action? OffsetDragEnded;

        private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Color Accent = Color.FromRgb(0x22, 0xD3, 0xEE);
        private static readonly Color Live = Color.FromRgb(0xFB, 0xBF, 0x24);
        private static readonly Color Save = Color.FromRgb(0x34, 0xD3, 0x99);
        private static readonly Color More = Color.FromRgb(0xF8, 0x71, 0x71);

        public VfCurveEditor()
        {
            FlowDirection = FlowDirection.LeftToRight;
            ClipToBounds = true;
            Focusable = true;
        }

        public IReadOnlyList<Pt> Points => _pts;

        /// <summary>Loads the live curve; keeps the user's pending deltas.</summary>
        public void SetCurve(IEnumerable<Pt> pts)
        {
            _pts = pts.OrderBy(p => p.Mv).ToList();
            InvalidateVisual();
        }

        public Dictionary<int, int> Deltas() => _pts.Where(p => p.DeltaMhz != 0).ToDictionary(p => p.Index, p => p.DeltaMhz);

        public void ClearRange() { Range = null; _rangeStart = null; InvalidateVisual(); RangeChanged?.Invoke(); }

        private static int Snap(double mhz) => (int)(Math.Round(mhz / 15.0) * 15);

        // ───────────── curve maths shared by both views ─────────────

        /// <summary>Lowest voltage (from <see cref="MinMv"/> up) at which the stock curve already gives <paramref name="mhz"/>.</summary>
        public double StockMvFor(double mhz) => _pts.Where(p => p.Mv >= MinMv && p.BaseMhz >= mhz).Select(p => p.Mv).DefaultIfEmpty(double.NaN).Min();

        /// <summary>Lowest voltage at which the edited curve gives <paramref name="mhz"/>.</summary>
        public double NewMvFor(double mhz) => _pts.Where(p => p.Mv >= MinMv && p.Target >= mhz).Select(p => p.Mv).DefaultIfEmpty(double.NaN).Min();

        /// <summary>
        /// "Voltage needed per clock": for every clock level the lowest voltage that reaches it (points from
        /// <see cref="MinMv"/> up). A flat top collapses into its first point, so the line ends at the real maximum.
        /// </summary>
        private List<(double f, double mv, int i)> Need(Func<Pt, double> freq)
        {
            var list = new List<(double f, double mv, int i)>();
            for (int i = 0; i < _pts.Count; i++)
            {
                var p = _pts[i];
                if (p.Mv < MinMv - 0.1) continue;
                double f = freq(p);
                if (list.Count == 0 || f > list[^1].f + 0.5) list.Add((f, p.Mv, i));
            }
            return list;
        }

        /// <summary>
        /// The edited "voltage per clock" line. Clocks the undervolt would reach below <see cref="MinMv"/> still run at
        /// the card's minimum voltage (~0.7 V; the GPU does not go lower), so the line starts with a flat floor from
        /// the lowest stock clock — index −1 marks that synthetic segment.
        /// </summary>
        private List<(double f, double mv, int i)> NeedEdited(List<(double f, double mv, int i)> stock)
        {
            var e = Need(p => p.Target);
            if (e.Count > 0 && stock.Count > 0 && e[0].f > stock[0].f + 0.5) e.Insert(0, (stock[0].f, e[0].mv, -1));
            return e;
        }

        private static double Interp(List<(double f, double mv, int i)> n, double f)
        {
            if (n.Count == 0 || f < n[0].f - 0.5 || f > n[^1].f + 0.5) return double.NaN;
            for (int k = 1; k < n.Count; k++)
                if (f <= n[k].f) return n[k - 1].mv + (n[k].mv - n[k - 1].mv) * (f - n[k - 1].f) / (n[k].f - n[k - 1].f);
            return n[^1].mv;
        }

        /// <summary>Average voltage difference (stock − edited, mV) over the clocks both curves reach; NaN if none.</summary>
        public double AverageSavingMv()
        {
            var s = Need(p => p.BaseMhz); var e = NeedEdited(s);
            if (s.Count < 2 || e.Count < 2) return double.NaN;
            double lo = Math.Max(s[0].f, e[0].f), hi = Math.Min(s[^1].f, e[^1].f);
            if (hi <= lo) return double.NaN;
            double sum = 0; int n = 0;
            for (double f = lo; f <= hi; f += 15) { double a = Interp(s, f), b = Interp(e, f); if (!double.IsNaN(a) && !double.IsNaN(b)) { sum += a - b; n++; } }
            return n == 0 ? double.NaN : sum / n;
        }

        /// <summary>Highest clock of the stock / edited curve and the lowest voltage that reaches it.</summary>
        public (double mhz, double mv) StockTop() { var s = Need(p => p.BaseMhz); return s.Count == 0 ? (double.NaN, double.NaN) : (s[^1].f, s[^1].mv); }
        public (double mhz, double mv) NewTop() { var e = Need(p => p.Target); return e.Count == 0 ? (double.NaN, double.NaN) : (e[^1].f, e[^1].mv); }

        // ───────────── geometry ─────────────

        private Rect Plot => new(52, 22, Math.Max(10, ActualWidth - 52 - 12), Math.Max(10, ActualHeight - 22 - 28));

        // Afterburner view: x = mV, y = MHz
        private (double lo, double hi) YRange()
        {
            var vis = _pts.Where(p => p.Mv >= MinMv && p.Mv <= MaxMv).ToList();
            if (vis.Count == 0) return (0, 2500);
            double lo = vis.Min(p => Math.Min(p.BaseMhz, p.Target)), hi = vis.Max(p => Math.Max(p.BaseMhz, p.Target));
            lo = Math.Floor((lo - 100) / 100) * 100;
            hi = Math.Ceiling((hi + 150) / 100) * 100;
            return (Math.Max(0, lo), hi);
        }
        private Point ToScreen(double mv, double mhz, (double lo, double hi) y) { var r = Plot; return new Point(r.Left + (mv - MinMv) / (MaxMv - MinMv) * r.Width, r.Bottom - (mhz - y.lo) / (y.hi - y.lo) * r.Height); }
        private double MhzAt(double sy, (double lo, double hi) y) { var r = Plot; return y.lo + (r.Bottom - sy) / r.Height * (y.hi - y.lo); }
        private double MvAtX(double sx) { var r = Plot; return MinMv + Math.Clamp((sx - r.Left) / r.Width, 0, 1) * (MaxMv - MinMv); }

        // Voltage view: x = MHz, y = mV
        private (double xlo, double xhi, double ylo, double yhi) VRange()
        {
            if (_frozen is { } f) return f;
            var s = Need(p => p.BaseMhz); var e = Need(p => p.Target);
            if (s.Count == 0) return (1000, 2100, MinMv, MaxMv);
            double xlo = Math.Min(s[0].f, e.Count > 0 ? e[0].f : s[0].f), xhi = Math.Max(s[^1].f, e.Count > 0 ? e[^1].f : 0);
            double yhi = Math.Max(s[^1].mv, e.Count > 0 ? e[^1].mv : 0);
            return (Math.Floor(xlo / 100) * 100, Math.Ceiling((xhi + 40) / 100) * 100, MinMv, Math.Ceiling((yhi + 30) / 50) * 50);
        }
        private Point ToScreenV(double mhz, double mv, (double xlo, double xhi, double ylo, double yhi) v) { var r = Plot; return new Point(r.Left + (mhz - v.xlo) / (v.xhi - v.xlo) * r.Width, r.Bottom - (mv - v.ylo) / (v.yhi - v.ylo) * r.Height); }
        private double MhzAtX(double sx, (double xlo, double xhi, double ylo, double yhi) v) { var r = Plot; return v.xlo + Math.Clamp((sx - r.Left) / r.Width, 0, 1) * (v.xhi - v.xlo); }

        /// <summary>Stock voltage of the point under a screen x, in either view.</summary>
        private double StockMvAtX(double sx)
        {
            if (_pts.Count == 0) return MvAtX(sx);
            if (!_voltageView) return _pts.OrderBy(p => Math.Abs(p.Mv - MvAtX(sx))).First().Mv;
            double mv = StockMvFor(MhzAtX(sx, VRange()));
            return double.IsNaN(mv) ? _pts.Where(p => p.Mv >= MinMv).Max(p => p.Mv) : mv;
        }

        private int HitTest(Point pos)
        {
            int best = -1; double bestD = 9;
            if (_voltageView)
            {
                var v = VRange();
                foreach (var n in Need(p => p.Target))
                {
                    double d = (ToScreenV(n.f, n.mv, v) - pos).Length;
                    if (d < bestD) { bestD = d; best = n.i; }
                }
                return best;
            }
            var y = YRange();
            for (int i = 0; i < _pts.Count; i++)
            {
                var p = _pts[i];
                if (p.Mv < MinMv || p.Mv > MaxMv) continue;
                double d = (ToScreen(p.Mv, p.Target, y) - pos).Length;
                if (d < bestD) { bestD = d; best = i; }
            }
            return best;
        }

        // ───────────── mouse ─────────────

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            var pos = e.GetPosition(this);
            int hit = HitTest(pos);
            if (hit >= 0)
            {
                Selected = hit;
                SelectionChanged?.Invoke();
                if (_voltageView)
                {
                    _frozen = VRange();
                    _offsetDrag = true;
                    _offsetStartY = pos.Y;
                    CaptureMouse();
                    OffsetDragStarted?.Invoke();
                }
                else
                {
                    _drag = hit;
                    _shiftDrag = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                    _dragStartY = pos.Y;
                    _dragStartTarget = _pts[_drag].Target;
                    _dragStartAll = _pts.ToDictionary(p => p.Index, p => p.DeltaMhz);
                    CaptureMouse();
                }
                InvalidateVisual();
                return;
            }
            // empty area: start marking a range
            _rangeStart = StockMvAtX(pos.X);
            Range = null;
            CaptureMouse();
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var pos = e.GetPosition(this);
            if (_rangeStart is double rs && e.LeftButton == MouseButtonState.Pressed)
            {
                double mv = StockMvAtX(pos.X);
                Range = Math.Abs(mv - rs) < 1 ? null : (Math.Min(rs, mv), Math.Max(rs, mv));
                InvalidateVisual();
                return;
            }
            if (_offsetDrag && e.LeftButton == MouseButtonState.Pressed && _frozen is { } fr)
            {
                var r = Plot;
                double mvPerPx = (fr.yhi - fr.ylo) / r.Height;
                double lowered = (pos.Y - _offsetStartY) * mvPerPx;   // dragging down = less voltage
                OffsetDragged?.Invoke(Math.Round(lowered / 6.25) * 6.25);
                InvalidateVisual();
                return;
            }
            if (_drag >= 0 && e.LeftButton == MouseButtonState.Pressed)
            {
                var y = YRange();
                double dy = MhzAt(pos.Y, y) - MhzAt(_dragStartY, y);
                if (_shiftDrag && _dragStartAll != null)
                {
                    int shift = Snap(dy);
                    foreach (var p in _pts) p.DeltaMhz = Math.Clamp(_dragStartAll[p.Index] + shift, -1000, 1000);
                }
                else
                {
                    var p = _pts[_drag];
                    p.DeltaMhz = Math.Clamp(Snap(_dragStartTarget + dy - p.BaseMhz), -1000, 1000);
                }
                Changed?.Invoke();
            }
            else
            {
                int h = HitTest(pos);
                if (h != _hover) { _hover = h; Cursor = h >= 0 ? Cursors.SizeNS : Cursors.Arrow; }
            }
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_rangeStart != null) { _rangeStart = null; ReleaseMouseCapture(); InvalidateVisual(); RangeChanged?.Invoke(); return; }
            if (_offsetDrag) { _offsetDrag = false; _frozen = null; ReleaseMouseCapture(); InvalidateVisual(); OffsetDragEnded?.Invoke(); return; }
            if (_drag >= 0) { _drag = -1; _dragStartAll = null; ReleaseMouseCapture(); Changed?.Invoke(); }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            // never leave a drag half-finished when the window loses the mouse mid-drag
            if (_rangeStart != null) { _rangeStart = null; RangeChanged?.Invoke(); }
            if (_offsetDrag) { _offsetDrag = false; _frozen = null; OffsetDragEnded?.Invoke(); }
            if (_drag >= 0) { _drag = -1; _dragStartAll = null; Changed?.Invoke(); }
            base.OnLostMouseCapture(e);
        }

        protected override void OnMouseLeave(MouseEventArgs e) { _hover = -1; InvalidateVisual(); }

        // ───────────── drawing ─────────────

        // Hebrew labels need a right-to-left paragraph, otherwise the bidi reordering scrambles "text: 1980 MHz"
        private FormattedText T(string s, double size, Color c) =>
            new(s, L.Culture, s.Any(ch => ch >= '\u0590' && ch <= '\u05FF') ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                Face, size, new SolidColorBrush(c), VisualTreeHelper.GetDpi(this).PixelsPerDip);

        /// <summary>
        /// A label made of words and a number part ("Stock: up to" + "1980 MHz · 1106.25 mV"). In Hebrew the two are
        /// drawn as separate pieces — numbers left, words right — because one mixed run gets scrambled by the bidi
        /// reordering; in English it is one ordinary string.
        /// </summary>
        private sealed class Mix
        {
            public FormattedText? He;
            public FormattedText Lat = null!;
            public double Width => Lat.Width + (He == null ? 0 : He.Width + (Lat.Width > 0 ? 5 : 0));
            public double Height => Math.Max(Lat.Height, He?.Height ?? 0);
        }

        private Mix M(string he, string latin, double size, Color c, string enBefore = "", string enAfter = "") =>
            L.En || he.Length == 0
                ? new Mix { Lat = T(L.En ? enBefore + latin + enAfter : latin, size, c) }
                : new Mix { Lat = T(latin, size, c), He = T(he, size, c) };

        private static void DrawM(DrawingContext dc, Mix m, Point topLeft)
        {
            DrawT(dc, m.Lat, topLeft);
            if (m.He != null) DrawT(dc, m.He, new Point(topLeft.X + m.Lat.Width + (m.Lat.Width > 0 ? 5 : 0), topLeft.Y));
        }

        /// <summary>Draws with <paramref name="topLeft"/> as the box's top-left corner in both directions (RTL text is anchored on its right edge).</summary>
        private static void DrawT(DrawingContext dc, FormattedText ft, Point topLeft) =>
            dc.DrawText(ft, ft.FlowDirection == FlowDirection.RightToLeft ? new Point(topLeft.X + ft.Width, topLeft.Y) : topLeft);

        private void Label(DrawingContext dc, Rect r, Point at, Mix line1, Mix? line2)
        {
            double w = Math.Max(line1.Width, line2?.Width ?? 0), h = line1.Height + (line2 != null ? line2.Height + 2 : 0);
            double x = Math.Clamp(at.X - w / 2 - 8, r.Left, r.Right - w - 16);
            double yy = at.Y - h - 24 < r.Top ? at.Y + 14 : at.Y - h - 24;
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 0x1A, 0x23, 0x32)), new Pen(new SolidColorBrush(Color.FromArgb(120, Accent.R, Accent.G, Accent.B)), 1), new Rect(x, yy, w + 16, h + 8), 6, 6);
            DrawM(dc, line1, new Point(x + 8, yy + 4));
            if (line2 != null) DrawM(dc, line2, new Point(x + 8, yy + 6 + line1.Height));
        }

        private void RangeBand(DrawingContext dc, Rect r, double x1, double x2, string text)
        {
            var band = new Rect(new Point(Math.Min(x1, x2), r.Top), new Point(Math.Max(Math.Max(x1, x2), Math.Min(x1, x2) + 2), r.Bottom));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(26, Accent.R, Accent.G, Accent.B)), new Pen(new SolidColorBrush(Color.FromArgb(90, Accent.R, Accent.G, Accent.B)), 1), band);
            var rl = T(text, 11, Accent);
            DrawT(dc, rl, new Point(Math.Clamp(band.Left + band.Width / 2 - rl.Width / 2, r.Left + 2, r.Right - rl.Width - 2), r.Top + 4));
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 80 || ActualHeight < 80) return;
            var r = Plot;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(10, 255, 255, 255)), null, r, 6, 6);
            if (_voltageView) RenderVoltageView(dc, r); else RenderAfterburnerView(dc, r);
        }

        private void RenderVoltageView(DrawingContext dc, Rect r)
        {
            var v = VRange();
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1);
            var lbl = Color.FromRgb(0x6B, 0x76, 0x8A);
            for (double mv = Math.Ceiling(v.ylo / 50) * 50; mv <= v.yhi; mv += 50)
            {
                var a = ToScreenV(v.xlo, mv, v);
                dc.DrawLine(grid, new Point(r.Left, Math.Round(a.Y) + .5), new Point(r.Right, Math.Round(a.Y) + .5));
                var ft = T($"{mv:0}", 10.5, lbl);
                DrawT(dc, ft, new Point(r.Left - ft.Width - 8, a.Y - ft.Height / 2));
            }
            for (double f = Math.Ceiling(v.xlo / 100) * 100; f <= v.xhi; f += 100)
            {
                var a = ToScreenV(f, v.ylo, v);
                dc.DrawLine(grid, new Point(Math.Round(a.X) + .5, r.Top), new Point(Math.Round(a.X) + .5, r.Bottom));
                var ft = T($"{f:0}", 10.5, lbl);
                if (a.X - ft.Width / 2 > r.Left + 2) DrawT(dc, ft, new Point(a.X - ft.Width / 2, r.Bottom + 6));
            }
            var unitY = T("mV", 10, lbl); DrawT(dc, unitY, new Point(r.Left - unitY.Width - 8, 2));
            var unitX = T("MHz", 10, lbl); DrawT(dc, unitX, new Point(r.Left - unitX.Width - 8, r.Bottom + 6));

            var s = Need(p => p.BaseMhz);
            var e = NeedEdited(s);
            if (s.Count < 2) return;

            if (Range is (double rf, double rt))
            {
                double f1 = _pts.OrderBy(p => Math.Abs(p.Mv - rf)).First().BaseMhz, f2 = _pts.OrderBy(p => Math.Abs(p.Mv - rt)).First().BaseMhz;
                RangeBand(dc, r, ToScreenV(f1, v.ylo, v).X, ToScreenV(f2, v.ylo, v).X, $"{f1:0}–{f2:0} MHz");
            }

            // shaded difference: green = less voltage than stock for the same clock, red = more
            if (e.Count >= 2)
            {
                double lo = Math.Max(s[0].f, e[0].f), hi = Math.Min(s[^1].f, e[^1].f);
                if (hi > lo)
                {
                    var steps = new List<double>();
                    for (double f = lo; f < hi; f += 7.5) steps.Add(f);
                    steps.Add(hi);
                    foreach (bool savePart in new[] { true, false })
                    {
                        var geo = new StreamGeometry();
                        using (var c = geo.Open())
                        {
                            var top = new List<Point>(); var bottom = new List<Point>();
                            void Flush()
                            {
                                if (top.Count > 1)
                                {
                                    c.BeginFigure(top[0], true, true);
                                    foreach (var pt in top.Skip(1)) c.LineTo(pt, false, false);
                                    for (int k = bottom.Count - 1; k >= 0; k--) c.LineTo(bottom[k], false, false);
                                }
                                top.Clear(); bottom.Clear();
                            }
                            foreach (double f in steps)
                            {
                                double a = Interp(s, f), b = Interp(e, f);
                                bool part = !double.IsNaN(a) && !double.IsNaN(b) && (savePart ? a > b + 0.5 : b > a + 0.5);
                                if (!part) { Flush(); continue; }
                                top.Add(ToScreenV(f, a, v)); bottom.Add(ToScreenV(f, b, v));
                            }
                            Flush();
                        }
                        geo.Freeze();
                        var col = savePart ? Save : More;
                        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(46, col.R, col.G, col.B)), null, geo);
                    }

                    double avg = AverageSavingMv();
                    if (!double.IsNaN(avg) && Math.Abs(avg) >= 1)
                    {
                        double fm = lo + (hi - lo) * 0.45;
                        double ya = Interp(s, fm), yb = Interp(e, fm);
                        var col = avg > 0 ? Save : More;
                        var ft = M("בממוצע לאותו תדר", avg > 0 ? $"−{avg:0} mV" : $"+{-avg:0} mV", 12, col, enAfter: " on average for the same clock");
                        var pa = ToScreenV(fm, Math.Max(ya, yb), v);
                        DrawM(dc, ft, new Point(Math.Clamp(pa.X - ft.Width / 2, r.Left + 4, r.Right - ft.Width - 4), Math.Max(r.Top + 4, pa.Y - ft.Height - 12)));
                    }
                }
            }

            StreamGeometry Line(List<(double f, double mv, int i)> n)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(ToScreenV(n[0].f, n[0].mv, v), false, false);
                    foreach (var p in n.Skip(1)) c.LineTo(ToScreenV(p.f, p.mv, v), true, true);
                }
                g.Freeze();
                return g;
            }
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)), 1.3) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) }, Line(s));
            if (e.Count >= 2)
            {
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Accent), 2.4) { LineJoin = PenLineJoin.Round }, Line(e));
                var ptBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x15));
                foreach (var n in e)
                {
                    if (n.i < 0) continue;
                    bool hot = n.i == _hover || n.i == Selected;
                    double rad = hot ? 5.5 : 2.6;
                    dc.DrawEllipse(n.i == Selected ? new SolidColorBrush(Accent) : ptBrush, new Pen(new SolidColorBrush(Accent), hot ? 2.2 : 1.2), ToScreenV(n.f, n.mv, v), rad, rad);
                }
                if (e[0].i < 0 && e.Count > 1)
                {
                    var a0 = ToScreenV(e[0].f, e[0].mv, v); var a1 = ToScreenV(e[1].f, e[1].mv, v);
                    var fl = M("מתח מינימלי של הכרטיס — לא יורד מתחת", "≈0.7 V", 11, Accent, enBefore: "Card minimum voltage ", enAfter: " — it never goes lower");
                    if (a1.X - a0.X > fl.Width + 8) DrawM(dc, fl, new Point(a0.X + (a1.X - a0.X - fl.Width) / 2, a0.Y - fl.Height - 6));
                }
                // the end of each line = the highest clock and the highest voltage the card will use
                var end = e[^1];
                var ep = ToScreenV(end.f, end.mv, v);
                var et = M("שלך: עד", $"{end.f:0} MHz · {end.mv:0.##} mV", 11.5, Accent, enBefore: "Yours: up to ");
                DrawM(dc, et, new Point(Math.Clamp(ep.X - et.Width - 12, r.Left + 4, r.Right - et.Width - 4), Math.Max(r.Top + 2, ep.Y - et.Height / 2 - 1)));
            }
            var sEnd = s[^1];
            var sp = ToScreenV(sEnd.f, sEnd.mv, v);
            var st = M("מקור: עד", $"{sEnd.f:0} MHz · {sEnd.mv:0.##} mV", 11.5, Color.FromRgb(0xB8, 0xC0, 0xCC), enBefore: "Stock: up to ");
            DrawM(dc, st, new Point(Math.Clamp(sp.X - st.Width - 12, r.Left + 4, r.Right - st.Width - 4), Math.Max(r.Top + 2, sp.Y - st.Height - 8)));

            if (LiveMv is double lmv && LiveMhz is double lmhz)
            {
                var p = ToScreenV(lmhz, lmv, v);
                if (r.Contains(p))
                {
                    dc.DrawEllipse(null, new Pen(new SolidColorBrush(Live), 2), p, 8, 8);
                    dc.DrawEllipse(new SolidColorBrush(Live), null, p, 3, 3);
                }
            }

            int show = _hover >= 0 ? _hover : Selected;
            if (show >= 0 && show < _pts.Count)
            {
                var p = _pts[show];
                double smv = StockMvFor(p.Target);
                var c2 = Color.FromRgb(0xB8, 0xC0, 0xCC);
                var line2 = double.IsNaN(smv) ? M("המקור לא מגיע לתדר הזה", "", 11.5, c2, enBefore: "Stock never reaches this clock")
                    : Math.Abs(smv - p.Mv) < 0.5 ? M("כמו במקור", "", 11.5, c2, enBefore: "Same as stock")
                    : M("במקור:", $"{smv:0.##} mV  ({p.Mv - smv:+0.##;−0.##} mV)", 11.5, c2, enBefore: "Stock: ");
                Label(dc, r, ToScreenV(p.Target, p.Mv, v), M("", $"{p.Target:0} MHz → {p.Mv:0.##} mV", 12, Colors.White), line2);
            }
        }

        private void RenderAfterburnerView(DrawingContext dc, Rect r)
        {
            var y = YRange();
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1);
            var lbl = Color.FromRgb(0x6B, 0x76, 0x8A);
            for (double f = Math.Ceiling(y.lo / 100) * 100; f <= y.hi; f += 100)
            {
                var a = ToScreen(MinMv, f, y);
                dc.DrawLine(grid, new Point(r.Left, Math.Round(a.Y) + .5), new Point(r.Right, Math.Round(a.Y) + .5));
                var ft = T($"{f:0}", 10.5, lbl);
                DrawT(dc, ft, new Point(r.Left - ft.Width - 8, a.Y - ft.Height / 2));
            }
            for (double mv = Math.Ceiling(MinMv / 50) * 50; mv <= MaxMv; mv += 50)
            {
                var a = ToScreen(mv, y.lo, y);
                dc.DrawLine(grid, new Point(Math.Round(a.X) + .5, r.Top), new Point(Math.Round(a.X) + .5, r.Bottom));
                var ft = T($"{mv:0}", 10.5, lbl);
                if (a.X - ft.Width / 2 > r.Left + 2) DrawT(dc, ft, new Point(a.X - ft.Width / 2, r.Bottom + 6));
            }
            var unitY = T("MHz", 10, lbl); DrawT(dc, unitY, new Point(r.Left - unitY.Width - 8, 2));
            var unitX = T("mV", 10, lbl); DrawT(dc, unitX, new Point(r.Left - unitX.Width - 8, r.Bottom + 6));

            if (Range is (double rf, double rt))
                RangeBand(dc, r, ToScreen(rf, y.lo, y).X, ToScreen(rt, y.lo, y).X, $"{rf:0.##}–{rt:0.##} mV");

            var vis = _pts.Where(p => p.Mv >= MinMv - 7 && p.Mv <= MaxMv + 7).ToList();
            if (vis.Count > 1)
            {
                var baseGeo = new StreamGeometry();
                using (var c = baseGeo.Open())
                {
                    c.BeginFigure(ToScreen(vis[0].Mv, vis[0].BaseMhz, y), false, false);
                    foreach (var p in vis.Skip(1)) c.LineTo(ToScreen(p.Mv, p.BaseMhz, y), true, true);
                }
                baseGeo.Freeze();
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1.2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) }, baseGeo);

                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    c.BeginFigure(ToScreen(vis[0].Mv, vis[0].Target, y), false, false);
                    foreach (var p in vis.Skip(1)) c.LineTo(ToScreen(p.Mv, p.Target, y), true, true);
                }
                geo.Freeze();
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Accent), 2.2) { LineJoin = PenLineJoin.Round }, geo);

                var ptBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x15));
                for (int i = 0; i < _pts.Count; i++)
                {
                    var p = _pts[i];
                    if (p.Mv < MinMv || p.Mv > MaxMv) continue;
                    bool hot = i == _hover || i == _drag;
                    bool sel = i == Selected;
                    double rad = hot || sel ? 5.5 : p.DeltaMhz != 0 ? 3.2 : 2.4;
                    dc.DrawEllipse(sel ? new SolidColorBrush(Accent) : ptBrush, new Pen(new SolidColorBrush(Accent), hot ? 2.4 : 1.4), ToScreen(p.Mv, p.Target, y), rad, rad);
                }
            }

            if (LiveMv is double lmv && LiveMhz is double lmhz && lmv >= MinMv && lmv <= MaxMv)
            {
                var p = ToScreen(lmv, lmhz, y);
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Live), 2), p, 8, 8);
                dc.DrawEllipse(new SolidColorBrush(Live), null, p, 3, 3);
            }

            int show = _drag >= 0 ? _drag : _hover >= 0 ? _hover : Selected;
            if (show >= 0 && show < _pts.Count)
            {
                var p = _pts[show];
                double smv = StockMvFor(p.Target);
                var c2 = Color.FromRgb(0xB8, 0xC0, 0xCC);
                Mix? line2 = p.DeltaMhz == 0 ? null : !double.IsNaN(smv) && smv > p.Mv + 1
                    ? M("במקור ב-", $"{smv:0.##} mV  ({p.Mv - smv:0.##} mV)", 11.5, c2, enBefore: "Stock at ")
                    : M("", $"{p.DeltaMhz:+0;-0} MHz", 11.5, c2);
                Label(dc, r, ToScreen(p.Mv, p.Target, y), M("", $"{p.Mv:0.##} mV → {p.Target:0} MHz", 12, Colors.White), line2);
            }
        }
    }
}
