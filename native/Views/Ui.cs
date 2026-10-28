using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using PulseWatch.Controls;
using PulseWatch.Core;

namespace PulseWatch.Views
{
    public abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }
        protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public static class Ui
    {
        public static readonly Color Cyan = Color.FromRgb(0x22, 0xD3, 0xEE);
        public static readonly Color Violet = Color.FromRgb(0xA7, 0x8B, 0xFA);
        public static readonly Color Amber = Color.FromRgb(0xFB, 0xBF, 0x24);
        public static readonly Color Pink = Color.FromRgb(0xF4, 0x72, 0xB6);
        public static readonly Color Green = Color.FromRgb(0x34, 0xD3, 0x99);
        public static readonly Color Blue = Color.FromRgb(0x60, 0xA5, 0xFA);
        public static readonly Color Orange = Color.FromRgb(0xFB, 0x92, 0x3C);
        public static readonly Color Red = Color.FromRgb(0xF8, 0x71, 0x71);
        public static readonly Color[] Palette = { Cyan, Violet, Amber, Pink, Green, Blue, Orange, Red };

        public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

        /// <summary>How many samples cover the given number of seconds at the current poll rate.</summary>
        public static int Samples(int seconds)
        {
            int poll;
            lock (App.Config) poll = Math.Max(250, App.Config.PollMs);
            return Math.Clamp((int)Math.Round(seconds * 1000.0 / poll), 2, SensorHub.HistorySeconds);
        }

        public static ChartSeries? Series(string idOrAlias, string label, Color color, int seconds, string? format = null)
        {
            var s = App.Hub.Sensor(idOrAlias);
            if (s == null) return null;
            return new ChartSeries
            {
                Label = label,
                Color = color,
                Unit = s.Unit,
                Values = s.History.Last(Samples(seconds)),
                Format = format ?? (s.Type == "Temperature" ? "0.0" : "0.#")
            };
        }

        public static string Val(string alias, string fmt = "0", string unit = "")
        {
            float v = App.Hub.Value(alias);
            return float.IsNaN(v) ? "—" : v.ToString(fmt) + unit;
        }

        public static string ModeText(ChannelAssignment a, FanChannel ch)
        {
            return a.Mode switch
            {
                FanMode.Fixed => L.T($"קבוע {a.FixedPct:0}%", $"Fixed {a.FixedPct:0}%"),
                FanMode.Curve => L.T("עקומה · ", "Curve · ") + (App.Config.FindCurve(a.CurveId)?.ToString() ?? "?"),
                _ => ch.AutoLabel
            };
        }

        public static ChannelAssignment Assignment(string channelId)
        {
            lock (App.Config)
            {
                var prof = App.Config.ActiveProfile;
                if (!prof.Channels.TryGetValue(channelId, out var a))
                {
                    a = new ChannelAssignment();
                    prof.Channels[channelId] = a;
                }
                return a;
            }
        }

        public static string ChannelName(FanChannel ch)
        {
            lock (App.Config) return App.Config.ChannelLabels.TryGetValue(ch.Id, out var n) && !string.IsNullOrWhiteSpace(n) ? n : ch.DefaultName;
        }

        public static string RpmText(FanChannel ch)
        {
            if (ch.RpmSensorId == null) return L.T("אין טכומטר", "no tach");
            float v = App.Hub.Value(ch.RpmSensorId);
            return float.IsNaN(v) ? "—" : $"{v:0} RPM";
        }

        public static string PctText(FanChannel ch)
        {
            if (ch.Blocked) return L.T("נעול", "locked");
            if (ch.AppliedPct is double p) return $"{p:0}%";
            if (ch.PctSensorId != null)
            {
                float v = App.Hub.Value(ch.PctSensorId);
                if (!float.IsNaN(v)) return L.T($"{v:0}% (אוטו)", $"{v:0}% (auto)");
            }
            return L.T("אוטו", "auto");
        }
    }

    /// <summary>Row model for fan channel lists (dashboard + cooling page).</summary>
    public sealed class ChannelRow : Observable
    {
        public FanChannel Channel { get; }
        public ChannelRow(FanChannel ch) { Channel = ch; }

        private string _name = "", _rpm = "", _pct = "", _mode = "", _group = "";
        private double _bar;
        private bool _selected, _warn;
        private Brush _modeBrush = Brushes.Gray;

        public string Name { get => _name; set => Set(ref _name, value); }
        public string Group { get => _group; set => Set(ref _group, value); }
        public string Rpm { get => _rpm; set => Set(ref _rpm, value); }
        public string Pct { get => _pct; set => Set(ref _pct, value); }
        public string Mode { get => _mode; set => Set(ref _mode, value); }
        public double Bar { get => _bar; set => Set(ref _bar, value); }
        public bool Selected { get => _selected; set => Set(ref _selected, value); }
        public bool Warn { get => _warn; set => Set(ref _warn, value); }
        public Brush ModeBrush { get => _modeBrush; set => Set(ref _modeBrush, value); }

        public void Update()
        {
            var a = Ui.Assignment(Channel.Id);
            Name = Ui.ChannelName(Channel);
            Group = Channel.Group;
            Rpm = Ui.RpmText(Channel);
            Pct = Ui.PctText(Channel);
            Mode = Channel.Emergency ? L.T("חירום · 100%", "Emergency · 100%") : Channel.Blocked ? L.T("קריאה בלבד", "Read only") : Ui.ModeText(a, Channel);
            double pct = Channel.AppliedPct ?? (Channel.PctSensorId != null ? App.Hub.Value(Channel.PctSensorId) : double.NaN);
            Bar = double.IsNaN(pct) ? 0 : Math.Clamp(pct / 100.0, 0, 1);
            Warn = Channel.LastError != null || Channel.Blocked;
            ModeBrush = Ui.Res(Channel.Emergency ? "Hot" : Channel.Blocked ? "Warn" : a.Mode == FanMode.Curve ? "Accent" : a.Mode == FanMode.Fixed ? "Accent2" : "Muted");
        }
    }

    /// <summary>Converts a 0..1 fraction to a width for bar fills inside a fixed-width track.</summary>
    public sealed class FractionToWidth : System.Windows.Data.IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not double f || values[1] is not double w) return 0.0;
            return Math.Max(0, f * w);
        }
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
