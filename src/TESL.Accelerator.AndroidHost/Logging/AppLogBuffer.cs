using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace TESL.Accelerator.AndroidHost.Logging
{
    public class LogItem
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string Source { get; set; } = "Core";
        public string Level { get; set; } = "INFO";
        public string Message { get; set; } = string.Empty;

        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss}] [{Source}] [{Level}] {Message}";
        }
    }

    /// <summary>
    /// 全局有界日志缓冲区 (500~1000条)，聚合 Core、cfnat、sing-box 与 VPN 全链路日志。
    /// 线程安全且支持主线程 UI 订阅更新。
    /// </summary>
    public static class AppLogBuffer
    {
        private const int MaxEntries = 1000;
        private static readonly ConcurrentQueue<LogItem> _queue = new();
        public static event Action<LogItem>? OnLogAppended;

        public static void Append(string source, string level, string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            var item = new LogItem
            {
                Timestamp = DateTime.Now,
                Source = source,
                Level = level,
                Message = message
            };

            _queue.Enqueue(item);
            while (_queue.Count > MaxEntries && _queue.TryDequeue(out _)) { }

            try
            {
                OnLogAppended?.Invoke(item);
            }
            catch { }
        }

        public static List<LogItem> Snapshot()
        {
            return _queue.ToList();
        }

        public static void Clear()
        {
            while (_queue.TryDequeue(out _)) { }
        }
    }
}
