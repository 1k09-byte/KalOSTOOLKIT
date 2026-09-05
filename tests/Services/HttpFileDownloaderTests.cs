using KaliteKit.Services;

namespace KaliteKit.Tests.Services;

/// <summary>
/// Pure-math tests for <see cref="HttpFileDownloader"/>: the percent computation
/// used to drive the wizard's progress bar. The actual download path hits the
/// network and is exercised manually; <see cref="ComputePercent"/> is the only
/// branch with observable, testable behavior, so it is the contract we pin.
/// </summary>
public class HttpFileDownloaderTests
{
    [Theory]
    [InlineData(0, 100, 0)]            // nothing read yet
    [InlineData(50, 100, 50)]         // halfway
    [InlineData(100, 100, 100)]        // complete
    [InlineData(1000, 100, 100)]       // never exceeds 100 even if total is understated
    public void ComputePercent_ClampsToZeroToHundred(long read, long total, int expected)
    {
        Assert.Equal(expected, HttpFileDownloader.ComputePercent(read, total));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    [InlineData(10, 0)]
    [InlineData(10, -5)]
    public void ComputePercent_ReturnsZeroForNonPositiveTotal(long read, long total)
    {
        Assert.Equal(0, HttpFileDownloader.ComputePercent(read, total));
    }

    [Fact]
    public void ComputePercent_RoundsDownForPartialProgress()
    {
        // 1 byte of 3 → 33% (integer truncation, not 34).
        Assert.Equal(33, HttpFileDownloader.ComputePercent(1, 3));
    }

    [Fact]
    public void DefaultMinBytes_RejectsHtmlErrorPagesAsPackages()
    {
        // The contract: a payload under 1MB is treated as corrupt. Pin the
        // threshold so a regression that drops it (e.g. to 1KB) is caught.
        Assert.Equal(1_000_000, HttpFileDownloader.DefaultMinBytes);
    }
}
