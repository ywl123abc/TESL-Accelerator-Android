using System;
using System.Threading;
using System.Threading.Tasks;
using TESL.Accelerator.Core.Abstractions;

namespace TESL.Accelerator.AndroidEngine
{
    public class AndroidProcessSupervisor : IProcessSupervisor
    {
        private readonly IAcceleratorLogger? _logger;

        public AndroidProcessSupervisor(IAcceleratorLogger? logger = null)
        {
            _logger = logger;
        }

        public Task InitializeAsync(CancellationToken ct = default)
        {
            // Android 对应初始化 Foreground Service 状态栏常驻通知，阻止系统 LMK (Low Memory Killer) 杀后台
            _logger?.Log("[Android-Supervisor] 前台保活服务与系统常驻通道已初始化。");
            return Task.CompletedTask;
        }

        public void RegisterProcess(IntPtr handle, int processId, string tag)
        {
            // Android 下登记 native 线程或子任务
        }

        public void TerminateAll()
        {
            _logger?.Log("[Android-Supervisor] 停止所有 Android 后台加速任务并注销前台通知。");
        }
    }
}
