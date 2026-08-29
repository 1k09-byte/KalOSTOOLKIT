using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔══════════════════════════════════════╗");
Console.WriteLine("║        KalOS Installer               ║");
Console.WriteLine("╚══════════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine();

string scriptUrl = "https://raw.githubusercontent.com/1k09-byte/KalOSTOOLKIT/main/install-kalos.ps1";
string scriptPath = Path.Combine(Path.GetTempPath(), "kalos-install.ps1");

// Download the install script
Console.ForegroundColor = ConsoleColor.White;
Console.WriteLine("Downloading install script...");
Console.ResetColor();

try
{
    using var client = new WebClient();
    client.DownloadFile(scriptUrl, scriptPath);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Failed to download install script: {ex.Message}");
    Console.ResetColor();
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey(true);
    return;
}

Console.WriteLine("Script downloaded. Starting installation...");
Console.WriteLine();

// Run the script with -InstallDotNetRuntime to auto-install dependencies
bool success = RunPowerShell(
    $"-ExecutionPolicy Bypass -NoProfile -File \"{scriptPath}\" -InstallDotNetRuntime");

// Clean up temp script
try { File.Delete(scriptPath); } catch { }

Console.WriteLine();

if (success)
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("KalOS installed successfully!");
    Console.ResetColor();
    Console.WriteLine("This window will close in 3 seconds...");
    System.Threading.Thread.Sleep(3000);
}
else
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("Installation failed. See errors above.");
    Console.ResetColor();
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey(true);
}

static bool RunPowerShell(string arguments)
{
    try
    {
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };

        proc.OutputDataReceived += (_, args) =>
        {
            if (args.Data != null)
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine(args.Data);
                Console.ResetColor();
            }
        };
        proc.ErrorDataReceived += (_, args) =>
        {
            if (args.Data != null)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine(args.Data);
                Console.ResetColor();
            }
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();

        return proc.ExitCode == 0;
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error: {ex.Message}");
        Console.ResetColor();
        return false;
    }
}
