using System;
using TESL.Accelerator.Core.Abstractions;

namespace TESL.Accelerator.Core.Models
{
    public class LogEventArgs : EventArgs
    {
        public string Message { get; }
        public LogLevel Level { get; }
        public DateTime Timestamp { get; }

        public LogEventArgs(string message, LogLevel level = LogLevel.Info, DateTime? timestamp = null)
        {
            Message = message ?? string.Empty;
            Level = level;
            Timestamp = timestamp ?? DateTime.UtcNow;
        }

        public override string ToString() => $"[{Timestamp:HH:mm:ss}] [{Level}] {Message}";
    }
}
