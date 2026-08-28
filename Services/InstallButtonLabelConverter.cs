using System;
using Microsoft.UI.Xaml.Data;

namespace KalOS.Services
{
    public class InstallButtonLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isInstalled && isInstalled)
                return "Installed";
            return "Install";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
