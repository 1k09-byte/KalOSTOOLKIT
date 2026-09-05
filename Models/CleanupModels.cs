using System;

namespace KaliteKit.Models
{
    /// <summary>A single session log entry the UI binds to (see <see cref="Services.LoggingService"/>).</summary>
    public class CleanupLog
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Message { get; set; } = string.Empty;
        public string Level { get; set; } = "Info";
    }
}