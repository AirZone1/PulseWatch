using System;
using System.Windows;
using System.Windows.Markup;
using PulseWatch.Core;

namespace PulseWatch.Views
{
    /// <summary>XAML: <c>Text="{l:T He=סקירה, En=Overview}"</c>. Resolved once when the view is created.</summary>
    [MarkupExtensionReturnType(typeof(string))]
    public sealed class T : MarkupExtension
    {
        public string He { get; set; } = "";
        public string En { get; set; } = "";

        public T() { }
        public T(string he, string en) { He = he; En = en; }

        public override object ProvideValue(IServiceProvider serviceProvider) => L.T(He, En);
    }

    /// <summary>XAML: <c>FlowDirection="{l:Flow}"</c> — right-to-left in Hebrew, left-to-right in English.</summary>
    [MarkupExtensionReturnType(typeof(FlowDirection))]
    public sealed class Flow : MarkupExtension
    {
        public override object ProvideValue(IServiceProvider serviceProvider) => L.En ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
    }
}
