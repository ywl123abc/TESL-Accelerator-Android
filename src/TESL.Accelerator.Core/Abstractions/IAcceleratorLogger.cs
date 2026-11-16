using System;

namespace TESL.Accelerator.Core.Abstractions
{
    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error
    }

    public interface IAcceleratorLogger
    {
        void Log(string message, LogLevel level = LogLevel.Info);
        event Action<string>? OnLog;
    }
}
