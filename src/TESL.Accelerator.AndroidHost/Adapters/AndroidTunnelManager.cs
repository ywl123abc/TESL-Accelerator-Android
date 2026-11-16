using System;
using System.Threading;
using System.Threading.Tasks;
#if ANDROID
using Android.Net;
#endif
using TESL.Accelerator.Core.Abstractions;

namespace TESL.Accelerator.AndroidHost.Adapters
{
    /// <summary>
    /// Android 平台 TUN 虚拟网卡与透明路由接管管理器。
    /// 【所有权契约】：
    /// 1. VpnService 是 masterPfd 的唯一创建者与所有者。
    /// 2. 本管理器通过 tunFdProvider 获取系统底层原始描述符整数。
    /// 3. sing-box libbox 接收该 fd 后自行 dup 副本，原 fd 持续由宿主保持有效。
    /// 4. 停止时，先关闭 libbox (释放副本)，严禁在此提前关闭 masterPfd；
    ///    masterPfd 必须在 cfnat 双实例完全销毁、回调全部返回后的第 7 步由宿主关闭。
    /// </summary>
    public class AndroidTunnelManager : ITunnelManager
    {
#if ANDROID
        private readonly VpnService? _vpnService;
        private LibboxTunnelService? _libboxTunnelService;
#endif
        private readonly Func<int> _tunFdProvider;
        private readonly Action? _tunCloseAction;
        private readonly IAcceleratorLogger _logger;
        private readonly object _lock = new();

        public bool IsRunning { get; private set; }
        public string? ActiveConfigJson { get; private set; }

        public AndroidTunnelManager(Func<int> tunFdProvider, IAcceleratorLogger logger, Action? tunCloseAction = null)
        {
            _tunFdProvider = tunFdProvider ?? throw new ArgumentNullException(nameof(tunFdProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _tunCloseAction = tunCloseAction;
        }

        public AndroidTunnelManager(Func<int> tunFdProvider, Action tunCloseAction, IAcceleratorLogger logger)
            : this(tunFdProvider, logger, tunCloseAction)
        {
        }

#if ANDROID
        public AndroidTunnelManager(VpnService? vpnService, Func<int> tunFdProvider, Action tunCloseAction, IAcceleratorLogger logger)
            : this(tunFdProvider, logger, tunCloseAction)
        {
            _vpnService = vpnService;
        }

        public AndroidTunnelManager(VpnService? vpnService, Func<int> tunFdProvider, IAcceleratorLogger logger, Action? tunCloseAction = null)
            : this(tunFdProvider, logger, tunCloseAction)
        {
            _vpnService = vpnService;
        }
#endif

        public Task<bool> StartTunnelAsync(string singBoxConfigJson, CancellationToken ct = default)
        {
            lock (_lock)
            {
                if (IsRunning) return Task.FromResult(true);

                _logger.Log("[AndroidTunnelManager] 正在启动 TUN 虚拟网卡路由接管...");

                // 1. 获取系统建立的原始描述符
                int masterFd = _tunFdProvider();
                if (masterFd < 0)
                {
                    _logger.Log($"[AndroidTunnelManager] 获取 masterPfd 失败 (fd={masterFd})，VPN 权限或 Builder 未就绪。", LogLevel.Error);
                    return Task.FromResult(false);
                }

                _logger.Log($"[AndroidTunnelManager] 成功取得系统 TUN 描述符: masterFd={masterFd} (宿主保留唯一所有权)");

                ActiveConfigJson = singBoxConfigJson;

                // 2. 若提供 VpnService，激活真实 sing-box v1.11.10 libbox 引擎
#if ANDROID
                if (_vpnService != null)
                {
                    _libboxTunnelService ??= new LibboxTunnelService(_vpnService, _tunFdProvider, _logger);
                    bool ok = _libboxTunnelService.Start(singBoxConfigJson);
                    if (!ok)
                    {
                        _logger.Log("[AndroidTunnelManager] LibboxTunnelService 启动失败。", LogLevel.Error);
                        return Task.FromResult(false);
                    }
                }
                else
                {
                    _logger.Log("[AndroidTunnelManager] 未配置 VpnService 宿主实例，跳过 libbox 运行时装载 (纯桩测试模式)。");
                }
#else
                _logger.Log("[AndroidTunnelManager] 非 Android 运行时环境，跳过 libbox 装载 (纯桩测试模式)。");
#endif

                IsRunning = true;
                _logger.Log("[AndroidTunnelManager] TUN 虚拟网卡与 sing-box 路由接管已成功启动。");
                return Task.FromResult(true);
            }
        }

        public Task StopTunnelAsync()
        {
            lock (_lock)
            {
                if (!IsRunning) return Task.CompletedTask;

                _logger.Log("[AndroidTunnelManager] 正在停止 sing-box TUN 路由接管 (步骤 2: 等待透明分流退出)...");

                // 步骤 2: 停止 libbox 引擎并等待退出 (触发其内部 dup 副本关闭)
#if ANDROID
                if (_libboxTunnelService != null)
                {
                    _libboxTunnelService.Stop();
                }
#endif

                ActiveConfigJson = null;
                IsRunning = false;
                _logger.Log("[AndroidTunnelManager] sing-box TUN 路由接管已停止。");
                return Task.CompletedTask;
            }
        }

        /// <summary>
        /// 宿主统一在第 7 步 (cfnat 已完全终止并销毁) 关闭系统 masterPfd。
        /// </summary>
        public void CloseMasterFd()
        {
            try
            {
                _tunCloseAction?.Invoke();
                _logger.Log("[AndroidTunnelManager] 系统 masterPfd 已由宿主安全 Close。");
            }
            catch (Exception ex)
            {
                _logger.Log($"[AndroidTunnelManager] 释放 masterPfd 异常: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}
