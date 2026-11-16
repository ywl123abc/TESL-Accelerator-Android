using System;
using System.IO;
using System.Text;
using TESL.Accelerator.Core.Abstractions;

namespace TESL.Accelerator.AndroidHost.Adapters
{
    /// <summary>
    /// Android 平台日志适配器。
    /// 输出至 Android Logcat、内部应用私有存储日志文件及托管事件订阅者。
    /// </summary>
    public class AndroidAcceleratorLogger : IAcceleratorLogger
    {
        private readonly string? _logFilePath;
        public event Action<string>? OnLog;

        public AndroidAcceleratorLogger(string? dataDirectory = null)
        {
            if (!string.IsNullOrEmpty(dataDirectory))
            {
                _logFilePath = Path.Combine(dataDirectory, "accelerator.log");
            }
        }

        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            string formatted = $"[{DateTime.UtcNow:HH:mm:ss.fff}] [{level}] {message}";

            // Android 原生环境向 Logcat 写入
#if ANDROID
            switch (level)
            {
                case LogLevel.Debug:
                    global::Android.Util.Log.Debug("TESL_Core", message);
                    break;
                case LogLevel.Info:
                    global::Android.Util.Log.Info("TESL_Core", message);
                    break;
                case LogLevel.Warning:
                    global::Android.Util.Log.Warn("TESL_Core", message);
                    break;
                case LogLevel.Error:
                    global::Android.Util.Log.Error("TESL_Core", message);
                    break;
            }
#else
            Console.WriteLine(formatted);
#endif

            // 私有目录文件写入
            if (!string.IsNullOrEmpty(_logFilePath))
            {
                try
                {
                    string dir = Path.GetDirectoryName(_logFilePath) ?? "";
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    File.AppendAllText(_logFilePath, formatted + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }

            OnLog?.Invoke(formatted);
            Logging.AppLogBuffer.Append("Core", level.ToString().ToUpperInvariant(), message);
        }
    }
}
