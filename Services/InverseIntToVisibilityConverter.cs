using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace KalOS.Services
{
    /// <summary>
    /// Inverts an integer before mapping to Visibility. Zero is Visible; non-zero is Collapsed.
    /// Useful for showing empty states when a collection count is zero.
    /// </summary>
    public class InverseIntToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int i)
            {
                return i == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
