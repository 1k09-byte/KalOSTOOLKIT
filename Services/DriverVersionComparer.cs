using System;
using System.Collections.Generic;
using System.Linq;

namespace KalOS.Services
{
    /// <summary>
    /// Compares the driver version WMI reports against the marketing version a
    /// provider publishes. Vendors disagree on numbering schemes, so each gets
    /// its own strategy; when no honest mapping exists the comparer returns
    /// null instead of guessing.
    /// </summary>
    internal static class DriverVersionComparer
    {
        /// <summary>
        /// -1 = installed is older (update available), 0 = equal, 1 = installed
        /// newer, null = the pair cannot be meaningfully compared.
        /// </summary>
        public static int? Compare(string vendor, string installedWmi, string latest) => vendor switch
        {
            "NVIDIA" => CompareNvidia(installedWmi, latest),
            "AMD" => CompareAmd(installedWmi, latest),
            "Intel" => CompareSegments(installedWmi, latest),
            _ => null,
        };

        private static int? CompareAmd(string installed, string latestMarketing)
        {
            try
            {
                if (string.Equals(installed, latestMarketing, StringComparison.OrdinalIgnoreCase))
                    return 0;

                // Handle AMD branch mappings: 25.10.45.xx is the RDNA2 driver bundled in Adrenalin 26.8.1
                if (installed.StartsWith("25.10.45", StringComparison.OrdinalIgnoreCase) &&
                    latestMarketing.StartsWith("26.8", StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }

                // Only compare when the installed version is actually a marketing
                // Adrenalin version (YY.MM.N, middle segment a valid month). The
                // OS sometimes reports a WMI/vehicle version instead (e.g.
                // "32.0.15.5244" or "32.0.21013") which does NOT correspond to
                // the Adrenalin number — comparing it would be a guess. Those
                // pairs return null so the UI shows "Unknown" rather than a
                // misleading up/down-to-date verdict.
                if (!IsAmdMarketingVersion(installed))
                {
                    return null;
                }

                var seg = CompareSegments(installed, latestMarketing);
                return seg.HasValue ? seg.Value : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>True for Adrenalin marketing versions like "25.10.1" / "26.8.1" (exactly three parts, middle part a month 1-12).</summary>
        private static bool IsAmdMarketingVersion(string version)
        {
            var parts = version.Split('.');
            if (parts.Length != 3) return false;
            return int.TryParse(parts[1], out int month) && month >= 1 && month <= 12;
        }



        /// <summary>
        /// NVIDIA: WMI reports four dotted groups whose last group carries the
        /// marketing driver number minus its leading digit — "32.0.15.5244"
        /// corresponds to Game Ready 552.44, "32.0.15.7216" to 572.16. Reducing
        /// BOTH sides to their last four digits makes them directly comparable.
        /// </summary>
        private static int? CompareNvidia(string installed, string latest)
        {
            int? a = NvidiaComparableNumber(installed);
            int? b = NvidiaComparableNumber(latest);
            return a.HasValue && b.HasValue ? a.Value.CompareTo(b.Value) : null;
        }

        private static int? NvidiaComparableNumber(string version)
        {
            var digits = new string(version.Where(char.IsDigit).ToArray());
            if (digits.Length == 0) return null;
            int take = Math.Min(digits.Length, 5);
            return int.TryParse(digits.Substring(digits.Length - take), out int n) ? n : null;
        }

        /// <summary>
        /// Intel publishes the same version string WMI reports
        /// ("32.0.101.6989"), so plain segment-wise numeric comparison works.
        /// </summary>
        private static int? CompareSegments(string installed, string latest)
        {
            var a = ParseSegments(installed);
            var b = ParseSegments(latest);
            if (a == null || b == null) return null;

            int count = Math.Max(a.Count, b.Count);
            for (int i = 0; i < count; i++)
            {
                int left = i < a.Count ? a[i] : 0;
                int right = i < b.Count ? b[i] : 0;
                if (left != right) return left.CompareTo(right);
            }

            return 0;
        }

        private static List<int>? ParseSegments(string version)
        {
            var parts = version.Split('.');
            var nums = new List<int>(parts.Length);
            foreach (var part in parts)
            {
                if (!int.TryParse(part.Trim(), out int n)) return null;
                nums.Add(n);
            }

            return nums.Count > 0 ? nums : null;
        }
    }
}
