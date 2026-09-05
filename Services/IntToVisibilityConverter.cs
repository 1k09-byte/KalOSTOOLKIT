using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace KaliteKit.Services
{
    /// <summary>
    /// Converts an integer to a Visibility value. Values greater than zero are Visible; zero or less are Collapsed.
    /// </summary>
    public class IntToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int i)
            {
                return i > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
