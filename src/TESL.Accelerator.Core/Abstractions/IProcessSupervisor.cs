using System;
using System.Threading;
using System.Threading.Tasks;

namespace TESL.Accelerator.Core.Abstractions
{
    public interface IProcessSupervisor
    {
        /// <summary>
        /// 初始化平台专属外部子进程治理结构 (例如 Windows Job Object 进程树约束)。
        /// 仅用于管理独立的外部操作系统子进程（如 Windows 下运行的 sing-box.exe、cfnat.exe）。
        /// Android 平台下由 AndroidEngine / VpnService 直接在应用进程内部托管生命周期，不实现或不需要此接口。
        /// </summary>
        Task InitializeAsync(CancellationToken ct = default);

        /// <summary>
        /// 注册受纳管的子进程或运行线程
        /// </summary>
        void RegisterProcess(IntPtr handle, int processId, string tag);

        /// <summary>
        /// 执行全局优雅终结与垃圾回收
        /// </summary>
        void TerminateAll();
    }
}
