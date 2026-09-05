using System;
using Microsoft.UI.Xaml.Data;

namespace KaliteKit.Services
{
    /// <summary>
    /// Returns the input string if it is not null or whitespace; otherwise returns null.
    /// Useful for bindings like ToolTipService.ToolTip where an empty string would still
    /// show an empty tooltip.
    /// </summary>
    public class StringNotEmptyToObjectConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string s && !string.IsNullOrWhiteSpace(s))
                return s;
            return null!;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
