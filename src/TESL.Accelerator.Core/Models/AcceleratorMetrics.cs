namespace TESL.Accelerator.Core.Models
{
    public class AcceleratorMetrics
    {
        public string PingText { get; set; } = "-- ms";
        public string LossText { get; set; } = "0.0%";
        public string SpeedText { get; set; } = "-- KB/s";
        public string CandidateCountText { get; set; } = "--";
    }
}
