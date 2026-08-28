using CommunityToolkit.Mvvm.ComponentModel;
using FluentIcons.Common;
using System.Collections.ObjectModel;

namespace KalOS.ViewModels
{
    public partial class InstallableItem : ObservableObject
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string WingetId { get; set; } = string.Empty;
        public string ChocolateyId { get; set; } = string.Empty;
        public string ScoopName { get; set; } = string.Empty;
        public Symbol IconSymbol { get; set; } = Symbol.Globe;

        [ObservableProperty]
        private bool _isInstalling;

        partial void OnIsInstallingChanged(bool value)
        {
            OnPropertyChanged(nameof(IsInstallEnabled));
            OnPropertyChanged(nameof(IsInstallVisible));
            OnPropertyChanged(nameof(IsUninstallVisible));
            OnPropertyChanged(nameof(IsDirectInstallVisible));
            OnPropertyChanged(nameof(IsCancelVisible));
            if (!value)
            {
                // The operation finished (or was canceled); drop the token source.
                _operationCts?.Dispose();
                _operationCts = null;
            }
        }

        private CancellationTokenSource? _operationCts;

        /// <summary>
        /// Starts a cancellable install/uninstall operation and returns a token
        /// the caller links with its own timeout. Only one operation per item.
        /// </summary>
        public CancellationToken BeginOperation()
        {
            _operationCts?.Cancel();
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();
            return _operationCts.Token;
        }

        /// <summary>Requests cancellation of the in-flight install/uninstall.</summary>
        public void CancelOperation()
        {
            try { _operationCts?.Cancel(); } catch { }
        }

        [ObservableProperty]
        private bool _isInstalled;

        partial void OnIsInstalledChanged(bool value)
        {
            OnPropertyChanged(nameof(IsInstallVisible));
            OnPropertyChanged(nameof(IsUninstallVisible));
        }

        [ObservableProperty]
        private bool _showSuccessNotice;

        [ObservableProperty]
        private bool _isError;

        [ObservableProperty]
        private string _statusText = string.Empty;

        [ObservableProperty]
        private double _progressValue;

        [ObservableProperty]
        private bool _showProgress;

        [ObservableProperty]
        private bool _isWingetAvailable = true;

        partial void OnIsWingetAvailableChanged(bool value)
        {
            OnPropertyChanged(nameof(IsInstallEnabled));
        }

        [ObservableProperty]
        private bool _isPackageManagerAvailable = true;

        partial void OnIsPackageManagerAvailableChanged(bool value)
        {
            OnPropertyChanged(nameof(IsInstallEnabled));
        }

        public string FallbackDownloadUrl { get; set; } = string.Empty;
        public string FallbackInstallerArgs { get; set; } = string.Empty;
        public FallbackInstallerType InstallerType { get; set; } = FallbackInstallerType.Exe;

        // Lived on the subclasses historically; hoisted here so the shared
        // InstallableItemTemplate can x:Bind them against the base type.
        public string IconPath { get; set; } = string.Empty;
        public ObservableCollection<ExtensionItem> Extensions { get; set; } = new();

        public bool IsInstallEnabled => !IsInstalling && (IsPackageManagerAvailable || !string.IsNullOrEmpty(FallbackDownloadUrl));

        public bool IsInstallVisible => !IsInstalled || IsInstalling;
        public bool IsUninstallVisible => IsInstalled && !IsInstalling;
        public bool IsDirectInstallVisible => !IsInstalling && !string.IsNullOrEmpty(FallbackDownloadUrl);
        public bool IsCancelVisible => IsInstalling;

        /// <summary>Only browsers carry extensions; software items always return false so the panel stays hidden.</summary>
        public virtual bool HasExtensions => Extensions.Count > 0;
    }

    public partial class BrowserItem : InstallableItem
    {
        public bool IsChromium { get; set; }
        public string DataPath { get; set; } = string.Empty;
    }

    public partial class SoftwareItem : InstallableItem
    {
    }

    public enum FallbackInstallerType
    {
        Exe,
        Msi
    }

    public partial class ExtensionItem : ObservableObject
    {
        [ObservableProperty]
        private bool _isSelected;

        public string Name { get; set; } = string.Empty;
        public string ChromeId { get; set; } = string.Empty;
        public string FirefoxId { get; set; } = string.Empty;
        public string FirefoxUrl { get; set; } = string.Empty;
    }
}
