using System;
using System.Windows;
using System.Windows.Media;

namespace PulseWatch.Controls
{
    /// <summary>270° arc gauge. Colour shifts from accent to amber to red with the value.</summary>
    public sealed class RingGauge : FrameworkElement
    {
        public double Value { get; set; } = double.NaN;
        public double Min { get; set; }
        public double Max { get; set; } = 100;
        public double Warn { get; set; } = 75;
        public double Hot { get; set; } = 88;
        public double Thickness { get; set; } = 10;

        private static readonly Color Cool = Color.FromRgb(0x22, 0xD3, 0xEE);
        private static readonly Color Amber = Color.FromRgb(0xFB, 0xBF, 0x24);
        private static readonly Color Red = Color.FromRgb(0xF8, 0x71, 0x71);

        public RingGauge()
        {
            FlowDirection = FlowDirection.LeftToRight;
        }

        public void Set(double v)
        {
            if (Math.Abs(v - Value) < 0.05 && !double.IsNaN(Value)) return;
            Value = v;
            InvalidateVisual();
        }

        public Color CurrentColor => double.IsNaN(Value) ? Color.FromRgb(0x56, 0x61, 0x75) : Value >= Hot ? Red : Value >= Warn ? Amber : Cool;

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size < 20) return;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            double rad = size / 2 - Thickness / 2 - 1;

            DrawArc(dc, c, rad, 135, 270, new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)));
            if (!double.IsNaN(Value))
            {
                double f = Math.Clamp((Value - Min) / (Max - Min), 0, 1);
                if (f > 0.002)
                {
                    var col = CurrentColor;
                    var brush = new LinearGradientBrush(Color.FromArgb(170, col.R, col.G, col.B), col, 0);
                    DrawArc(dc, c, rad, 135, 270 * f, brush);
                }
            }
        }

        private void DrawArc(DrawingContext dc, Point c, double r, double startDeg, double sweepDeg, Brush brush)
        {
            double a0 = startDeg * Math.PI / 180, a1 = (startDeg + sweepDeg) * Math.PI / 180;
            var p0 = new Point(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
            var p1 = new Point(c.X + r * Math.Cos(a1), c.Y + r * Math.Sin(a1));
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(p0, false, false);
                ctx.ArcTo(p1, new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
            }
            g.Freeze();
            dc.DrawGeometry(null, new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
        }
    }
}
