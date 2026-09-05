using System.Linq;
using KaliteKit.Models;
using KaliteKit.ViewModels;

namespace KaliteKit.Tests.Services;

/// <summary>
/// Regression test for the Phase 1a refactor: <see cref="BrowserViewModel"/>
/// must map <emphasis>every</emphasis> <see cref="SoftwareCatalog"/> entry onto
/// a UI item with the same identity fields (name, IDs, fallback URL, installer
/// kind). If the catalog grows a new item that the VM forgets to map, or a
/// field is dropped in the mapping, this test fails — the Browsers &amp; Software
/// page and the KaliteKit Setup wizard share the same catalog, so the two must stay
/// in lockstep.
/// </summary>
public class BrowserViewModelCatalogTests
{
    // Constructing the VM does not touch the UI thread or App.Services — only
    // the catalog and the pure mapping helpers. Safe in the test host.
    private static BrowserViewModel NewVm() => new();

    [Fact]
    public void EveryBrowserEntryIsMapped_OneToOne()
    {
        var vm = NewVm();

        var mapped = vm.Browsers.Cast<InstallableItem>().ToArray();
        Assert.Equal(SoftwareCatalog.Browsers.Count, mapped.Length);

        foreach (var entry in SoftwareCatalog.Browsers)
        {
            var item = mapped.FirstOrDefault(m => m.Name == entry.Name);
            Assert.NotNull(item);
            AssertEntryMapped(entry, item!);
            Assert.True(item is BrowserItem);
        }
    }

    [Fact]
    public void EveryAppEntryIsMapped_OneToOne()
    {
        var vm = NewVm();

        var mapped = vm.Software.Cast<InstallableItem>().ToArray();
        Assert.Equal(SoftwareCatalog.Apps.Count, mapped.Length);

        foreach (var entry in SoftwareCatalog.Apps)
        {
            var item = mapped.FirstOrDefault(m => m.Name == entry.Name);
            Assert.NotNull(item);
            AssertEntryMapped(entry, item!);
            Assert.True(item is SoftwareItem);
        }
    }

    [Fact]
    public void EveryRuntimeEntryIsMapped_OneToOne()
    {
        var vm = NewVm();

        var mapped = vm.Runtimes.Cast<InstallableItem>().ToArray();
        Assert.Equal(SoftwareCatalog.Runtimes.Count, mapped.Length);

        foreach (var entry in SoftwareCatalog.Runtimes)
        {
            var item = mapped.FirstOrDefault(m => m.Name == entry.Name);
            Assert.NotNull(item);
            AssertEntryMapped(entry, item!);
            Assert.True(item is RuntimeItem);
        }
    }

    private static void AssertEntryMapped(CatalogEntry entry, InstallableItem item)
    {
        Assert.Equal(entry.Description, item.Description);
        Assert.Equal(entry.WingetId, item.WingetId);
        Assert.Equal(entry.ChocolateyId, item.ChocolateyId);
        Assert.Equal(entry.ScoopName, item.ScoopName);
        Assert.Equal(entry.FallbackDownloadUrl, item.FallbackDownloadUrl);
        Assert.Equal(entry.FallbackInstallerArgs, item.FallbackInstallerArgs);
        Assert.Equal(
            entry.InstallerKind == CatalogInstallerKind.Msi ? FallbackInstallerType.Msi : FallbackInstallerType.Exe,
            item.InstallerType);
    }
}
