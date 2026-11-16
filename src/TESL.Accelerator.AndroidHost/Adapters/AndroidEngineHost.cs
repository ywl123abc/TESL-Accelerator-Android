using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TESL.Accelerator.AndroidHost.Interop;
using TESL.Accelerator.Core.Abstractions;
using TESL.Accelerator.Core.Models;

namespace TESL.Accelerator.AndroidHost.Adapters
{
    /// <summary>
    /// Android 平台 cfnat 双通道原生引擎宿主控制器。
    /// 统一管理 443 与 8443 两个独立的 cfnat 实例，负责参数映射、套接字保护挂钩、就绪轮询及异常回滚。
    /// 【原生状态】: libcfnat.so 处于待 NDK 编译状态。
    /// </summary>
    public class AndroidEngineHost : IEngineHost
    {
        private readonly IProtectedSocketProvider _socketProvider;
        private readonly IAcceleratorLogger _logger;

        private CfnatInstanceBridge? _instance443;
        private CfnatInstanceBridge? _instance8443;
        private readonly object _lock = new();

        public bool IsRunning { get; private set; }

        public AndroidEngineHost(IProtectedSocketProvider socketProvider, IAcceleratorLogger logger)
        {
            _socketProvider = socketProvider ?? throw new ArgumentNullException(nameof(socketProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> StartEnginesAsync(AcceleratorConfig config, string cand443Path, string cand8443Path, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (IsRunning) return true;
            }

            _logger.Log("[AndroidEngineHost] 正在配置并启动 443 / 8443 双通道 cfnat 引擎...");

            try
            {
                // 1. 初始化 443 通道引擎实例
                string listen443 = $"127.0.0.1:{config.Channel443.LocalPort}";
                string cache443 = Path.Combine(Path.GetDirectoryName(cand443Path) ?? "", "direct-cache-v4.txt");

                _instance443 = new CfnatInstanceBridge(sockFd => _socketProvider.ProtectSocket(sockFd), _logger);
                bool ok443 = _instance443.Create(
                    listen443,
                    (ushort)config.Channel443.RemotePort,
                    80,
                    config.Channel443.SniHost,
                    cache443,
                    cand443Path);

                if (!ok443)
                {
                    _logger.Log("[AndroidEngineHost] 443 通道 cfnat 实例创建失败，中止启动。", LogLevel.Error);
                    await StopEnginesAsync();
                    return false;
                }

                if (!_instance443.Start())
                {
                    _logger.Log("[AndroidEngineHost] 443 通道 cfnat 启动失败。", LogLevel.Error);
                    await StopEnginesAsync();
                    return false;
                }

                // 2. 初始化 8443 通道引擎实例
                string listen8443 = $"127.0.0.1:{config.Channel8443.LocalPort}";
                string cache8443 = Path.Combine(Path.GetDirectoryName(cand8443Path) ?? "", "direct-cache-v4.txt");

                _instance8443 = new CfnatInstanceBridge(sockFd => _socketProvider.ProtectSocket(sockFd), _logger);
                bool ok8443 = _instance8443.Create(
                    listen8443,
                    (ushort)config.Channel8443.RemotePort,
                    80,
                    config.Channel8443.SniHost,
                    cache8443,
                    cand8443Path);

                if (!ok8443)
                {
                    _logger.Log("[AndroidEngineHost] 8443 通道 cfnat 实例创建失败，执行回滚。", LogLevel.Error);
                    await StopEnginesAsync();
                    return false;
                }

                if (!_instance8443.Start())
                {
                    _logger.Log("[AndroidEngineHost] 8443 通道 cfnat 启动失败，执行回滚。", LogLevel.Error);
                    await StopEnginesAsync();
                    return false;
                }

                // 3. 轮询等待双实例就绪 (非阻塞等待，最长 5 秒)
                bool ready443 = await Task.Run(() => _instance443.WaitReady(5000), ct);
                bool ready8443 = await Task.Run(() => _instance8443.WaitReady(5000), ct);

                if (!ready443 || !ready8443)
                {
                    _logger.Log($"[AndroidEngineHost] 双通道实例就绪检查未全部通过 (443={ready443}, 8443={ready8443})，执行回滚。", LogLevel.Warning);
                    await StopEnginesAsync();
                    return false;
                }

                lock (_lock)
                {
                    IsRunning = true;
                }
                _logger.Log("[AndroidEngineHost] 双通道 cfnat 引擎实例均已启动并就绪。");
                return true;
            }
            catch (DllNotFoundException ex)
            {
                _logger.Log($"[AndroidEngineHost] 【原生库待编译】未检测到 libcfnat.so 动态链接库: {ex.Message}。此状态为当前 NDK 暂缓阶段预期行为。保持 sing-box TUN 路由接管常驻运行。", LogLevel.Warning);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Log($"[AndroidEngineHost] 启动双通道引擎发生异常: {ex.Message}，已执行资源回滚。", LogLevel.Error);
                await StopEnginesAsync();
                return false;
            }
        }

        public CfnatStatus? GetStatus443() => _instance443?.GetStatus();
        public CfnatStatus? GetStatus8443() => _instance8443?.GetStatus();

        public void TriggerRescan443()
        {
            _logger.Log("[AndroidEngineHost] 收到 UI 443 通道独立重新测速请求。");
            _instance443?.TriggerRescan();
        }

        public void TriggerRescan8443()
        {
            _logger.Log("[AndroidEngineHost] 收到 UI 8443 通道独立重新测速请求。");
            _instance8443?.TriggerRescan();
        }

        public void StopChannel443()
        {
            _logger.Log("[AndroidEngineHost] 收到 UI 443 通道独立停止请求。");
            _instance443?.Stop();
        }

        public void StopChannel8443()
        {
            _logger.Log("[AndroidEngineHost] 收到 UI 8443 通道独立停止请求。");
            _instance8443?.Stop();
        }

        public bool StartChannel443()
        {
            _logger.Log("[AndroidEngineHost] 收到 UI 443 通道独立启动请求。");
            return _instance443?.Start() ?? false;
        }

        public bool StartChannel8443()
        {
            _logger.Log("[AndroidEngineHost] 收到 UI 8443 通道独立启动请求。");
            return _instance8443?.Start() ?? false;
        }

        public Task StopEnginesAsync()
        {
            lock (_lock)
            {
                IsRunning = false;
            }

            bool hadNative = false;

            try
            {
                if (_instance443 != null)
                {
                    if (_instance443.IsActive) hadNative = true;
                    _instance443.Stop();
                    _instance443.Dispose();
                    _instance443 = null;
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"[AndroidEngineHost] 443 引擎实例停止异常: {ex.Message}", LogLevel.Warning);
            }

            try
            {
                if (_instance8443 != null)
                {
                    if (_instance8443.IsActive) hadNative = true;
                    _instance8443.Stop();
                    _instance8443.Dispose();
                    _instance8443 = null;
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"[AndroidEngineHost] 8443 引擎实例停止异常: {ex.Message}", LogLevel.Warning);
            }

            if (hadNative)
            {
                _logger.Log("[AndroidEngineHost] 双通道 cfnat 引擎已停止并销毁原生资源。");
            }
            else
            {
                _logger.Log("[AndroidEngineHost] 托管层资源已清理回滚（Native 实例未加载，无需原生销毁）。");
            }

            return Task.CompletedTask;
        }
    }
}
