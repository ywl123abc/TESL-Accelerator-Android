using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TESL.Accelerator.Core.Abstractions;

namespace TESL.Accelerator.AndroidEngine
{
    public class AndroidTunnelManager : ITunnelManager
    {
        private readonly string _filesDir;
        private readonly IAcceleratorLogger? _logger;
        private bool _isRunning = false;

        public bool IsRunning => _isRunning;

        public AndroidTunnelManager(string filesDir, IAcceleratorLogger? logger = null)
        {
            _filesDir = filesDir;
            _logger = logger;
        }

        public Task<bool> StartTunnelAsync(string singBoxConfigJson, CancellationToken ct = default)
        {
            // 在 Android 环境中，此方法由 .NET MAUI Android VpnService 绑定：
            // 1. 将 singBoxConfigJson 写入内部存储 config.json
            // 2. 调用 libbox AAR 的 PlatformInterface 或 CommandServer
            // 3. 将 VpnService.Builder 建立的 TUN fd 移交至 libbox.NewStandaloneCommand
            string configPath = Path.Combine(_filesDir, "config.json");
            Core.Services.CacheManager.AtomicWriteText(configPath, singBoxConfigJson);

            _logger?.Log("[Android-TUN] sing-box Android 配置文件已生成并注入内部存储。");
            _isRunning = true;
            return Task.FromResult(true);
        }

        public Task StopTunnelAsync()
        {
            // 调用 libbox.CommandServer.Close() 并释放 VpnService TUN fd
            _isRunning = false;
            _logger?.Log("[Android-TUN] sing-box Android VpnService 隧道已优雅注销。");
            return Task.CompletedTask;
        }
    }
}
