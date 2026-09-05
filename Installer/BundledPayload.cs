using System;
using System.IO;

namespace KaliteKit.Setup
{
    /// <summary>
    /// Locates the KaliteKit consumer payload embedded inside the standalone
    /// installer exe. <c>publish-standalone.ps1</c> embeds the consumer
    /// release zip as a managed resource (build property
    /// <c>KaliteKitPayloadZip</c>), so the one exe can install KaliteKit completely
    /// offline — no GitHub lookup, no package download, no install script.
    ///
    /// The embedded first-run wizard inside the consumer app has no payload
    /// (its host already IS the app) and keeps its existing GitHub deploy
    /// path; the pipeline branches on <see cref="SetupState.Embedded"/>.
    /// </summary>
    internal static class BundledPayload
    {
        /// <summary>Logical-name prefix of the embedded payload resource.</summary>
        public const string ResourcePrefix = "KaliteKit.Setup.Payloads.";

        /// <summary>Name of the embedded payload resource, or null when this
        /// build was produced without one (dev builds / the consumer app).</summary>
        public static string? PayloadResourceName { get; }

        static BundledPayload()
        {
            try
            {
                foreach (string name in typeof(BundledPayload).Assembly.GetManifestResourceNames())
                {
                    if (name.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        PayloadResourceName = name;
                        return;
                    }
                }
            }
            catch
            {
                // No payload — callers surface a clear message instead.
            }
        }

        /// <summary>True when this exe carries the KaliteKit consumer payload.</summary>
        public static bool HasPayload => PayloadResourceName is not null;

        /// <summary>Opens the embedded payload zip for reading; null when absent.</summary>
        public static Stream? OpenPayload()
        {
            if (PayloadResourceName is null) return null;
            try
            {
                return typeof(BundledPayload).Assembly.GetManifestResourceStream(PayloadResourceName);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Streams the embedded payload out to a temp zip and returns its
        /// path, or null when the payload is missing or could not be written.
        /// Progress (0..1) reports bytes copied; when the stream length is
        /// unknown it is never reported.
        /// </summary>
        public static string? ExtractToTemp(IProgress<double>? progress = null)
        {
            using var source = OpenPayload();
            if (source is null) return null;

            string zipPath = Path.Combine(
                Path.GetTempPath(), "KaliteKit-Setup", "KaliteKit-bundled.zip");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
                long total = source.CanSeek ? source.Length : 0;
                using (var destination = File.Create(zipPath))
                {
                    var buffer = new byte[81920];
                    long copied = 0;
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        destination.Write(buffer, 0, read);
                        copied += read;
                        if (total > 0) progress?.Report((double)copied / total);
                    }
                }
                return zipPath;
            }
            catch
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                return null;
            }
        }
    }
}
