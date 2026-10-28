using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PulseWatch.Views
{
    /// <summary>
    /// Small ⓘ next to a title; the explanation floats on hover instead of taking up space on the page.
    /// XAML: <c>&lt;v:HelpTip Help="{v:T He=…, En=…}"/&gt;</c>; code: <c>tip.Help = "…"</c>.
    /// </summary>
    public sealed class HelpTip : TextBlock
    {
        public static readonly DependencyProperty HelpProperty = DependencyProperty.Register(
            nameof(Help), typeof(string), typeof(HelpTip), new PropertyMetadata("", (d, e) => ((HelpTip)d).Apply((string)e.NewValue)));

        public string Help
        {
            get => (string)GetValue(HelpProperty);
            set => SetValue(HelpProperty, value);
        }

        public HelpTip()
        {
            Text = "";
            FontFamily = (FontFamily)Application.Current.FindResource("IconFont");
            FontSize = 13;
            Foreground = (Brush)Application.Current.FindResource("Faint");
            VerticalAlignment = VerticalAlignment.Center;
            Margin = new Thickness(8, 0, 0, 0);
            Cursor = Cursors.Help;
            Background = Brushes.Transparent;
            ToolTipService.SetInitialShowDelay(this, 120);
            ToolTipService.SetShowDuration(this, 60000);
            MouseEnter += (_, _) => Foreground = (Brush)Application.Current.FindResource("Accent");
            MouseLeave += (_, _) => Foreground = (Brush)Application.Current.FindResource("Faint");
        }

        private void Apply(string text)
        {
            Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
            ToolTip = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 420, LineHeight = 19 };
        }
    }
}
