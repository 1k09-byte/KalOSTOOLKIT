using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace KalOS.Helpers;

public static class JsonConfigHelper
{
    private static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KalOS", "Configs");

    public static async Task SaveAsync<T>(string fileName, T data)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var filePath = Path.Combine(ConfigDirectory, fileName);

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to save config '{fileName}': {ex.Message}", ex);
        }
    }

    public static async Task<T?> LoadAsync<T>(string fileName)
    {
        try
        {
            var filePath = Path.Combine(ConfigDirectory, fileName);

            if (!File.Exists(filePath))
                return default;

            var json = await File.ReadAllTextAsync(filePath);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load config '{fileName}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Synchronously loads config. Intended only for app-startup scenarios (e.g. applying the
    /// saved theme/backdrop before the first window is shown) where awaiting isn't convenient
    /// and the read is small and local. Returns default(T) if the file doesn't exist or can't
    /// be parsed, rather than throwing, since startup should never crash on a missing/corrupt
    /// settings file.
    /// </summary>
    public static T? LoadSync<T>(string fileName)
    {
        try
        {
            var filePath = Path.Combine(ConfigDirectory, fileName);

            if (!File.Exists(filePath))
                return default;

            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return default;
        }
    }
}
