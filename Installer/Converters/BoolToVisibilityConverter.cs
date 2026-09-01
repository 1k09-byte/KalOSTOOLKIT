using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace KalOS.Setup.Converters
{
    /// <summary>true → Visible, false → Collapsed (the classic BoolToVis).</summary>
    public sealed class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => value is true ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => value is Visibility.Visible;
    }
}
