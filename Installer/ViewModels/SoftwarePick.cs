using CommunityToolkit.Mvvm.ComponentModel;
using KaliteKit.Models;

namespace KaliteKit.Setup.ViewModels
{
    /// <summary>
    /// One selectable software item in the wizard's Software page. A thin
    /// observable wrapper around a <see cref="CatalogEntry"/> carrying an
    /// <see cref="IsSelected"/> checkbox. Top-level (not nested) so the
    /// <c>[ObservableProperty]</c> source generator emits its
    /// <c>IsSelected</c> property correctly.
    /// </summary>
    public sealed partial class SoftwarePick : ObservableObject
    {
        public CatalogEntry Entry { get; init; } = null!;
        [ObservableProperty] private bool _isSelected;
    }
}
