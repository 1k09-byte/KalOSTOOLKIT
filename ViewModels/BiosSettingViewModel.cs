using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using KaliteKit.Models.Bios;

namespace KaliteKit.ViewModels;

/// <summary>
/// One row in the BIOS settings list. Wraps a <see cref="BiosSetting"/> and
/// exposes the editor state the row UI binds against. Only dirty rows are sent
/// back to the provider on Apply.
/// </summary>
public sealed class BiosSettingViewModel : INotifyPropertyChanged
{
    private readonly BiosSetting _setting;

    /// <summary>
    /// Maps display labels → raw values for the ComboBox.
    /// SCEWIN options are often raw hex like "0x00", "0x01", or ints like "30".
    /// We produce human-readable labels like "Option 1 (0x00)" and map back
    /// to the raw value on output.
    /// </summary>
    private readonly List<(string Display, string Raw)> _optionMap = new();

    public BiosSettingViewModel(BiosSetting setting)
    {
        _setting = setting;

        if (setting.PossibleValues is { Count: > 0 })
        {
            foreach (var raw in setting.PossibleValues)
            {
                var label = FormatOptionLabel(raw, setting.Name);
                _optionMap.Add((label, raw));
                DisplayOptions.Add(label);
            }
        }

        _selectedIndex = FindIndex(setting.CurrentValue);
        _editedString = _currentValue;
        _isIntegerParsed = int.TryParse(_currentValue, out var parsed);
        _editedInt = _isIntegerParsed ? parsed : 0;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Read-only facts ────────────────────────────────────────────────────

    public string Name => _setting.Name;
    public string DataType => _setting.DataType;
    public bool IsSensitive => _setting.IsSensitive;
    public bool IsReadOnly => _setting.IsReadOnly;
    public string CurrentValue => _setting.CurrentValue;

    /// <summary>Human-readable labels for the ComboBox.</summary>
    public ObservableCollection<string> DisplayOptions { get; } = new();

    /// <summary>Raw values for backward-compat and the old enum path.</summary>
    public ObservableCollection<string> PossibleValues { get; } = new();

    public bool IsEnum => _setting.DataType == BiosDataType.Enum && _optionMap.Count > 0 && !_setting.IsReadOnly;
    public bool IsInteger => _setting.DataType == BiosDataType.Integer && !_setting.IsReadOnly;
    public bool IsTextEditor => IsEditable && !IsEnum && !IsInteger;
    public bool IsPasswordEditable => _setting.DataType == BiosDataType.Password;
    public bool IsEditable => !IsPasswordEditable && !_setting.IsReadOnly;

    private string _currentValue => _setting.CurrentValue;

    public string CurrentValueLabel
    {
        get
        {
            if (IsEnum)
            {
                var match = _optionMap.FirstOrDefault(o =>
                    string.Equals(o.Raw, _currentValue, StringComparison.OrdinalIgnoreCase));
                return match.Display ?? _currentValue;
            }
            return _currentValue;
        }
    }

    public string CurrentValueText => $"Firmware: {CurrentValueLabel}";
    public string Description => _setting.Description ?? string.Empty;
    public bool HasDescription => !string.IsNullOrWhiteSpace(_setting.Description);
    public string SensitiveBadge => IsSensitive ? "boot / security critical" : string.Empty;
    public bool IsSensitiveVisible => IsSensitive;
    public string ReadOnlyBadge => IsReadOnly ? "read-only" : string.Empty;
    public bool IsReadOnlyVisible => IsReadOnly;
    public string DirtyBadge => IsDirty ? "edited" : string.Empty;
    public bool IsDirtyVisible => IsDirty;

    // ── Editor state ──────────────────────────────────────────────────────

    private int _selectedIndex;
    private string _editedString;
    private int _editedInt;
    private bool _isIntegerParsed;
    private bool _isDirty;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < 0) return;
            if (_selectedIndex == value) return;
            _selectedIndex = value;
            MarkDirty();
            Notify(nameof(SelectedIndex));
        }
    }

    public string? SelectedRawValue => _selectedIndex >= 0 && _selectedIndex < _optionMap.Count
        ? _optionMap[_selectedIndex].Raw
        : null;

    public string EditedString
    {
        get => _editedString;
        set
        {
            if (_editedString == value) return;
            _editedString = value;
            MarkDirty();
            Notify(nameof(EditedString));
        }
    }

    public int EditedInt
    {
        get => _editedInt;
        set
        {
            if (_editedInt == value) return;
            _editedInt = value;
            MarkDirty();
            Notify(nameof(EditedInt));
        }
    }

    public bool IsDirty => _isDirty;

    /// <summary>The raw value to send to the provider if this row is included in an apply.</summary>
    public string OutputValue
    {
        get
        {
            if (IsEnum) return SelectedRawValue ?? _currentValue;
            if (IsInteger) return _editedInt.ToString();
            return _editedString;
        }
    }

    private int FindIndex(string rawValue)
    {
        for (int i = 0; i < _optionMap.Count; i++)
        {
            if (string.Equals(_optionMap[i].Raw, rawValue, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return _optionMap.Count > 0 ? 0 : -1;
    }

    private void MarkDirty()
    {
        _isDirty = true;
        Notify(nameof(IsDirty));
        Notify(nameof(IsDirtyVisible));
    }

    public void ResetToFirmware()
    {
        _selectedIndex = FindIndex(_currentValue);
        _editedString = _currentValue;
        _isIntegerParsed = int.TryParse(_currentValue, out var parsed);
        if (_isIntegerParsed) _editedInt = parsed;
        _isDirty = false;
        Notify(nameof(SelectedIndex));
        Notify(nameof(EditedString));
        Notify(nameof(EditedInt));
        Notify(nameof(IsDirty));
        Notify(nameof(IsDirtyVisible));
    }

    public void MarkApplied()
    {
        _isDirty = false;
        Notify(nameof(IsDirty));
        Notify(nameof(IsDirtyVisible));
    }

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── Label formatting ──────────────────────────────────────────────────

    /// <summary>
    /// Produces a human-readable label from a raw SCEWIN option value by stripping 
    /// the hex choice index brackets (e.g. "[00]Disabled" -> "Disabled").
    /// </summary>
    private static string FormatOptionLabel(string raw, string settingName)
    {
        var trimmed = raw.Trim();

        if (trimmed.StartsWith('*'))
            trimmed = trimmed[1..].Trim();

        // Extract label from [XX]Label format
        var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\[[0-9A-Fa-f]+\](.*)$");
        if (match.Success)
        {
            var label = match.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(label))
                return label;
        }

        return trimmed;
    }
}