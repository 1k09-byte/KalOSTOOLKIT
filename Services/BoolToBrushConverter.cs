using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace KaliteKit.Services;

public class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.Red);
    public Brush FalseBrush { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.Green);

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true ? TrueBrush : FalseBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}
