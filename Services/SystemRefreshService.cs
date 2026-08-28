using System;
using System.Collections.Generic;
using System.Linq;

namespace KalOS.Services;

/// <summary>Shared refresh-rate state for live system monitoring features.</summary>
public sealed class SystemRefreshService
{
    public static IReadOnlyList<RefreshRateOption> RefreshRates { get; } = new[]
    {
        new RefreshRateOption("Every 1 second", 1),
        new RefreshRateOption("Every 3 seconds", 3),
        new RefreshRateOption("Every 5 seconds", 5),
        new RefreshRateOption("Every 10 seconds", 10),
        new RefreshRateOption("Every 30 seconds", 30)
    };

    private RefreshRateOption _selectedRate = RefreshRates.First(r => r.Seconds == 3);

    public RefreshRateOption SelectedRate
    {
        get => _selectedRate;
        set => _selectedRate = value ?? _selectedRate;
    }

    public event EventHandler? RefreshRateChanged;

    public void SetRate(RefreshRateOption rate)
    {
        if (rate == null || rate.Seconds == _selectedRate.Seconds) return;
        _selectedRate = rate;
        RefreshRateChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record RefreshRateOption(string Label, int Seconds);
