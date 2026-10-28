using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PulseWatch.Controls;
using PulseWatch.Core;

namespace PulseWatch.Views
{
    public partial class DashboardView : UserControl, IView
    {
        private int _span = 300;
        private readonly ObservableCollection<ChannelRow> _rows = new();
        private int _channelCount = -1;
        private int _tick;

        public DashboardView()
        {
            InitializeComponent();
            FanList.ItemsSource = _rows;
        }

        public void OnShown() { }

        private void Span_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && int.TryParse(rb.Tag as string, out int s)) { _span = s; if (IsLoaded) Refresh(); }
        }

        private void Cpu_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => MainWindow.Instance?.Navigate("NavSensors");
        private void Gpu_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => MainWindow.Instance?.Navigate("NavGpu");
        private void Cooling_Click(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("NavCooling");

        public void Refresh()
        {
            var hub = App.Hub;
            _tick++;

            // CPU
            var cpuT = hub.Sensor("@cpu.temp");
            CpuName.Text = cpuT?.Hardware ?? "";
            float ct = cpuT?.Value ?? float.NaN;
            CpuGauge.Set(ct);
            CpuTemp.Text = float.IsNaN(ct) ? "—" : ct.ToString("0");
            CpuTemp.Foreground = MainWindow.TempBrush(ct, 75, 88);
            CpuLoad.Text = Ui.Val("@cpu.load", "0", "%");
            float clk = hub.Value("@cpu.clock");
            CpuClock.Text = float.IsNaN(clk) ? "—" : $"{clk / 1000:0.00} GHz";
            CpuPower.Text = Ui.Val("@cpu.power", "0", " W");
            CpuPeak.Text = cpuT == null || float.IsNaN(cpuT.Max) ? "—" : $"{cpuT.Max:0}°C";
            CpuSpark.SetSeries(new[] { Ui.Series("@cpu.temp", "CPU", Ui.Cyan, 300) }.OfType<ChartSeries>());

            // GPU
            GpuName.Text = hub.Gpu?.Name ?? "";
            float gt = hub.Value("@gpu.temp");
            GpuGauge.Set(gt);
            GpuTemp.Text = float.IsNaN(gt) ? "—" : gt.ToString("0");
            GpuTemp.Foreground = MainWindow.TempBrush(gt, 75, 84);
            GpuHot.Text = $"{Ui.Val("@gpu.hotspot", "0", "°")} / {Ui.Val("@gpu.memtemp", "0", "°")}";
            GpuLoad.Text = Ui.Val("@gpu.load", "0", "%");
            GpuClock.Text = $"{Ui.Val("@gpu.clock")} / {Ui.Val("@gpu.memclock")} MHz";
            var live = hub.GpuLive;
            GpuPower.Text = live == null ? "—" : $"{live.PowerW:0} / {live.PowerLimitW:0} W";
            GpuFan.Text = live == null ? "—" : $"{live.FanPct}% · {Ui.Val("@gpu.fanrpm", "0", " RPM")}";
            GpuSpark.SetSeries(new[] { Ui.Series("@gpu.temp", "GPU", Ui.Green, 300) }.OfType<ChartSeries>());

            // Cooling
            PumpRpm.Text = Ui.Val("@aio.pump");
            RadRpm.Text = Ui.Val("@aio.fan");
            AioPanel.Visibility = hub.Aio != null ? Visibility.Visible : Visibility.Collapsed;
            List<string> hidden;
            lock (App.Config) hidden = App.Config.HiddenChannels.ToList();
            var channels = hub.Channels.Where(c => c.Kind != ChannelKind.Pump && c.Kind != ChannelKind.AioFans && !hidden.Contains(c.Id)).ToList();
            if (channels.Count != _channelCount)
            {
                _channelCount = channels.Count;
                _rows.Clear();
                foreach (var ch in channels) _rows.Add(new ChannelRow(ch));
            }
            foreach (var r in _rows) r.Update();

            // Charts
            var temps = new List<ChartSeries?>
            {
                Ui.Series("@cpu.temp", "CPU", Ui.Cyan, _span),
                Ui.Series("@gpu.temp", "GPU", Ui.Green, _span),
                Ui.Series("@gpu.hotspot", "GPU Hot Spot", Ui.Amber, _span),
                Ui.Series("@gpu.memtemp", "GPU VRAM", Ui.Pink, _span),
                Ui.Series("@mb.temp", L.T("לוח אם", "Motherboard"), Ui.Violet, _span),
                Ui.Series("@nvme.temp", "NVMe", Ui.Blue, _span),
            }.OfType<ChartSeries>().ToList();
            TempChart.SpanSeconds = _span;
            TempChart.SetSeries(temps);
            ChartLegend.Update(TempLegend, TempChart, temps, "°");

            var loads = new List<ChartSeries?>
            {
                Ui.Series("@cpu.load", "CPU", Ui.Cyan, _span),
                Ui.Series("@gpu.load", "GPU", Ui.Green, _span),
                Ui.Series("@ram.load", "RAM", Ui.Violet, _span),
            }.OfType<ChartSeries>().ToList();
            LoadChart.SpanSeconds = _span;
            LoadChart.SetSeries(loads);
            ChartLegend.Update(LoadLegend, LoadChart, loads, "%");

            // Tiles
            float used = hub.Value("@ram.used"), avail = hub.Value("@ram.avail");
            RamText.Text = Ui.Val("@ram.load", "0", "%");
            RamSub.Text = float.IsNaN(used) ? "" : $"{used:0.0} / {used + avail:0} GB";
            if (live != null && live.VramTotalMb > 0)
            {
                VramText.Text = $"{live.VramUsedMb / live.VramTotalMb * 100:0}%";
                VramSub.Text = $"{live.VramUsedMb / 1024:0.0} / {live.VramTotalMb / 1024:0} GB";
            }
            if (_tick % 5 == 1)
                DriveTemps.ItemsSource = hub.DriveTemps().Select(d => Tuple.Create(d.Model, d.Sensor.HasValue ? $"{d.Sensor.Value:0}°C" : "—")).ToList();
            float down = hub.Value("@net.down"), up = hub.Value("@net.up");
            NetDown.Text = float.IsNaN(down) ? "—" : FormatRate(down);
            NetUp.Text = float.IsNaN(up) ? "—" : FormatRate(up);
        }

        private static string FormatRate(float mbs) => mbs >= 1 ? $"{mbs:0.0} MB/s" : $"{mbs * 1024:0} KB/s";
    }
}
