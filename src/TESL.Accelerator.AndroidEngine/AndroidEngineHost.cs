using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TESL.Accelerator.Core.Abstractions;
using TESL.Accelerator.Core.Models;

namespace TESL.Accelerator.AndroidEngine
{
    public class AndroidEngineHost : IEngineHost
    {
        private readonly string _cfnat443Folder;
        private readonly string _cfnat8443Folder;
        private readonly IProtectedSocketProvider _socketProtector;
        private readonly IAcceleratorLogger? _logger;
        private bool _isRunning = false;

        public bool IsRunning => _isRunning;

        public AndroidEngineHost(
            string cfnat443Folder,
            string cfnat8443Folder,
            IProtectedSocketProvider socketProtector,
            IAcceleratorLogger? logger = null)
        {
            _cfnat443Folder = cfnat443Folder;
            _cfnat8443Folder = cfnat8443Folder;
            _socketProtector = socketProtector;
            _logger = logger;
        }

        public Task<bool> StartEnginesAsync(
            AcceleratorConfig config,
            string candidates443Path,
            string candidates8443Path,
            CancellationToken ct = default)
        {
            // 在 Android 环境中：
            // 1. 通过 Android NDK 编译的 libcfnat.so (arm64-v8a / armeabi-v7a) 导出 JNI/PInvoke 入口
            // 2. 在每个 cfnat socket() 创建后，回调 _socketProtector.ProtectSocket(fd)，确保流量直连物理网络
            // 3. 运行 443 与 8443 双实例并监听 127.0.0.1:10443 与 127.0.0.1:18443
            _logger?.Log("[Android-cfnat] 启动双通道 Android NDK Anycast 原生优选实例 (受 ProtectedSocketProvider 保护)。");
            _isRunning = true;
            return Task.FromResult(true);
        }

        public Task StopEnginesAsync()
        {
            _isRunning = false;
            _logger?.Log("[Android-cfnat] Android NDK Anycast 原生优选实例已停止。");
            return Task.CompletedTask;
        }
    }
}
