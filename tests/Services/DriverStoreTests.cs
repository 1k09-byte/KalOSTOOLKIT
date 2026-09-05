using System;
using System.Collections.Generic;
using System.Linq;
using KaliteKit.Models;
using KaliteKit.Services;
using KaliteKit.ViewModels;
using Xunit;

namespace KaliteKit.Tests.Services
{
    /// <summary>
    /// Driver Store Manager tests. The classifier tests lock the spec's hard
    /// rules (10.3): Smart Cleanup can NEVER select a boot-critical or an
    /// in-use package, regardless of version or association state.
    /// </summary>
    public class DriverStoreTests
    {
        private static DriverPackageRecord Pkg(
            string infName,
            string version,
            bool bootCritical = false,
            bool inUse = false,
            bool inbox = false,
            string provider = "ACME",
            string className = "Display adapters") =>
            new DriverPackageRecord
            {
                InfName = infName,
                PublishedName = infName,
                OriginalInfName = "nv_dispi.inf",
                Provider = provider,
                Signer = "ACME Signing",
                DriverClass = className,
                DriverVersion = new Version(version),
                BootCritical = bootCritical,
                IsInbox = inbox,
                FolderLocation = $@"C:\Windows\System32\DriverStore\FileRepository\{infName}_x",
            };

        // ── Smart Cleanup hard rules (spec 5.5 / 10.3) ──────────────────

        [Fact]
        public void Cleanup_NeverSelectsBootCritical_RegardlessOfState()
        {
            var packages = new List<DriverPackageRecord>
            {
                Pkg("oem1.inf", "30.0.1.0"),                       // newest → keeper
                Pkg("oem2.inf", "27.0.0.3", bootCritical: true),    // older, boot-critical
                Pkg("oem3.inf", "26.0.0.1", bootCritical: true),    // oldest, boot-critical
            };

            var candidates = SmartCleanupClassifier.GetCandidates(packages);

            Assert.DoesNotContain(candidates, c => c.Package.BootCritical);
            Assert.All(candidates, c => Assert.False(c.Package.BootCritical));
        }

        [Fact]
        public void Cleanup_NeverSelectsInUse_EvenWhenOlder()
        {
            var packages = new List<DriverPackageRecord>
            {
                Pkg("oem1.inf", "30.0.1.0"),
                Pkg("oem2.inf", "27.0.0.3", inUse: true),
                Pkg("oem3.inf", "25.0.0.1"),
            };

            var candidates = SmartCleanupClassifier.GetCandidates(packages);

            Assert.DoesNotContain(candidates, c => c.Package.InUseByPresentDevice);
            // The two unused old versions ARE candidates with reasoning.
            Assert.Equal(2, candidates.Count);
            Assert.All(candidates, c => Assert.StartsWith("older version", c.Reason));
        }

        [Fact]
        public void Cleanup_NewestIsAlwaysTheKeeper_EvenIfUnused()
        {
            var packages = new List<DriverPackageRecord>
            {
                Pkg("oem1.inf", "31.0.0.0"),          // newest, unused → kept
                Pkg("oem2.inf", "28.0.0.0"),
            };

            var candidates = SmartCleanupClassifier.GetCandidates(packages);

            Assert.Single(candidates);
            Assert.Equal("oem2.inf", candidates[0].Package.InfName);
        }

        [Fact]
        public void Cleanup_InboxPackagesAreNeverCandidates()
        {
            var packages = new List<DriverPackageRecord>
            {
                Pkg("oem1.inf", "10.0.0.0", inbox: true),
                Pkg("oem2.inf", "9.0.0.0", inbox: true),
            };

            Assert.Empty(SmartCleanupClassifier.GetCandidates(packages));
        }

        [Fact]
        public void Cleanup_SinglePackageGroupHasNoCandidates()
        {
            var packages = new List<DriverPackageRecord> { Pkg("oem1.inf", "10.0.0.0") };
            Assert.Empty(SmartCleanupClassifier.GetCandidates(packages));
        }

        [Fact]
        public void Cleanup_GroupsByOriginalInfName_NotStoreLabel()
        {
            // Different groups (different original INF + class) → no candidate
            // even though versions differ.
            var a1 = Pkg("oem1.inf", "10.0.0.0"); a1 = a1 with { OriginalInfName = "audio.inf" };
            var a2 = Pkg("oem2.inf", "12.0.0.0"); a2 = a2 with { OriginalInfName = "audio.inf" };
            var b1 = Pkg("oem3.inf", "5.0.0.0");  b1 = b1 with { OriginalInfName = "net.inf" };

            var candidates = SmartCleanupClassifier.GetCandidates(new[] { a1, a2, b1 });

            Assert.Single(candidates);
            Assert.Equal("oem1.inf", candidates[0].Package.InfName);
        }

        [Fact]
        public void Cleanup_DisconnectedDeviceMentionedInReason()
        {
            var old = Pkg("oem2.inf", "9.0.0.0");
            old = old with
            {
                AssociatedDevices = new[] { new AssociatedDevice(@"USB\VID_1234", "SuperDock", IsPresent: false) },
            };
            var packages = new List<DriverPackageRecord> { Pkg("oem1.inf", "10.0.0.0"), old };

            var candidates = SmartCleanupClassifier.GetCandidates(packages);

            var c = Assert.Single(candidates);
            Assert.Contains("SuperDock", c.Reason);
            Assert.Contains("disconnected", c.Reason);
        }

        // ── pnputil parser (fallback provider) ──────────────────────────

        [Fact]
        public void PnputilParser_ParsesBlocks()
        {
            const string output = """
                Published Name:     oem12.inf
                    Original Name:  nvlt.inf
                    Provider Name:  NVIDIA
                    Class Name:     Display adapters
                    Class GUID:     {4d36e968-e325-11ce-bfc1-08002be10318}
                    Driver Version: 10/05/2025 32.0.15.6109
                    Signer Name:    Microsoft Windows Hardware Compatibility Publisher

                Published Name:     oem7.inf
                    Original Name:  hdaudio.inf
                    Provider Name:  Microsoft
                    Class Name:     Sound, video and game controllers
                    Class GUID:     {4d36e96c-e325-11ce-bfc1-08002be10318}
                    Driver Version: 05/06/2024 10.0.26100.1
                    Signer Name:    Microsoft Windows
                """;

            var packages = PnputilParser.Parse(output);

            Assert.Equal(2, packages.Count);
            var nv = packages[0];
            Assert.Equal("oem12.inf", nv.PublishedName);
            Assert.Equal("nvlt.inf", nv.OriginalInfName);
            Assert.Equal("NVIDIA", nv.Provider);
            Assert.Equal(new Version(32, 0, 15, 6109), nv.DriverVersion);
            Assert.False(nv.IsInbox);

            // Inbox-like by signer.
            Assert.True(packages[1].IsInbox);
        }

        [Fact]
        public void PnputilParser_EmptyAndGarbageInput()
        {
            Assert.Empty(PnputilParser.Parse(string.Empty));
            Assert.Empty(PnputilParser.Parse("some random text"));
            Assert.Empty(PnputilParser.Parse("Published Name: nope\nProvider Name: x"));
        }

        // ── Backup folder naming (spec 5.2) ────────────────────────────

        [Fact]
        public void BackupFolderName_IsHumanReadable_AndCollisionSafe()
        {
            var p = Pkg("oem12.inf", "32.0.15.6109", provider: "NVIDIA");
            var name = p.BackupFolderName;

            Assert.Contains("NVIDIA", name);
            Assert.Contains("nv_dispi.inf", name);
            Assert.Contains("32.0.15.6109", name);
            Assert.DoesNotContain("/", name);
            Assert.DoesNotContain("\\", name);
            Assert.DoesNotContain(":", name);
        }

        // ── Offline validator (spec 5.1) ───────────────────────────────

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(@"C:\definitely\not\a\real\path")]
        public void OfflineValidator_RejectsInvalidRoots(string? path)
        {
            Assert.False(OfflineStoreValidator.IsValidOfflineRoot(path));
        }

        // ── Size formatting ────────────────────────────────────────────

        [Theory]
        [InlineData(0, "\u2014")]
        [InlineData(512, "512 B")]
        [InlineData(2048, "2 KB")]
        [InlineData(5 * 1024 * 1024, "5 MB")]
        public void SizeFormatter_FormatsBytes(long bytes, string expected)
        {
            Assert.Equal(expected, DriverPackageRow.FormatBytes(bytes));
        }
    }
}
