namespace TESL.Accelerator.Core.Models
{
    public class ChannelInfo
    {
        public int Port { get; set; }
        public string Ip { get; set; } = "探测中...";
        public string Location { get; set; } = "Anycast 边缘";
        public int LatencyMs { get; set; } = 0;
        public bool IsReady { get; set; } = false;
        public string StatusText { get; set; } = "等待启动";
    }

    public class CandidateInfo
    {
        public string Ip { get; set; } = string.Empty;
        public int Port { get; set; } = 443;
        public string Colo { get; set; } = "SIN";
        public int LatencyMs { get; set; } = 999;
        public string Source { get; set; } = string.Empty;
        public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
