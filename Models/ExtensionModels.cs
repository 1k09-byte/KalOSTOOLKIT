namespace KaliteKit.Models
{
    /// <summary>
    /// WinUI-free description of a force-installable browser extension. Shared
    /// by the main KaliteKit app and the KaliteKit Setup wizard so the extension
    /// catalog and the policy application are defined in exactly one place.
    /// The app's presentation-side <c>ExtensionItem</c> (with its IsSelected
    /// toggle) maps to this DTO when calling the service.
    /// </summary>
    public sealed class BrowserExtension
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>Chrome Web Store extension id (Chromium-family browsers).</summary>
        public string ChromeId { get; init; } = string.Empty;

        /// <summary>Add-on id as listed in install.rdf / manifest (Firefox-family).</summary>
        public string FirefoxId { get; init; } = string.Empty;

        /// <summary>Direct .xpi download URL used for force_installed policies.</summary>
        public string FirefoxUrl { get; init; } = string.Empty;
    }
}