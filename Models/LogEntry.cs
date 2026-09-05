using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KaliteKit.Models;

public partial class LogEntry : ObservableObject
{
    [ObservableProperty]
    private DateTime _timestamp;

    [ObservableProperty]
    private string _category = string.Empty;

    [ObservableProperty]
    private string _operation = string.Empty;

    [ObservableProperty]
    private string _result = string.Empty;

    [ObservableProperty]
    private bool _isError;
}
