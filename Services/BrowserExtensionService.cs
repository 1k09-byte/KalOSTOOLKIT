using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using KalOS.Models;

namespace KalOS.Services
{
    /// <summary>
    /// WinUI-free forced-extension logic, shared by the main KalOS app and the
    /// KalOS Setup wizard. Chromium-family browsers get an
    /// <c>ExtensionInstallForcelist</c> registry policy; Firefox-family
    /// browsers get a <c>distribution/policies.json</c> plus an
    /// <c>ExtensionSettings</c> registry policy (and both are cleared again on
    /// uninstall). Pure backend — no UI types.
    /// </summary>
    public static class BrowserExtensionService
    {
        /// <summary>The privacy extensions force-installed for every browser KalOS sets up.</summary>
        public static List<BrowserExtension> CreateDefaultExtensions() => new()
        {
            new BrowserExtension
            {
                Name = "uBlock Origin",
                ChromeId = "cjpalhdlnbpafiamejdnhcphjbkeiagm",
                FirefoxId = "uBlock0@raymondhill.net",
                FirefoxUrl = "https://addons.mozilla.org/firefox/downloads/latest/ublock-origin/latest.xpi"
            },
            new BrowserExtension
            {
                Name = "Privacy Badger",
                ChromeId = "pkehgijcmpdhfbdbbnkijodmdjhbjlgp",
                FirefoxId = "jid1-MnnxcxisBPnSXQ@jetpack",
                FirefoxUrl = "https://addons.mozilla.org/firefox/downloads/latest/privacy-badger17/latest.xpi"
            },
            new BrowserExtension
            {
                Name = "I still don't care about cookies",
                ChromeId = "edibdbjcniadpccecjdfdjjppcpchdlm",
                FirefoxId = "idcac-pub@guus.ninja",
                FirefoxUrl = "https://addons.mozilla.org/firefox/downloads/latest/istilldontcareaboutcookies/latest.xpi"
            },
            new BrowserExtension
            {
                Name = "SponsorBlock",
                ChromeId = "mnjggcdmjocbbbhaepdhchncahnbgone",
                FirefoxId = "sponsorBlocker@ajay.app",
                FirefoxUrl = "https://addons.mozilla.org/firefox/downloads/latest/sponsorblock/latest.xpi"
            },
        };

        /// <summary>
        /// Force-installs the given extensions for a browser. Chromium-family
        /// browsers use the <c>ExtensionInstallForcelist</c> registry policy;
        /// Firefox-family browsers get <c>policies.json</c> (forks that ship it)
        /// plus the <c>ExtensionSettings</c> registry policy. Never throws — each
        /// write is best-effort so a locked policy key can't abort an install.
        /// </summary>
        public static void ApplyExtensions(string browserName, bool isChromium, IEnumerable<BrowserExtension> extensions)
        {
            var selected = extensions
                .Where(e => !string.IsNullOrWhiteSpace(e.ChromeId) || !string.IsNullOrWhiteSpace(e.FirefoxId))
                .ToList();
            if (selected.Count == 0) return;

            if (isChromium)
            {
                string policyKeyPath = browserName switch
                {
                    "Brave" => @"SOFTWARE\Policies\BraveSoftware\Brave\ExtensionInstallForcelist",
                    _ => @"SOFTWARE\Policies\Chromium\ExtensionInstallForcelist"
                };

                try
                {
                    using var key = Registry.LocalMachine.CreateSubKey(policyKeyPath);
                    if (key != null)
                    {
                        int index = 1;
                        foreach (var ext in selected)
                        {
                            key.SetValue(index.ToString(), $"{ext.ChromeId};https://clients2.google.com/service/update2/crx", RegistryValueKind.String);
                            index++;
                        }
                    }
                }
                catch
                {
                    // Best-effort — policy keys can be locked down by group policy.
                }
            }
            else
            {
                // Write to policies.json if possible (reliable for forks like LibreWolf/Zen).
                string installDir = GetFirefoxEngineInstallDir(browserName);
                if (!string.IsNullOrEmpty(installDir))
                {
                    try
                    {
                        string distDir = Path.Combine(installDir, "distribution");
                        Directory.CreateDirectory(distDir);
                        File.WriteAllText(Path.Combine(distDir, "policies.json"), BuildFirefoxPoliciesJson(selected));
                    }
                    catch
                    {
                        // Best-effort — Program Files may be read-only in odd setups.
                    }
                }

                string policyKeyPath = browserName switch
                {
                    "LibreWolf" => @"SOFTWARE\Policies\LibreWolf\ExtensionSettings",
                    "Zen Browser" => @"SOFTWARE\Policies\Zen\ExtensionSettings",
                    _ => @"SOFTWARE\Policies\Mozilla\Firefox\ExtensionSettings"
                };

                try
                {
                    using var key = Registry.LocalMachine.CreateSubKey(policyKeyPath);
                    if (key != null)
                    {
                        foreach (var ext in selected)
                        {
                            using var extKey = key.CreateSubKey(ext.FirefoxId);
                            if (extKey != null)
                            {
                                extKey.SetValue("installation_mode", "force_installed", RegistryValueKind.String);
                                extKey.SetValue("install_url", ext.FirefoxUrl, RegistryValueKind.String);
                            }
                        }
                    }
                }
                catch
                {
                    // Best-effort.
                }
            }
        }

        /// <summary>
        /// Removes every forced-extension policy for a browser (registry keys +
        /// <c>policies.json</c>). Best-effort, used on uninstall.
        /// </summary>
        public static void ClearExtensionPolicies(string browserName, bool isChromium)
        {
            if (isChromium)
            {
                string policyKeyPath = browserName switch
                {
                    "Brave" => @"SOFTWARE\Policies\BraveSoftware\Brave\ExtensionInstallForcelist",
                    _ => @"SOFTWARE\Policies\Chromium\ExtensionInstallForcelist"
                };

                try
                {
                    Registry.LocalMachine.DeleteSubKeyTree(policyKeyPath, throwOnMissingSubKey: false);
                }
                catch
                {
                    // Best-effort
                }
            }
            else
            {
                string installDir = GetFirefoxEngineInstallDir(browserName);
                if (!string.IsNullOrEmpty(installDir))
                {
                    string policyFile = Path.Combine(installDir, "distribution", "policies.json");
                    if (File.Exists(policyFile))
                    {
                        try { File.Delete(policyFile); } catch { }
                    }
                }

                string policyKeyPath = browserName switch
                {
                    "LibreWolf" => @"SOFTWARE\Policies\LibreWolf\ExtensionSettings",
                    "Zen Browser" => @"SOFTWARE\Policies\Zen\ExtensionSettings",
                    _ => @"SOFTWARE\Policies\Mozilla\Firefox\ExtensionSettings"
                };
                try
                {
                    Registry.LocalMachine.DeleteSubKeyTree(policyKeyPath, throwOnMissingSubKey: false);
                }
                catch
                {
                    // Best-effort
                }
            }
        }

        /// <summary>
        /// Best-effort check of whether an extension is force-installed (policy)
        /// or present in a browser profile. Returns false on any failure.
        /// </summary>
        public static bool IsExtensionInstalled(string browserName, bool isChromium, string dataPath, BrowserExtension ext)
        {
            try
            {
                if (isChromium)
                {
                    string policyKeyPath = browserName switch
                    {
                        "Brave" => @"SOFTWARE\Policies\BraveSoftware\Brave\ExtensionInstallForcelist",
                        _ => @"SOFTWARE\Policies\Chromium\ExtensionInstallForcelist"
                    };

                    using (var key = Registry.LocalMachine.OpenSubKey(policyKeyPath))
                    {
                        if (key != null)
                        {
                            foreach (var valueName in key.GetValueNames())
                            {
                                string? val = key.GetValue(valueName) as string;
                                if (val != null && val.StartsWith(ext.ChromeId, StringComparison.OrdinalIgnoreCase))
                                {
                                    return true;
                                }
                            }
                        }
                    }

                    string userDataPath = Path.Combine(dataPath, "User Data");
                    if (Directory.Exists(userDataPath))
                    {
                        var profiles = Directory.GetDirectories(userDataPath)
                            .Where(d => Path.GetFileName(d).Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                                        Path.GetFileName(d).StartsWith("Profile ", StringComparison.OrdinalIgnoreCase));

                        foreach (var profile in profiles)
                        {
                            string extPath = Path.Combine(profile, "Extensions", ext.ChromeId);
                            if (Directory.Exists(extPath)) return true;
                        }
                    }
                }
                else
                {
                    string policyKeyPath = browserName switch
                    {
                        "LibreWolf" => @"SOFTWARE\Policies\LibreWolf\ExtensionSettings",
                        "Zen Browser" => @"SOFTWARE\Policies\Zen\ExtensionSettings",
                        _ => @"SOFTWARE\Policies\Mozilla\Firefox\ExtensionSettings"
                    };
                    using (var key = Registry.LocalMachine.OpenSubKey(policyKeyPath))
                    {
                        if (key != null)
                        {
                            var subKeyNames = key.GetSubKeyNames();
                            foreach (var name in subKeyNames)
                            {
                                if (name.Equals(ext.FirefoxId, StringComparison.OrdinalIgnoreCase)) return true;
                            }
                        }
                    }

                    string installDir = GetFirefoxEngineInstallDir(browserName);
                    if (!string.IsNullOrEmpty(installDir))
                    {
                        string policyFile = Path.Combine(installDir, "distribution", "policies.json");
                        if (File.Exists(policyFile))
                        {
                            string json = File.ReadAllText(policyFile);
                            if (json.Contains(ext.FirefoxId, StringComparison.OrdinalIgnoreCase)) return true;
                        }
                    }

                    string profilesPath = Path.Combine(dataPath, "Profiles");
                    if (browserName == "Zen Browser")
                    {
                        profilesPath = Directory.Exists(Path.Combine(dataPath, "Profiles"))
                            ? Path.Combine(dataPath, "Profiles")
                            : dataPath;
                    }

                    if (Directory.Exists(profilesPath))
                    {
                        var profiles = Directory.GetDirectories(profilesPath);
                        foreach (var profile in profiles)
                        {
                            string extDir = Path.Combine(profile, "extensions");
                            if (Directory.Exists(extDir))
                            {
                                if (File.Exists(Path.Combine(extDir, ext.FirefoxId + ".xpi")) ||
                                    Directory.Exists(Path.Combine(extDir, ext.FirefoxId)))
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // Best-effort
            }
            return false;
        }

        /// <summary>Generates the <c>policies.json</c> content for Firefox-family forks.</summary>
        public static string BuildFirefoxPoliciesJson(IEnumerable<BrowserExtension> extensions)
        {
            var list = extensions.ToList();
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"policies\": {");
            sb.AppendLine("    \"ExtensionSettings\": {");

            for (int i = 0; i < list.Count; i++)
            {
                var ext = list[i];
                sb.AppendLine($"      \"{ext.FirefoxId}\": {{");
                sb.AppendLine("        \"installation_mode\": \"force_installed\",");
                sb.AppendLine($"        \"install_url\": \"{ext.FirefoxUrl}\"");
                sb.Append("      }");
                if (i < list.Count - 1) sb.AppendLine(",");
                else sb.AppendLine();
            }

            sb.AppendLine("    }");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string GetFirefoxEngineInstallDir(string browserName)
        {
            string[] possiblePaths = browserName switch
            {
                "LibreWolf" => new[] { @"C:\Program Files\LibreWolf" },
                "Zen Browser" => new[] { @"C:\Program Files\Zen Browser", @"C:\Program Files\Zen", @"C:\Program Files\Zen-Browser" },
                _ => Array.Empty<string>()
            };

            foreach (var path in possiblePaths)
            {
                if (Directory.Exists(path)) return path;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (browserName == "Zen Browser")
            {
                string localZen = Path.Combine(localAppData, "Programs", "Zen Browser");
                if (Directory.Exists(localZen)) return localZen;
                string localZen2 = Path.Combine(localAppData, "Programs", "Zen");
                if (Directory.Exists(localZen2)) return localZen2;
            }

            return string.Empty;
        }
    }
}