using Microsoft.UI.Xaml.Controls;
using KalOS.ViewModels;
using System.Diagnostics;

namespace KalOS.Views
{
    public sealed partial class SdioPage : Page
    {
        public SdioViewModel ViewModel { get; }

        public SdioPage()
        {
            this.InitializeComponent();

            // Resolve ViewModel from DI container
            ViewModel = App.Services.GetService(typeof(SdioViewModel)) as SdioViewModel 
                        ?? throw new System.Exception("SdioViewModel not registered");

            DataContext = ViewModel;
        }
    }
}
