using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PulseWatch.Core;

namespace PulseWatch.Views
{
    /// <summary>
    /// Graphics card page on PCs with an AMD card and no NVIDIA one. Marked experimental: see <see cref="AmdTuning"/>.
    /// Sliders use the ranges the driver reports; nothing reaches the card before Apply.
    /// </summary>
    public sealed class AmdGpuView : UserControl, IView
    {
        private readonly StackPanel _host = new();
        private readonly List<AmdAdapter> _adapters;
        private readonly Dictionary<AmdSetting, Slider> _sliders = new();

        public AmdGpuView(List<AmdAdapter> adapters)
        {
            _adapters = adapters;
            Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(24, 6, 24, 24), Content = _host };
            Build();
        }

        private void Build()
        {
            _host.Children.Clear();
            _sliders.Clear();
            _host.Children.Add(new Border
            {
                Background = Ui.Res("WarnSoft"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 10, 14, 10), Margin = new Thickness(0, 0, 0, 16),
                Child = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = L.T("ניסיוני: הכוונון של AMD נכתב לפי התיעוד של AMD ועדיין לא נבדק על כרטיס Radeon. שנה ערך אחד בכל פעם ובדוק יציבות. אם Radeon Software מחיל פרופיל משלו, הוא עלול לדרוס את הערכים.",
                               "Experimental: AMD tuning follows AMD's documentation and has not been tested on a Radeon card yet. Change one value at a time and check stability. If Radeon Software applies its own profile, it may override these values.")
                }
            });
            foreach (var a in _adapters) _host.Children.Add(Card(a));
        }

        private Border Card(AmdAdapter a)
        {
            var body = new StackPanel();
            body.Children.Add(new TextBlock { Text = a.Name, Style = (Style)FindResource("H2") });
            foreach (var s in a.Settings)
            {
                var slider = new Slider { Minimum = s.Min, Maximum = s.Max, Value = s.Current, TickFrequency = 1, IsSnapToTickEnabled = true };
                var value = new TextBlock { Style = (Style)FindResource("Num"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right };
                void Text() => value.Text = $"{slider.Value:0} {s.Unit}";
                slider.ValueChanged += (_, _) => Text();
                Text();
                _sliders[s] = slider;
                var head = new DockPanel { Margin = new Thickness(0, 14, 0, 2) };
                DockPanel.SetDock(value, Dock.Right);
                head.Children.Add(value);
                head.Children.Add(new TextBlock { Text = L.T($"{s.Name} (ברירת מחדל {s.Default})", $"{s.Name} (default {s.Default})"), Style = (Style)FindResource("Caption") });
                body.Children.Add(head);
                body.Children.Add(slider);
            }

            var apply = new Button { Content = L.T("החל על הכרטיס", "Apply to card"), Style = (Style)FindResource("Primary"), Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
            var reset = new Button { Content = L.T("חזרה לברירת מחדל", "Back to defaults"), Style = (Style)FindResource("Danger"), Padding = new Thickness(14, 6, 14, 6) };
            apply.Click += async (_, _) =>
            {
                // only what changed, one value per call
                var wanted = a.Settings.Select(s => (s, v: (int)Math.Round(_sliders[s].Value))).Where(x => x.v != x.s.Current).ToList();
                string? err = await Task.Run(() =>
                {
                    foreach (var (s, v) in wanted)
                    {
                        var e = AmdTuning.Set(a, s, v);
                        if (e != null) return e;
                    }
                    return null;
                });
                Done(err, L.T("הערכים הוחלו על הכרטיס", "Values applied to the card"));
            };
            reset.Click += async (_, _) => Done(await Task.Run(() => AmdTuning.ResetAll(a)), L.T("הכרטיס חזר לברירת המחדל", "The card is back to its defaults"));
            var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
            buttons.Children.Add(apply);
            buttons.Children.Add(reset);
            body.Children.Add(buttons);
            return new Border { Style = (Style)FindResource("CardBorder"), Margin = new Thickness(0, 0, 0, 16), Child = body };
        }

        private void Done(string? err, string ok)
        {
            MainWindow.Instance?.ShowToast(err ?? ok, err == null ? MainWindow.ToastKind.Info : MainWindow.ToastKind.Error);
            OnShown();
        }

        public void OnShown()
        {
            foreach (var a in _adapters)
            {
                if (!AmdTuning.Refresh(a)) continue;
                foreach (var s in a.Settings)
                    if (_sliders.TryGetValue(s, out var sl)) sl.Value = Math.Clamp(s.Current, s.Min, s.Max);
            }
        }

        public void Refresh() { }
    }
}
