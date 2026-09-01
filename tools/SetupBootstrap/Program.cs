// KalOS Installer bootstrapper — a dependency-free .NET Framework 4.8 exe
// that runs on any Windows 10/11 out of the box (that is the point: its job
// is to check for, and auto-install, the .NET 9 Desktop Runtime that the
// KalOS app needs, so it cannot itself require .NET 9).
//
// It mirrors install-kalos.ps1's default mode end to end:
//   1. Elevate to administrator (the Setup wizard requires it anyway).
//   2. Check the internet connection.
//   3. Check for .NET 9 Desktop Runtime — download + silent-install when missing.
//   4. Resolve the newest KalOS-Setup zip from GitHub Releases.
//   5. Download, validate, and install the wizard to
//      %LOCALAPPDATA%\Programs\KalOSSetup (plus a Start Menu shortcut).
//   6. Launch the wizard.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace KalOS.Installer
{
    internal static class Program
    {
        private const string Owner = "1k09-byte";
        private const string Repo = "KalOSTOOLKIT";
        private const string ReleasesLatestUrl = "https://github.com/" + Owner + "/" + Repo + "/releases/latest";
        private const string DotNetRuntimeUrl = "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe";

        private static readonly string SetupDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "KalOSSetup");

        private static int Main()
        {
            try { Console.Title = "KalOS Installer"; } catch { }
            Console.WriteLine();
            Console.WriteLine("  KalOS Installer — downloads and launches the KalOS Setup wizard");
            Console.WriteLine("  ---------------------------------------------------------------");
            Console.WriteLine();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            EnsureAdministrator();
            CheckDependencies();
            string version = ResolveLatestVersion(out string setupZipUrl);

            string zipPath = Path.Combine(Path.GetTempPath(), "KalOS-Setup-" + version + ".zip");
            Step("Downloading KalOS Setup v" + version + " ...");
            DownloadFile(setupZipUrl, zipPath);

            string staging = Path.Combine(Path.GetTempPath(), "KalOS-Setup-" + version);
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            try
            {
                ZipFile.ExtractToDirectory(zipPath, staging);
            }
            catch (Exception ex)
            {
                Fail("Extract failed (corrupt download?): " + ex.Message);
            }

            string setupExe = Path.Combine(staging, "KalOS.Setup.exe");
            if (!File.Exists(setupExe)) Fail("The release package is missing KalOS.Setup.exe.");
            Ok("Setup package contains the wizard.");

            Step("Installing KalOS Setup to " + SetupDir + " ...");
            Directory.CreateDirectory(SetupDir);
            foreach (var existing in Directory.GetFiles(SetupDir))
            {
                try { File.Delete(existing); } catch { }
            }
            foreach (var file in Directory.GetFiles(staging))
            {
                File.Copy(file, Path.Combine(SetupDir, Path.GetFileName(file)), true);
            }
            try { File.Delete(zipPath); } catch { }
            try { Directory.Delete(staging, true); } catch { }

            CreateStartMenuShortcut(Path.Combine(SetupDir, "KalOS.Setup.exe"), version);

            Console.WriteLine();
            Ok("KalOS Setup " + version + " installed successfully!");
            Console.WriteLine("  The setup wizard opens next — it installs KalOS, GPU drivers,");
            Console.WriteLine("  software and tweaks. Relaunch it any time from the Start Menu");
            Console.WriteLine("  (KalOS Setup) or run:");
            Console.WriteLine("      " + Path.Combine(SetupDir, "KalOS.Setup.exe"));
            Console.WriteLine();

            Step("Launching KalOS Setup ...");
            try
            {
                Process.Start(new ProcessStartInfo(Path.Combine(SetupDir, "KalOS.Setup.exe"))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
            }
            catch { }

            KeepOpenIfInteractive();
            return 0;
        }

        // ── dependency checks (mirror install-kalos.ps1) ───────────────────

        private static void EnsureAdministrator()
        {
            var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            if (principal.IsInRole(WindowsBuiltInRole.Administrator)) return;

            Step("Requesting administrator permission ...");
            string exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" });
                    Environment.Exit(0);
                }
                catch { }
            }
            Fail("Administrator permission is required. Right-click the exe and choose 'Run as administrator'.");
        }

        private static void CheckDependencies()
        {
            Step("Checking required dependencies");

            if (!InternetAvailable())
                Fail("An internet connection is required to download KalOS Setup and its dependencies.");
            Ok("Internet connection is available.");

            if (DotNetDesktopRuntimeInstalled())
            {
                Ok(".NET Desktop Runtime 9 is installed.");
                return;
            }

            Console.WriteLine("  .NET Desktop Runtime 9 is required and was not found.");
            Step("Downloading .NET 9 Desktop Runtime ...");
            string runtimeInstaller = Path.Combine(Path.GetTempPath(), "windowsdesktop-runtime-9-x64.exe");
            try
            {
                DownloadFile(DotNetRuntimeUrl, runtimeInstaller);
                Step("Installing .NET 9 Desktop Runtime ...");
                var psi = new ProcessStartInfo(runtimeInstaller, "/install /quiet /norestart")
                {
                    UseShellExecute = true,
                };
                using (var p = Process.Start(psi))
                {
                    p?.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                Fail("Could not install .NET 9 Desktop Runtime: " + ex.Message);
            }
            finally
            {
                try { File.Delete(runtimeInstaller); } catch { }
            }

            if (!DotNetDesktopRuntimeInstalled())
                Fail("The .NET 9 Desktop Runtime installation did not complete successfully.");
            Ok(".NET Desktop Runtime 9 installed.");
        }

        private static bool InternetAvailable()
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://github.com/status");
                req.Method = "HEAD";
                req.Timeout = 15000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    return (int)resp.StatusCode >= 200 && (int)resp.StatusCode < 500;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool DotNetDesktopRuntimeInstalled()
        {
            try
            {
                var psi = new ProcessStartInfo("dotnet", "--list-runtimes")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    if (p == null) return false;
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    return output.Contains("Microsoft.WindowsDesktop.App 9.");
                }
            }
            catch
            {
                return false;
            }
        }

        // ── GitHub release resolution (mirrors install-kalos.ps1) ──────────

        private static string ResolveLatestVersion(out string setupZipUrl)
        {
            setupZipUrl = null;
            Step("Checking latest KalOS release on " + Owner + "/" + Repo + " ...");

            string redirect;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(ReleasesLatestUrl);
                req.AllowAutoRedirect = false;
                req.Timeout = 15000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    redirect = resp.Headers["Location"];
                }
            }
            catch (Exception ex)
            {
                Fail("Failed to fetch latest release from GitHub: " + ex.Message);
                return null;
            }

            if (string.IsNullOrEmpty(redirect))
                Fail("No redirect location returned from GitHub.");

            Match versionMatch = Regex.Match(redirect, "/tag/v(.*)$");
            if (!versionMatch.Success)
                Fail("Could not parse version from redirect URL: " + redirect);
            string version = versionMatch.Groups[1].Value;

            string html = HttpGet("https://github.com/" + Owner + "/" + Repo +
                                  "/releases/expanded_assets/v" + version);

            Match asset = Regex.Match(html,
                "href=\"(/[^\"]+/releases/download/[^\"]+KalOS-Setup-[^\"]+win-x64\\.zip)\"");
            if (!asset.Success)
            {
                asset = Regex.Match(html,
                    "href=\"(/[^\"]+/releases/download/[^\"]+KalOS-Setup-[^\"]+\\.zip)\"");
            }
            if (!asset.Success)
                Fail("Could not locate a KalOS-Setup zip attached to release v" + version +
                     ". Please ensure publish-setup.ps1 output is uploaded to GitHub.");

            setupZipUrl = "https://github.com" + asset.Groups[1].Value;
            Ok("Latest version: " + version);
            return version;
        }

        private static string HttpGet(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = 15000;
            req.Accept = "text/html";
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        // ── plumbing ───────────────────────────────────────────────────────

        private static void DownloadFile(string url, string dest)
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.AllowAutoRedirect = true;
                req.Timeout = 30000;
                req.ReadWriteTimeout = 300000;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var src = resp.GetResponseStream())
                using (var dst = File.Create(dest))
                {
                    long total = resp.ContentLength;
                    var buffer = new byte[81920];
                    long read = 0;
                    int n;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        dst.Write(buffer, 0, n);
                        read += n;
                        if (total > 0)
                        {
                            Console.Write("\r    {0:0} / {1:0} MB      ",
                                read / 1024 / 1024, total / 1024 / 1024);
                        }
                    }
                    Console.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Fail("Download failed: " + ex.Message);
            }
        }

        private static void CreateStartMenuShortcut(string targetPath, string version)
        {
            try
            {
                string startMenu = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Microsoft", "Windows", "Start Menu", "Programs");
                string lnkPath = Path.Combine(startMenu, "KalOS Setup.lnk");

                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic lnk = shell.CreateShortcut(lnkPath);
                lnk.TargetPath = targetPath;
                lnk.WorkingDirectory = Path.GetDirectoryName(targetPath);
                lnk.Description = "KalOS Setup " + version + " — install KalOS, drivers, software and tweaks";
                lnk.Save();
                Ok("Shortcut created: " + lnkPath);
            }
            catch
            {
                Console.WriteLine("  Skipped the Start Menu shortcut (could not create it).");
            }
        }

        private static void Step(string msg)
        {
            Console.WriteLine();
            Console.WriteLine("==> " + msg);
        }

        private static void Ok(string msg)
        {
            Console.WriteLine("    " + msg);
        }

        private static void Fail(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: " + msg);
            Console.ResetColor();
            KeepOpenIfInteractive();
            Environment.Exit(1);
        }

        /// <summary>Double-clicked exes close instantly on exit — hold the window open.</summary>
        private static void KeepOpenIfInteractive()
        {
            try
            {
                if (Environment.UserInteractive && !Console.IsInputRedirected)
                {
                    Console.Write("Press Enter to close this window ...");
                    Console.ReadLine();
                }
            }
            catch { }
        }
    }
}
