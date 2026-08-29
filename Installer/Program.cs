using System;
using System.Diagnostics;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔══════════════════════════════════════╗");
Console.WriteLine("║        KalOS Installer               ║");
Console.WriteLine("╚══════════════════════════════════════╝");
Console.ResetColor();
Console.WriteLine();

// Step 1: Set execution policy
Console.ForegroundColor = ConsoleColor.White;
Console.WriteLine("Setting execution policy...");
Console.ResetColor();

RunPowerShell("Set-ExecutionPolicy -ExecutionPolicy Unrestricted -Scope CurrentUser -Force");
Console.WriteLine();

// Step 2: Install KalOS
Console.ForegroundColor = ConsoleColor.White;
Console.WriteLine("Installing KalOS...");
Console.ResetColor();
Console.WriteLine();

bool success = RunPowerShell(
    "irm https://raw.githubusercontent.com/1k09-byte/KalOSTOOLKIT/main/install-kalos.ps1 | iex");

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

static bool RunPowerShell(string command)
{
    try
    {
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -NoProfile -Command \"{command}\"",
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
