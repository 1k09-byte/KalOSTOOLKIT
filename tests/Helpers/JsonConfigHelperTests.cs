using KalOS.Helpers;
using Xunit;

namespace KalOS.Tests.Helpers;

public class JsonConfigHelperTests
{
    private class TestConfig
    {
        public string Name { get; set; } = string.Empty;
        public int Value { get; set; }
    }

    [Fact]
    public async Task SaveAndLoadAsync_RoundTrip_ReturnsSameData()
    {
        var config = new TestConfig { Name = "Test", Value = 42 };
        var fileName = $"test_{Guid.NewGuid()}.json";

        await JsonConfigHelper.SaveAsync(fileName, config);
        var loaded = await JsonConfigHelper.LoadAsync<TestConfig>(fileName);

        Assert.NotNull(loaded);
        Assert.Equal(config.Name, loaded.Name);
        Assert.Equal(config.Value, loaded.Value);

        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KalOS", "Configs", fileName);
        if (File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public async Task LoadAsync_FileDoesNotExist_ReturnsNull()
    {
        var result = await JsonConfigHelper.LoadAsync<TestConfig>("nonexistent_file.json");
        Assert.Null(result);
    }

    [Fact]
    public void LoadSync_FileDoesNotExist_ReturnsNull()
    {
        var result = JsonConfigHelper.LoadSync<TestConfig>("nonexistent_file.json");
        Assert.Null(result);
    }

    [Fact]
    public void LoadSync_InvalidJson_ReturnsNull()
    {
        var fileName = $"invalid_{Guid.NewGuid()}.json";
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KalOS", "Configs");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, "not valid json{{{");

        var result = JsonConfigHelper.LoadSync<TestConfig>(fileName);
        Assert.Null(result);

        if (File.Exists(path)) File.Delete(path);
    }
}
