using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KalOS.Services;

namespace KalOS.ViewModels;

/// <summary>
/// Installs UniGetUI (formerly WingetUI) — the GUI for winget/choco/scoop —
/// via winget, then optionally pins it to the taskbar. Used by the UniGetUI
/// panel on the Personalization page.
/// </summary>
public partial class WingetUiViewModel : ObservableObject
{
    // Stable winget ID for UniGetUI (installs per-user).
    private const string UniGetUiWingetId = "Marticliment.UniGetUI";

    // After a per-user install the exe lives under %LOCALAPPDATA%\Programs\<id>.
    private static readonly string[] UniGetUiCandidates =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "UniGetUI", "UniGetUI.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WingetUI", "UniGetUI.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UniGetUI", "UniGetUI.exe"),
    };

    private readonly LogService _log;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private string _statusText = "UniGetUI is not installed.";

    [ObservableProperty]
    private bool _showProgress;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private bool _hasError;

    public WingetUiViewModel(LogService log)
    {
        _log = log;
    }

    public string? UniGetUiExePath
    {
        get
        {
            foreach (string candidate in UniGetUiCandidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }

    /// <summary>Refreshes installed/pinned state from disk.</summary>
    public void RefreshState()
    {
        string? exe = UniGetUiExePath;
        IsInstalled = exe != null;
        IsPinned = exe != null && TaskbarPinHelper.IsPinned(exe);
        StatusText = exe == null
            ? "UniGetUI is not installed."
            : IsPinned
                ? "UniGetUI is installed and pinned to the taskbar."
                : "UniGetUI is installed but not pinned.";
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsInstalling) return;

        IsInstalling = true;
        HasError = false;
        IsInstalled = false;
        ShowProgress = true;
        ProgressValue = 0;
        StatusText = "Installing UniGetUI via winget...";

        try
        {
            _ = _log.WriteAsync("UniGetUI", "Install", "Starting winget install", isError: false);
            var result = await WingetHelper.RunAsync(
                $"install --id {UniGetUiWingetId} --source winget -e " +
                "--accept-package-agreements --accept-source-agreements --disable-interactivity --silent",
                ensureSource: true,
                default);

            if (!result.Success)
            {
                string detail = !string.IsNullOrWhiteSpace(result.StandardError)
                    ? result.StandardError
                    : result.StandardOutput;
                string message = $"winget failed (exit {result.ExitCode}): {detail.Trim()}";
                StatusError(message);
                _ = _log.WriteAsync("UniGetUI", "Install", message, isError: true);
                return;
            }

            ProgressValue = 90;
            string? exe = UniGetUiExePath;
            if (exe == null)
            {
                // winget may report success before the file appears; give it a moment.
                await Task.Delay(1500);
                exe = UniGetUiExePath;
            }

            if (exe == null)
            {
                StatusError("UniGetUI reported success, but UniGetUI.exe was not found. Check the winget log.");
                return;
            }

            IsInstalled = true;
            IsPinned = TaskbarPinHelper.PinToTaskbar(exe);
            ProgressValue = 100;
            StatusText = "UniGetUI installed" + (IsPinned ? " and pinned to the taskbar." : ".");
            _ = _log.WriteAsync(
                "UniGetUI",
                "Install",
                IsPinned ? "Installed and pinned to taskbar" : "Installed (pin failed or declined)",
                isError: !IsPinned);
        }
        catch (Exception ex)
        {
            StatusError($"Failed to install UniGetUI: {ex.Message}");
            _ = _log.WriteAsync("UniGetUI", "Install", ex.Message, isError: true);
        }
        finally
        {
            IsInstalling = false;
            ShowProgress = false;
            ProgressValue = 0;
        }
    }

    [RelayCommand]
    private void PinToTaskbar()
    {
        string? exe = UniGetUiExePath;
        if (exe == null)
        {
            StatusText = "UniGetUI must be installed before it can be pinned.";
            return;
        }

        IsPinned = TaskbarPinHelper.PinToTaskbar(exe);
        StatusText = IsPinned
            ? "UniGetUI is pinned to the taskbar."
            : "Could not pin UniGetUI to the taskbar. Pin it manually from Start.";
        _ = _log.WriteAsync("UniGetUI", "Pin", StatusText, isError: !IsPinned);
    }

    private void StatusError(string message)
    {
        HasError = true;
        StatusText = message;
    }
}