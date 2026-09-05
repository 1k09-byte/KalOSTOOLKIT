using System;
using Microsoft.Extensions.DependencyInjection;
using KaliteKit.Setup.ViewModels;

namespace KaliteKit.Setup
{
    /// <summary>
    /// Main-app-only stand-in for the standalone installer's
    /// <c>KaliteKit.Setup.App</c> statics. The wizard pages, view model and
    /// pipeline are source-shared with the Installer project and reference
    /// <c>App.Wizard</c> / <c>App.MainWindow</c> / <c>App.Services</c>; when
    /// they are compiled into the consumer app, this class provides those
    /// members, backed by the main app's service provider.
    ///
    /// This file lives OUTSIDE Installer/ so the standalone wizard project
    /// never compiles it (its real Application-derived App would collide).
    /// </summary>
    public static class App
    {
        /// <summary>The shared service provider — the main app's DI container,
        /// which registers everything the pipeline needs.</summary>
        public static IServiceProvider Services => global::KaliteKit.App.Services;

        /// <summary>The wizard shell window — pages reach it for nav refreshes
        /// and the Finish page closes it to swap into the consumer UI.</summary>
        public static MainWindow? MainWindow { get; set; }

        /// <summary>The single wizard state object every page binds to.</summary>
        public static InstallerViewModel Wizard { get; private set; } = null!;

        /// <summary>Just the version string — the wizard title bar shows it.</summary>
        public static string AppVersion => global::KaliteKit.App.AppVersion;

        /// <summary>
        /// Called by the main app right before it creates the wizard window on
        /// a first run (or via --setup): builds the wizard view model from DI
        /// and pre-builds the software catalog so the Software page binds to a
        /// populated list the moment it loads.
        /// </summary>
        public static void InitializeWizard()
        {
            Wizard = Services.GetRequiredService<InstallerViewModel>();
            Wizard.BuildSoftwarePicks();
        }
    }
}
