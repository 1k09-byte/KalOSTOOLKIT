using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using KalOS.Models;
using KalOS.Services;

namespace KalOS.ViewModels
{
    /// <summary>
    /// Backs the Personalization page's Tint Color section: the preset palette
    /// and the current selection, applied live to the window backdrop and
    /// persisted by <see cref="BackdropService"/>.
    /// </summary>
    public partial class PersonalizationViewModel : ObservableObject
    {
        private readonly BackdropService _backdropService;

        /// <summary>The full palette: Default (no tint) + 20 named colors.</summary>
        public IReadOnlyList<TintPreset> Tints { get; } = TintPresets.All;

        [ObservableProperty]
        private TintPreset? _selectedTint;

        public PersonalizationViewModel(BackdropService backdropService)
        {
            _backdropService = backdropService;

            // Restore the persisted choice: the matching preset card, the Default
            // card when no tint is set, or no card when a custom color is active.
            _selectedTint = string.IsNullOrEmpty(_backdropService.CurrentTint)
                ? Tints.First() // Default
                : Tints.FirstOrDefault(t =>
                    string.Equals(t.Hex, _backdropService.CurrentTint, StringComparison.OrdinalIgnoreCase));
        }

        partial void OnSelectedTintChanged(TintPreset? value)
        {
            if (value is null) return;
            _backdropService.SetTintColor(string.IsNullOrWhiteSpace(value.Hex) ? null : value.Hex);
        }

        /// <summary>Applies a custom color from the color picker (no preset card selected).</summary>
        public void ApplyCustomColor(Windows.UI.Color color)
        {
            SelectedTint = null;
            _backdropService.SetTintColor(TintPresets.ToHex(color));
        }
    }
}