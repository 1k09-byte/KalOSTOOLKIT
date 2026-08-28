using CommunityToolkit.Mvvm.ComponentModel;

namespace KalOS.ViewModels;

/// <summary>
/// One row in the import-preview dialog: the imported value, what the live machine
/// currently has, whether it's valid to apply, and a checkbox to opt in/out.
/// </summary>
public partial class BiosDiffItem : ObservableObject
{
    public BiosDiffItem(string name, string current, string proposed, bool isValid, bool isSensitive)
    {
        Name = name;
        CurrentValue = current;
        ProposedValue = proposed;
        IsValid = isValid;
        IsSensitive = isSensitive;
        IsIncluded = isValid; // only valid rows are pre-checked
    }

    public string Name { get; }
    public string CurrentValue { get; }
    public string ProposedValue { get; }
    public bool IsValid { get; }
    public bool IsSensitive { get; }

    [ObservableProperty]
    private bool _isIncluded;

    public string Arrow => IsValid ? "   →   " : string.Empty;
    public string Note => !IsValid ? "not valid on this machine" : (IsSensitive ? "boot / security critical" : string.Empty);
    public bool ShowNote => !IsValid || IsSensitive;
    public bool ShowEditor => IsValid && !IsSensitive;
}