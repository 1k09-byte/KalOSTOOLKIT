using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace KaliteKit.Services
{
    /// <summary>
    /// Inverts a boolean before mapping to Visibility:
    ///   true  → Collapsed (the element is hidden when the bound value is true)
    ///   false → Visible   (the element is shown when the bound value is false)
    ///
    /// This is the semantic opposite of <see cref="BoolToVisibilityConverter"/>. Use it for
    /// bindings like "hide the Install button when IsInstalled is true" — the name
    /// <c>InverseBoolToVis</c> in App.xaml signals the inverse mapping to anyone reading the XAML.
    /// </summary>
    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b)
            {
                return b ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is Visibility v)
            {
                return v != Visibility.Visible;
            }
            return false;
        }
    }
}
