using System;
using Microsoft.UI.Xaml.Data;

namespace KalOS.Services;

public sealed class NullValueToEmptyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is double d && double.IsNaN(d) ? string.Empty : value;
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotImplementedException();
}
