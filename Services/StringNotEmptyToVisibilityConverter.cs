using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace KaliteKit.Services
{
    /// <summary>
    /// Converts a string to a Visibility value. Non-empty strings are Visible; null or empty strings are Collapsed.
    /// </summary>
    public class StringNotEmptyToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string s)
            {
                return string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
