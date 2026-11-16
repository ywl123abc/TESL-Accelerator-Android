using System;

namespace TESL.Accelerator.Core.Models
{
    public class AcceleratorStatus
    {
        public string Title { get; set; } = "就绪";
        public string Subtitle { get; set; } = "等待启动";
        public bool IsAccelerating { get; set; } = false;
        public bool IsBusy { get; set; } = false;
        public EngineState State { get; set; } = EngineState.Stopped;
        public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

        public AcceleratorStatus()
        {
        }

        public AcceleratorStatus(string title, string subtitle, bool isAccelerating, bool isBusy, EngineState state = EngineState.Stopped)
        {
            Title = title;
            Subtitle = subtitle;
            IsAccelerating = isAccelerating;
            IsBusy = isBusy;
            State = state;
            TimestampUtc = DateTime.UtcNow;
        }

        public override string ToString() => $"[{State}] {Title} - {Subtitle} (Accelerating: {IsAccelerating}, Busy: {IsBusy})";
    }
}
