using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using KaliteKit.Services;

namespace KaliteKit.ViewModels;

/// <summary>Status-only VM — Windhawk is installed via the installer pipeline.</summary>
public partial class WindhawkViewModel : ObservableObject
{
    private readonly WindhawkManagerService _service;

    [ObservableProperty] private bool _isWindhawkInstalled;
    [ObservableProperty] private string _installedStateText = "Checking…";
    [ObservableProperty] private string _statusText = "Windhawk is configured during setup (fixed URL + windhawk.json).";

    public WindhawkViewModel(WindhawkManagerService service, LogService log)
    {
        _service = service;
    }

    public void RefreshState()
    {
        IsWindhawkInstalled = _service.IsInstalled();
        InstalledStateText = IsWindhawkInstalled ? "Installed" : "Not installed";
    }

    public Task LoadAsync()
    {
        RefreshState();
        return Task.CompletedTask;
    }
}
