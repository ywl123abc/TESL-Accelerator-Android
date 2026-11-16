using System.Threading;
using System.Threading.Tasks;
using TESL.Accelerator.Core.Models;

namespace TESL.Accelerator.Core.Abstractions
{
    public interface IEngineHost
    {
        bool IsRunning { get; }

        /// <summary>
        /// 启动 443 与 8443 双通道 Anycast 优选引擎 (Windows cfnat.exe 或 Android NDK 原生层)
        /// </summary>
        Task<bool> StartEnginesAsync(AcceleratorConfig config, string candidates443Path, string candidates8443Path, CancellationToken ct = default);

        /// <summary>
        /// 停止双通道 Anycast 引擎
        /// </summary>
        Task StopEnginesAsync();
    }
}
