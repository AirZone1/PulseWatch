using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PulseWatch.Core;

namespace PulseWatch.Controls
{
    /// <summary>
    /// GCC-style fan curve editor: drag points, double-click to add, right-click to remove.
    /// Shows the live temperature and the duty the engine is applying right now.
    /// </summary>
    public sealed class FanCurveEditor : FrameworkElement
    {
        public const double TMin = 20, TMax = 100;

        private List<CurvePoint> _points = new();
        private int _drag = -1;
        private int _hoverPt = -1;
        private Point? _mouse;

        private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private static readonly Color Accent = Color.FromRgb(0x22, 0xD3, 0xEE);
        private static readonly Color Live = Color.FromRgb(0xFB, 0xBF, 0x24);
        private static readonly CultureInfo He = CultureInfo.GetCultureInfo("he-IL");

        public double? LiveTemp { get; set; }
        public double? LivePct { get; set; }
        public double MinPct { get; set; }
        public bool ReadOnly { get; set; }

        /// <summary>Raised continuously while dragging.</summary>
        public event Action? PointsChanging;
        /// <summary>Raised when an edit is finished (mouse up / add / remove).</summary>
        public event Action? PointsChanged;

        public FanCurveEditor()
        {
            FlowDirection = FlowDirection.LeftToRight;
            ClipToBounds = true;
            Focusable = true;
            Cursor = Cursors.Arrow;
        }

        public List<CurvePoint> Points
        {
            get => _points;
            set { _points = value ?? new List<CurvePoint>(); Sort(); InvalidateVisual(); }
        }

        private void Sort() => _points.Sort((a, b) => a.T.CompareTo(b.T));

        private Rect Plot => new(44, 12, Math.Max(10, ActualWidth - 44 - 14), Math.Max(10, ActualHeight - 12 - 30));
        private Point ToScreen(double t, double p) { var r = Plot; return new Point(r.Left + (t - TMin) / (TMax - TMin) * r.Width, r.Bottom - p / 100.0 * r.Height); }
        private (double t, double p) FromScreen(Point s) { var r = Plot; return (TMin + (s.X - r.Left) / r.Width * (TMax - TMin), (r.Bottom - s.Y) / r.Height * 100.0); }

        private int HitTest(Point pos)
        {
            for (int i = 0; i < _points.Count; i++)
            {
                var sp = ToScreen(_points[i].T, _points[i].P);
                if ((sp - pos).Length <= 11) return i;
            }
            return -1;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (ReadOnly) return;
            Focus();
            var pos = e.GetPosition(this);
            if (e.ClickCount == 2 && HitTest(pos) < 0)
            {
                var (t, p) = FromScreen(pos);
                t = Math.Round(Math.Clamp(t, TMin, TMax));
                p = Math.Round(Math.Clamp(p, 0, 100));
                if (_points.Any(x => Math.Abs(x.T - t) < 1)) return;
                _points.Add(new CurvePoint(t, p));
                Sort();
                InvalidateVisual();
                PointsChanged?.Invoke();
                return;
            }
            _drag = HitTest(pos);
            if (_drag >= 0) CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var pos = e.GetPosition(this);
            _mouse = pos;
            if (_drag >= 0 && e.LeftButton == MouseButtonState.Pressed)
            {
                var (t, p) = FromScreen(pos);
                double lo = _drag > 0 ? _points[_drag - 1].T + 1 : TMin;
                double hi = _drag < _points.Count - 1 ? _points[_drag + 1].T - 1 : TMax;
                _points[_drag].T = Math.Round(Math.Clamp(t, lo, hi));
                _points[_drag].P = Math.Round(Math.Clamp(p, 0, 100));
                PointsChanging?.Invoke();
            }
            else
            {
                _hoverPt = HitTest(pos);
                Cursor = !ReadOnly && _hoverPt >= 0 ? Cursors.Hand : Cursors.Arrow;
            }
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (_drag >= 0)
            {
                _drag = -1;
                ReleaseMouseCapture();
                PointsChanged?.Invoke();
            }
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            if (ReadOnly) return;
            int i = HitTest(e.GetPosition(this));
            if (i >= 0 && _points.Count > 2)
            {
                _points.RemoveAt(i);
                _hoverPt = -1;
                InvalidateVisual();
                PointsChanged?.Invoke();
            }
            e.Handled = true;
        }

        protected override void OnMouseLeave(MouseEventArgs e) { _mouse = null; _hoverPt = -1; InvalidateVisual(); }

        private FormattedText T(string s, double size, Color c, bool bold = false) =>
            new(s, He, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, new SolidColorBrush(c), VisualTreeHelper.GetDpi(this).PixelsPerDip);

        protected override void OnRender(DrawingContext dc)
        {
            if (ActualWidth < 60 || ActualHeight < 60) return;
            var r = Plot;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(10, 255, 255, 255)), null, r, 6, 6);

            var grid = new Pen(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)), 1);
            var lbl = Color.FromRgb(0x6B, 0x76, 0x8A);
            for (int p = 0; p <= 100; p += 20)
            {
                var a = ToScreen(TMin, p); var b = ToScreen(TMax, p);
                dc.DrawLine(grid, new Point(a.X, Math.Round(a.Y) + .5), new Point(b.X, Math.Round(b.Y) + .5));
                var ft = T($"{p}%", 10.5, lbl);
                dc.DrawText(ft, new Point(r.Left - ft.Width - 8, a.Y - ft.Height / 2));
            }
            for (int t = 20; t <= 100; t += 10)
            {
                var a = ToScreen(t, 0); var b = ToScreen(t, 100);
                dc.DrawLine(grid, new Point(Math.Round(a.X) + .5, a.Y), new Point(Math.Round(b.X) + .5, b.Y));
                var ft = T($"{t}°", 10.5, lbl);
                dc.DrawText(ft, new Point(a.X - ft.Width / 2, r.Bottom + 6));
            }

            // hardware floor
            if (MinPct > 0)
            {
                var top = ToScreen(TMin, MinPct);
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(18, 248, 113, 113)), null, new Rect(r.Left, top.Y, r.Width, r.Bottom - top.Y));
                var ft = T($"מינימום חומרה {MinPct:0}%", 10, Color.FromArgb(150, 248, 113, 113));
                dc.DrawText(ft, new Point(r.Right - ft.Width - 6, top.Y - ft.Height - 1));
            }

            if (_points.Count > 0)
            {
                var pts = _points.Select(p => ToScreen(p.T, p.P)).ToList();
                var first = ToScreen(TMin, _points[0].P);
                var last = ToScreen(TMax, _points[^1].P);

                var area = new StreamGeometry();
                using (var c = area.Open())
                {
                    c.BeginFigure(new Point(first.X, r.Bottom), true, true);
                    c.LineTo(first, false, false);
                    foreach (var p in pts) c.LineTo(p, false, false);
                    c.LineTo(last, false, false);
                    c.LineTo(new Point(last.X, r.Bottom), false, false);
                }
                area.Freeze();
                dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(80, Accent.R, Accent.G, Accent.B), Color.FromArgb(5, Accent.R, Accent.G, Accent.B), 90), null, area);

                var line = new StreamGeometry();
                using (var c = line.Open())
                {
                    c.BeginFigure(first, false, false);
                    foreach (var p in pts) c.LineTo(p, true, true);
                    c.LineTo(last, true, true);
                }
                line.Freeze();
                dc.DrawGeometry(null, new Pen(new SolidColorBrush(Accent), 2.4) { LineJoin = PenLineJoin.Round }, line);

                for (int i = 0; i < pts.Count; i++)
                {
                    bool hot = i == _hoverPt || i == _drag;
                    dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x15)), new Pen(new SolidColorBrush(Accent), hot ? 3 : 2.2), pts[i], hot ? 8 : 6, hot ? 8 : 6);
                    if (hot)
                    {
                        var ft = T($"{_points[i].T:0}°C → {_points[i].P:0}%", 12, Colors.White, true);
                        double x = Math.Clamp(pts[i].X - ft.Width / 2 - 8, r.Left, r.Right - ft.Width - 16);
                        double y = pts[i].Y - ft.Height - 22 < r.Top ? pts[i].Y + 14 : pts[i].Y - ft.Height - 22;
                        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 0x1A, 0x23, 0x32)), new Pen(new SolidColorBrush(Color.FromArgb(120, Accent.R, Accent.G, Accent.B)), 1), new Rect(x, y, ft.Width + 16, ft.Height + 8), 6, 6);
                        dc.DrawText(ft, new Point(x + 8, y + 4));
                    }
                }
            }

            // live marker
            if (LiveTemp is double lt && !double.IsNaN(lt))
            {
                double t = Math.Clamp(lt, TMin, TMax);
                var top = ToScreen(t, 100); var bot = ToScreen(t, 0);
                var livePen = new Pen(new SolidColorBrush(Color.FromArgb(150, Live.R, Live.G, Live.B)), 1.2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                dc.DrawLine(livePen, top, bot);
                if (LivePct is double lp)
                {
                    var p = ToScreen(t, lp);
                    dc.DrawEllipse(new SolidColorBrush(Live), new Pen(new SolidColorBrush(Color.FromRgb(0x0A, 0x0E, 0x15)), 2), p, 6, 6);
                    var ft = T($"עכשיו {lt:0}°C · {lp:0}%", 11.5, Live, true);
                    double x = p.X + 10 + ft.Width > r.Right ? p.X - 10 - ft.Width : p.X + 10;
                    dc.DrawText(ft, new Point(x, Math.Max(r.Top, p.Y - ft.Height - 6)));
                }
            }

            if (!ReadOnly && _mouse is Point m && r.Contains(m) && _drag < 0 && _hoverPt < 0)
            {
                var (t, p) = FromScreen(m);
                var ft = T($"{t:0}° / {p:0}%   ·   לחיצה כפולה מוסיפה נקודה, לחצן ימני מוחק", 10.5, Color.FromRgb(0x56, 0x61, 0x75));
                dc.DrawText(ft, new Point(r.Left + 8, r.Top + 6));
            }
        }
    }
}
