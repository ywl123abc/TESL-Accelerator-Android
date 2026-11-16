using System;

namespace TESL.Accelerator.Core.Models
{
    /// <summary>
    /// 单个节点对具体目标域名与端口的实测失败/403 记录 (精确定点记录，严禁粗暴泛化至整段 IP)
    /// </summary>
    public class NodeProbeFailureRecord
    {
        public string Ip { get; set; } = string.Empty;
        public string TargetSni { get; set; } = string.Empty;
        public int TargetPort { get; set; }
        public string Channel { get; set; } = string.Empty; // "443" 或 "8443"
        public string Result { get; set; } = string.Empty; // 例: "HTTP 403 Forbidden" / "TLS Handshake Refused"
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        public override string ToString()
        {
            return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] [{Channel}] {Ip}:{TargetPort} (SNI: {TargetSni}) -> {Result}";
        }
    }
}
