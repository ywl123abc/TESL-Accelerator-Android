using System.Threading;
using System.Threading.Tasks;

namespace TESL.Accelerator.Core.Abstractions
{
    public interface ITunnelManager
    {
        bool IsRunning { get; }

        /// <summary>
        /// 启动 TUN 虚拟接口并应用路由规则 (sing-box 独立进程组或 libbox AAR)
        /// </summary>
        Task<bool> StartTunnelAsync(string singBoxConfigJson, CancellationToken ct = default);

        /// <summary>
        /// 优雅停止 TUN 虚拟网卡并还原系统网络栈
        /// </summary>
        Task StopTunnelAsync();
    }
}
