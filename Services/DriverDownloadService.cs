using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KalOS.Services
{
    /// <summary>
    /// Streams a driver package to disk with progress + cancellation and a
    /// small retry loop. One static <see cref="HttpClient"/> with a long
    /// timeout — driver packages are hundreds of megabytes.
    /// </summary>
    public class DriverDownloadService
    {
        private readonly LoggingService _log;

        private static readonly HttpClient _http = CreateClient();

        static DriverDownloadService()
        {
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
            client.Timeout = TimeSpan.FromMinutes(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KalOS/1.1 (driver-download)");
            return client;
        }

        public DriverDownloadService(LoggingService log)
        {
            _log = log;
        }

        /// <summary>Raw byte progress, for callers that want speed/ETA math.</summary>
        public event Action<double, double>? ProgressChanged;

        /// <summary>
        /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/>,
        /// reporting 0–100 percent. Retries three times before giving up.
        /// The download is atomic: data streams to a <c>.tmp</c> file first and
        /// is only moved to the final path after MZ-header validation passes.
        /// </summary>
        public async Task<string> DownloadAsync(
            string url,
            string destinationPath,
            IProgress<double>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string tmpPath = destinationPath + ".tmp";

            _log.Info($"Starting download: {Path.GetFileName(destinationPath)}");

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    long totalBytes = response.Content.Headers.ContentLength ?? 0;
                    if (totalBytes == 0)
                    {
                        _log.Warn($"Download response for {url} did not include a content length; validating the payload after streaming.");
                    }
                    await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var fileStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

                    var buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;
                    int lastReportedPct = -1;
                    
                    while ((bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                        totalRead += bytesRead;
                        
                        if (totalBytes > 0)
                        {
                            int currentPct = (int)(totalRead * 100.0 / totalBytes);
                            if (currentPct != lastReportedPct)
                            {
                                progress?.Report(currentPct);
                                ProgressChanged?.Invoke(totalRead, totalBytes);
                                lastReportedPct = currentPct;
                            }
                        }
                        else
                        {
                            // If length is unknown, just report roughly occasionally
                            if (totalRead % (81920 * 10) == 0)
                            {
                                progress?.Report(0);
                                ProgressChanged?.Invoke(totalRead, totalBytes);
                            }
                        }
                    }

                    // Flush and close before validation/move
                    await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await fileStream.DisposeAsync().ConfigureAwait(false);

                    if (!LooksLikeWindowsExecutable(tmpPath))
                    {
                        try { File.Delete(tmpPath); } catch { }
                        throw new InvalidDataException(
                            $"Downloaded file is not a valid Windows executable ({totalRead:N0} bytes). " +
                            "The vendor may have returned an HTML error page or an expired download URL.");
                    }

                    // Atomic move: the real path is never left in a half-written state.
                    File.Move(tmpPath, destinationPath, overwrite: true);

                    _log.Success($"Download complete: {destinationPath} ({totalRead / 1024.0 / 1024.0:F1} MB)");
                    return destinationPath;
                }
                catch (OperationCanceledException)
                {
                    try { File.Delete(tmpPath); } catch { }
                    throw;
                }
                catch (Exception ex)
                {
                    try { File.Delete(tmpPath); } catch { }
                    _log.Warn($"Download attempt {attempt}/3 failed: {ex.Message}");
                    if (attempt == 3) throw;
                    await Task.Delay(2000 * attempt, cancellationToken).ConfigureAwait(false);
                }
            }

            throw new IOException("Download failed after 3 attempts");
        }

        private static bool LooksLikeWindowsExecutable(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length < 2) return false;

                int first = stream.ReadByte();
                int second = stream.ReadByte();
                return first == 'M' && second == 'Z';
            }
            catch
            {
                return false;
            }
        }
    }
}
