using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using PulseWatch.Core;

namespace PulseWatch.Views
{
    /// <summary>
    /// On-screen display: a small always-on-top panel that mouse clicks pass through. Shows over windowed and
    /// borderless-fullscreen games; exclusive fullscreen hides every normal window, this one included.
    /// FPS comes from <see cref="FpsMonitor"/> (Windows event tracing, nothing injected into the game).
    /// </summary>
    public sealed class OsdWindow : Window
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);
        [StructLayout(LayoutKind.Sequential)] private struct Rect32 { public int Left, Top, Right, Bottom; }

        /// <summary>Set by <see cref="Automation"/> while FPS is on.</summary>
        public FpsMonitor? Fps { get; set; }
        private const int GwlExStyle = -20, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExLayered = 0x80000;

        private readonly TextBlock _text = new() { FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 13, LineHeight = 19 };
        private readonly Border _box;

        private static readonly Brush Label = Frozen(0x9A, 0xA6, 0xB8), Normal = Frozen(0xF1, 0xF5, 0xF9),
                                      Warm = Frozen(0xFB, 0xBF, 0x24), Hot = Frozen(0xF8, 0x71, 0x71), Cpu = Frozen(0x38, 0xBD, 0xF8), Gpu = Frozen(0x34, 0xD3, 0x99);

        private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

        /// <summary>One value the OSD can show. <see cref="Tag"/> is the short label in front of it (empty = none).</summary>
        public sealed record Item(string Key, string Group, string He, string En, string Sensor, string Fmt, string Unit, string Tag = "",
                                  double Warm = double.NaN, double Hot = double.NaN, double Scale = 1);

        /// <summary>Everything the OSD can show, grouped (cpu / gpu / sys) and in display order.</summary>
        public static readonly Item[] Items =
        {
            new("cpu.temp", "cpu", "טמפרטורה", "Temperature", "@cpu.temp", "0", "°", "", 75, 88),
            new("cpu.ccd", "cpu", "טמפ' CCD", "CCD temp", "@cpu.ccd", "0", "°", "CCD", 80, 90),
            new("cpu.load", "cpu", "עומס", "Load", "@cpu.load", "0", "%"),
            new("cpu.power", "cpu", "הספק", "Power", "@cpu.power", "0", " W"),
            new("cpu.clock", "cpu", "תדר", "Clock", "@cpu.clock", "0.00", " GHz", "", Scale: 0.001),
            new("gpu.temp", "gpu", "טמפרטורה", "Temperature", "@gpu.temp", "0", "°", "", 75, 84),
            new("gpu.load", "gpu", "עומס", "Load", "@gpu.load", "0", "%"),
            new("gpu.power", "gpu", "הספק", "Power", "@gpu.power", "0", " W"),
            new("gpu.clock", "gpu", "תדר ליבה", "Core clock", "@gpu.clock", "0", " MHz"),
            new("gpu.hotspot", "gpu", "Hot Spot", "Hot spot", "@gpu.hotspot", "0", "°", "HS", 90, 100),
            new("gpu.memtemp", "gpu", "טמפ' VRAM", "VRAM temp", "@gpu.memtemp", "0", "°", "VRAM", 90, 100),
            new("gpu.memclock", "gpu", "תדר זיכרון", "Memory clock", "@gpu.memclock", "0", " MHz", "Mem"),
            new("gpu.fan", "gpu", "מאוורר", "Fan", "@gpu.fanrpm", "0", " rpm", "Fan"),
            new("gpu.vram", "gpu", "VRAM בשימוש", "VRAM used", "@gpu.vram", "0.0", " GB", "Used", Scale: 1 / 1024.0),
            new("mb.temp", "sys", "לוח אם", "Motherboard", "@mb.temp", "0", "°", "MB", 60, 75),
            new("nvme.temp", "sys", "NVMe", "NVMe", "@nvme.temp", "0", "°", "NVMe", 65, 75),
            new("aio.pump", "sys", "משאבה", "Pump", "@aio.pump", "0", " rpm", "Pump"),
            new("aio.fan", "sys", "מאווררי רדיאטור", "Radiator fans", "@aio.fan", "0", " rpm", "Rad"),
            new("psu.power", "sys", "ספק כוח · הספק", "PSU power", "@psu.power", "0", " W", "PSU"),
            new("psu.temp", "sys", "ספק כוח · טמפ'", "PSU temp", "@psu.temp", "0", "°", "PSU", 60, 70),
            new("ram.load", "sys", "עומס RAM", "RAM load", "@ram.load", "0", "%", "RAM"),
            new("ram.used", "sys", "RAM בשימוש", "RAM used", "@ram.used", "0.0", " GB", "RAM"),
        };

        public OsdWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            FlowDirection = FlowDirection.LeftToRight;
            Title = "PulseWatch OSD";
            _box = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6, 12, 7), Child = _text };
            Content = _box;
            SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(this).Handle;
                SetWindowLong(h, GwlExStyle, GetWindowLong(h, GwlExStyle) | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExLayered);
            };
            SizeChanged += (_, _) => Place();
        }

        public void Place()
        {
            string corner;
            double opacity;
            lock (App.Config) { corner = App.Config.Osd.Corner; opacity = App.Config.Osd.Opacity; }
            _box.Background = new SolidColorBrush(Color.FromArgb((byte)(Math.Clamp(opacity, 0.2, 1) * 255), 0x0B, 0x10, 0x18));
            // placed in physical pixels on the chosen screen (each screen can have its own scaling);
            // moving to a screen with another scaling resizes the window, which calls Place again
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            string device;
            lock (App.Config) device = App.Config.Osd.Screen;
            var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device) ?? System.Windows.Forms.Screen.PrimaryScreen!;
            var wa = screen.WorkingArea;
            GetWindowRect(hwnd, out var r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top, m = 14;
            int x = corner is "tr" or "br" ? wa.Right - w - m : corner is "tc" or "bc" ? wa.Left + (wa.Width - w) / 2 : wa.Left + m;
            int y = corner is "bl" or "bc" or "br" ? wa.Bottom - h - m : wa.Top + m;
            if (x != r.Left || y != r.Top) SetWindowPos(hwnd, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010);   // TOPMOST, NOSIZE | NOACTIVATE
        }

        public void Update()
        {
            OsdConfig c;
            lock (App.Config) c = App.Config.Osd;
            var hub = App.Hub;
            bool line = c.Layout == "line";
            double size = Math.Clamp(c.Size, 9, 20);
            if (Math.Abs(_text.FontSize - size) > 0.1) { _text.FontSize = size; _text.LineHeight = size * 1.45; }
            _box.Padding = line ? new Thickness(size * 0.75, size * 0.3, size * 0.75, size * 0.35) : new Thickness(size * 0.8, size * 0.45, size, size * 0.55);
            var lines = _text.Inlines;
            lines.Clear();
            bool first = true;
            // rows: one line per part · line: everything on one line with a divider between the parts
            void Head(string t, Brush color)
            {
                if (!first)
                {
                    if (line) lines.Add(new Run("│  ") { Foreground = Label });
                    else lines.Add(new LineBreak());
                }
                first = false;
                lines.Add(new Run(t + " ") { Foreground = color, FontWeight = FontWeights.SemiBold });
            }
            void Val(string sensor, string fmt, string unit, double warm = double.NaN, double hot = double.NaN, string sep = "   ")
            {
                double v = hub.Value(sensor);
                if (double.IsNaN(v)) return;
                var b = !double.IsNaN(hot) && v >= hot ? Hot : !double.IsNaN(warm) && v >= warm ? Warm : Normal;
                lines.Add(new Run(v.ToString(fmt) + unit) { Foreground = b });
                lines.Add(new Run(sep));
            }
            void Lbl(string t) => lines.Add(new Run(t + " ") { Foreground = Label });

            if (c.ShowFps && Fps != null)
            {
                Fps.Sample();
                var f = c.FpsSource is "fg" or "" ? Fps.Foreground() : Fps.OnScreen(c.FpsSource);
                if (f is { } fr)
                {
                    Head("FPS", Warm);
                    lines.Add(new Run($"{fr.fps:0}") { Foreground = Normal, FontWeight = FontWeights.SemiBold });
                    string n = fr.name.Length > 16 ? fr.name[..16] : fr.name;
                    lines.Add(new Run("  " + n + "   ") { Foreground = Label, FontSize = size * 0.85 });
                }
            }

            var chosen = new HashSet<string>(c.Items);
            foreach (var (group, head, color) in new[] { ("cpu", "CPU", Cpu), ("gpu", "GPU", Gpu), ("sys", "SYS", Label) })
            {
                var items = Items.Where(i => i.Group == group && chosen.Contains(i.Key) && !double.IsNaN(hub.Value(i.Sensor))).ToList();
                if (items.Count == 0) continue;
                Head(head, color);
                int k = 0;
                foreach (var it in items)
                {
                    // long rows wrap after five values (rows layout only)
                    if (!line && k > 0 && k % 5 == 0) { lines.Add(new LineBreak()); lines.Add(new Run("        ")); }
                    k++;
                    if (it.Tag.Length > 0) Lbl(it.Tag);
                    double v = hub.Value(it.Sensor) * it.Scale;
                    var b2 = !double.IsNaN(it.Hot) && v >= it.Hot ? Hot : !double.IsNaN(it.Warm) && v >= it.Warm ? Warm : Normal;
                    lines.Add(new Run(v.ToString(it.Fmt) + it.Unit) { Foreground = b2 });
                    lines.Add(new Run("   "));
                }
            }
            if (first) { Head("PulseWatch", Label); }
        }
    }
}
