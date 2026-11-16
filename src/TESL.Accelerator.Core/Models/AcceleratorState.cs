using System;

namespace TESL.Accelerator.Core.Models
{
    public enum EngineState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Faulted
    }

    public class AcceleratorState
    {
        public bool IsRunning { get; set; } = false;
        public bool IsBusy { get; set; } = false;
        public bool IsGameRunning { get; set; } = false;
        public string Title { get; set; } = "等待启动";
        public string Subtitle { get; set; } = "就绪";
        public EngineState State { get; set; } = EngineState.Stopped;
        public DateTime LastStateChangeUtc { get; set; } = DateTime.UtcNow;
    }
}
