using System.Threading.Tasks;

namespace KalOS.Services.Bios;

/// <summary>
/// File-picker seam so the BIOS view model can run Export/Import as MVVM commands
/// without holding a XamlRoot. Production implementation lives in the view layer
/// (which owns the XamlRoot); unit tests inject a fake that returns fixed paths.
/// </summary>
public interface IBiosFilePicker
{
    /// <summary>Returns the chosen save path, or null when cancelled.</summary>
    Task<string?> PickExportPathAsync(string suggestedName);

    /// <summary>Returns the chosen open path, or null when cancelled.</summary>
    Task<string?> PickImportPathAsync();

    /// <summary>Returns the chosen firmware-tool executable path, or null when cancelled.</summary>
    Task<string?> PickToolPathAsync();
}