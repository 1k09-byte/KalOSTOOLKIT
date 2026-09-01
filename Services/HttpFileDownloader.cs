using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace KalOS.Services
{
    /// <summary>
    /// Streams a file (zip payloads, runtime installers, scripts) to disk with
    /// progress, retries, and an atomic move. Companion to
    /// <see cref="DriverDownloadService"/> — that one hard-validates MZ
    /// executable headers, which is exactly wrong for zip packages, so this
    /// downloader only enforces a minimum size and lets the caller validate
    /// content (e.g. <see cref="ZipPackageInstaller"/> checking the zip open).
    /// </summary>
    public class HttpFileDownloader
    {
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KalOS-Setup/1.0 (package-download)");
            return client;
        }

        /// <summary>A downloaded file below this size is treated as an error page, not a package.</summary>
        public const long DefaultMinBytes = 1_000_000;

        /// <summary>
        /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/>,
        /// reporting 0–100 percent. Retries three times. The write is atomic:
        /// data lands in a <c>.tmp</c> sibling that is only moved to the final
        /// path after the minimum-size check passes.
        /// </summary>
        /// <param name="minBytes">Reject smaller payloads as corrupt/error pages.</param>
        public async Task<string> DownloadAsync(
            string url,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default,
            long minBytes = DefaultMinBytes)
        {
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmpPath = destinationPath + ".tmp";

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    long totalBytes = response.Content.Headers.ContentLength ?? 0;
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int lastReportedPct = -1;
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                        totalRead += bytesRead;

                        if (totalBytes > 0)
                        {
                            int pct = ComputePercent(totalRead, totalBytes);
                            if (pct != lastReportedPct)
                            {
                                progress?.Report(pct);
                                lastReportedPct = pct;
                            }
                        }
                        else
                        {
                            // Unknown length: pulse occasionally so the UI stays alive.
                            if (totalRead % (81920 * 20) == 0)
                            {
                                progress?.Report(0);
                            }
                        }
                    }

                    await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await fileStream.DisposeAsync().ConfigureAwait(false);

                    if (totalRead < minBytes)
                    {
                        TryDelete(tmpPath);
                        throw new InvalidDataException(
                            $"Downloaded payload is only {totalRead:N0} bytes (minimum {minBytes:N0}) — the source likely returned an error page or an expired link.");
                    }

                    File.Move(tmpPath, destinationPath, overwrite: true);
                    return destinationPath;
                }
                catch (OperationCanceledException)
                {
                    TryDelete(tmpPath);
                    throw;
                }
                catch (Exception)
                {
                    TryDelete(tmpPath);
                    if (attempt == 3) throw;
                    await Task.Delay(2000 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new IOException("Download failed after 3 attempts.");
        }

        /// <summary>Percent math in one place so it is unit-testable. Extracted for tests.</summary>
        internal static int ComputePercent(long read, long total)
        {
            if (total <= 0) return 0;
            if (read <= 0) return 0;
            return (int)Math.Min(100, read * 100.0 / total);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
