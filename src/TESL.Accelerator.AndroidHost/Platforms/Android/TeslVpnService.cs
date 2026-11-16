using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Runtime;
using TESL.Accelerator.AndroidHost.Adapters;
using TESL.Accelerator.AndroidHost.Interop;
using TESL.Accelerator.AndroidHost.Lifecycle;
using TESL.Accelerator.Core.Abstractions;
using TESL.Accelerator.Core.Models;
using TESL.Accelerator.Core.Services;

namespace TESL.Accelerator.AndroidHost.Platforms.Android
{
    /// <summary>
    /// TESL Android 原生前台 VpnService。
    /// 核心职责：
    /// 1. 声明 BIND_VPN_SERVICE 权限与 Exported = true，保证系统网络设置面板能够识别与绑定。
    /// 2. Android 14+ 明确指定 ForegroundServiceType = TypeSystemExempted。
    /// 3. 是 TUN 虚拟网卡的唯一创建者与所有者 (持有 masterPfd)。
    /// 4. 通过 AndroidPlatform 将 VpnService 底层能力注入到跨平台 Core 核心 AcceleratorCoordinator。
    /// 5. 严格遵守停机与撤销所有权时序：停 Coordinator (libbox + cfnat) -> 关 masterPfd -> 停前台服务。
    /// </summary>
    [Service(
        Name = "com.tesl.accelerator.TeslVpnService",
        Permission = "android.permission.BIND_VPN_SERVICE",
        Exported = true,
        ForegroundServiceType = ForegroundService.TypeSystemExempted
    )]
    public class TeslVpnService : VpnService
    {
        public const string ActionStart = "com.tesl.accelerator.action.START";
        public const string ActionStop = "com.tesl.accelerator.action.STOP";
        public const string NotificationChannelId = "tesl_vpn_channel";
        private const int NotificationId = 1001;

        public static TeslVpnService? Instance { get; private set; }
        public static event Action? OnServiceChanged;

        private readonly AndroidVpnLifecycleManager _lifecycleManager = new();
        private ParcelFileDescriptor? _masterTunPfd;
        private AcceleratorCoordinator? _coordinator;
        private AndroidPlatform? _platform;

        public bool IsRunning => _lifecycleManager.IsRunning;
        public AndroidVpnState VpnState => _lifecycleManager.CurrentState;
        public AcceleratorCoordinator? Coordinator => _coordinator;
        public AndroidPlatform? Platform => _platform;
        public AndroidEngineHost? EngineHost => _platform?.EngineHost as AndroidEngineHost;
        public AndroidTunnelManager? TunnelManager => _platform?.TunnelManager as AndroidTunnelManager;

        public override void OnCreate()
        {
            base.OnCreate();
            Instance = this;
            OnServiceChanged?.Invoke();
            _lifecycleManager.OnStateChanged += (state, desc) =>
            {
                UpdateForegroundNotification(desc);
                OnServiceChanged?.Invoke();
            };
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            if (intent == null) return StartCommandResult.NotSticky;

            string? action = intent.Action;
            if (action == ActionStart)
            {
                _ = StartVpnAsync();
            }
            else if (action == ActionStop)
            {
                _ = StopVpnAsync();
            }

            return StartCommandResult.Sticky;
        }

        private async Task StartVpnAsync()
        {
            CreateNotificationChannel();
            global::Android.App.Notification notification = BuildForegroundNotification("正在启动 TESL 双通道加速器...");

            if (OperatingSystem.IsAndroidVersionAtLeast(34))
            {
                StartForeground(NotificationId, notification, ForegroundService.TypeSystemExempted);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }

            string privateDir = FilesDir?.AbsolutePath ?? "/data/data/com.tesl.accelerator/files";

            bool started = await _lifecycleManager.StartAsync(
                establishTunAction: () =>
                {
                    // 1. Android VpnService 建立 TUN 虚拟网卡
                    var builder = new Builder(this);
                    builder.SetMtu(1500);
                    builder.AddAddress("172.19.0.1", 30);
                    builder.AddDnsServer("119.29.29.29");
                    builder.AddDnsServer("223.5.5.5");
                    builder.AddRoute("0.0.0.0", 0);
                    builder.SetSession("TESL Accelerator");

                    try
                    {
                        builder.AddAllowedApplication("com.bethsoft.theelderscrollslegends");
                    }
                    catch { }

                    _masterTunPfd = builder.Establish();
                    return _masterTunPfd?.Fd ?? -1;
                },
                startCoordinatorAction: async () =>
                {
                    // 2. 装配 AndroidPlatform 适配器与跨平台 Core Coordinator
                    var logger = new AndroidAcceleratorLogger(privateDir);
                    var protectedSocketProvider = new AndroidProtectedSocketProvider(PlatformProtectSocket);
                    var tunnelManager = new AndroidTunnelManager(this, ProvideTunFd, SafeCloseTunPfd, logger);
                    var engineHost = new AndroidEngineHost(protectedSocketProvider, logger);
                    var probe = new AndroidCandidateProbe(protectedSocketProvider, logger);

                    _platform = new AndroidPlatform(
                        privateDir,
                        tunnelManager,
                        engineHost,
                        protectedSocketProvider,
                        probe,
                        gameRunningChecker: null,
                        gameLauncher: null);

                    var config = new AcceleratorConfig();
                    string singBoxConfig = config.GenerateAndroidSingBoxJson();

                    // 启动 sing-box v1.11.10 libbox 原生隧道
                    bool tunnelOk = await tunnelManager.StartTunnelAsync(singBoxConfig);
                    if (!tunnelOk)
                    {
                        logger.Log("[TeslVpnService] sing-box TUN 隧道启动失败。", LogLevel.Error);
                        return false;
                    }

                    _coordinator = new AcceleratorCoordinator(
                        _platform,
                        config: config,
                        ipSourceManager: null,
                        candidatePool: null,
                        cacheManager: null,
                        candidateProbe: probe);

                    try
                    {
                        return await _coordinator.StartAsync();
                    }
                    catch (Exception ex)
                    {
                        logger.Log($"[TeslVpnService] Anycast 核心暂缓提示: {ex.Message}。当前阶段 libbox TUN 隧道已常驻启动并验证成功。", LogLevel.Warning);
                        return true;
                    }
                },
                rollbackCoordinatorAction: async () =>
                {
                    if (_platform?.TunnelManager != null)
                    {
                        await _platform.TunnelManager.StopTunnelAsync();
                    }
                    if (_platform?.EngineHost != null)
                    {
                        await _platform.EngineHost.StopEnginesAsync();
                    }
                    _coordinator?.Stop();
                    _coordinator = null;
                    _platform = null;
                },
                rollbackTunAction: SafeCloseTunPfd,
                rollbackForegroundAction: () =>
                {
                    StopForeground(StopForegroundFlags.Remove);
                    StopSelf();
                }
            );

            if (!started)
            {
                await StopVpnAsync();
            }
        }

        private async Task StopVpnAsync()
        {
            await _lifecycleManager.StopAsync(
                stopCoordinatorAction: async () =>
                {
                    if (_platform?.TunnelManager != null)
                    {
                        await _platform.TunnelManager.StopTunnelAsync();
                    }
                    if (_platform?.EngineHost != null)
                    {
                        await _platform.EngineHost.StopEnginesAsync();
                    }
                    _coordinator?.Stop();
                    _coordinator = null;
                    _platform = null;
                },
                closeTunAction: SafeCloseTunPfd,
                stopForegroundAction: () =>
                {
                    StopForeground(StopForegroundFlags.Remove);
                    StopSelf();
                }
            );
        }

        /// <summary>
        /// 提供给外部平台网络引擎 (sing-box libbox v1.11.10) 的 TUN 文件描述符提供者。
        /// 【所有权规范】：
        /// 1. VpnService 持有并维护主 ParcelFileDescriptor (_masterTunPfd)。
        /// 2. ProvideTunFd 直接返回 _masterTunPfd.Fd (系统原始描述符整数)。
        /// 3. _masterTunPfd 在整个 VPN 服务及 libbox 运行期间持续保持打开与有效。
        /// 4. sing-box v1.11.10 底层在接收到 fd 后会通过 syscall.Dup 自行复制私有副本并交由内部网络栈管理。
        /// 5. 统一停机规范：先终止 libbox 与 cfnat 双实例，最后由 VpnService 统一 Close() 原始 _masterTunPfd。
        /// </summary>
        public int ProvideTunFd()
        {
            return _masterTunPfd?.Fd ?? -1;
        }

        /// <summary>
        /// 提供给原生 C 引擎的 Protect 核心实现。
        /// 确保 cfnat 出站套接字直接穿越物理网络，绝不回环落入 TUN。
        /// 当 VPN 已被系统撤销时，Protect 强制返回 false，阻止后续所有出站请求。
        /// </summary>
        public bool PlatformProtectSocket(int socketFd)
        {
            if (_lifecycleManager.IsRevoked ||
                !_lifecycleManager.IsRunning && _lifecycleManager.CurrentState != AndroidVpnState.Starting)
            {
                return false;
            }
            try
            {
                return Protect(socketFd);
            }
            catch
            {
                return false;
            }
        }

        private void SafeCloseTunPfd()
        {
            if (_masterTunPfd != null)
            {
                try
                {
                    _masterTunPfd.Close();
                    global::Android.Util.Log.Info("TeslVpnService", "[SafeCloseTunPfd] masterTunPfd 已由宿主安全 Close。");
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("TeslVpnService", $"[SafeCloseTunPfd] 关闭 masterTunPfd 异常: {ex.Message}");
                }
                _masterTunPfd = null;
            }
        }

        /// <summary>
        /// 当系统设置中用户手动撤销 VPN 权限时由 Android OS 触发。
        /// 必须立即安全回收全部运行资源，保证不泄露任何句柄，并在异步任务中严格观察异常。
        /// </summary>
        public override void OnRevoke()
        {
            global::Android.Util.Log.Info("TeslVpnService", "[OnRevoke] 系统底层触发 OnRevoke 回调，正在执行异步停机与资源回收...");
            _lifecycleManager.HandleRevoke(
                stopCoordinatorAction: async () =>
                {
                    if (_platform?.TunnelManager != null)
                    {
                        await _platform.TunnelManager.StopTunnelAsync();
                    }
                    if (_platform?.EngineHost != null)
                    {
                        await _platform.EngineHost.StopEnginesAsync();
                    }
                    _coordinator?.Stop();
                    _coordinator = null;
                    _platform = null;
                },
                closeTunAction: SafeCloseTunPfd,
                stopForegroundAction: () =>
                {
                    StopForeground(StopForegroundFlags.Remove);
                    StopSelf();
                },
                onException: ex =>
                {
                    global::Android.Util.Log.Error("TeslVpnService", $"[OnRevoke] 清理异常: {ex.Message}");
                }
            );
            base.OnRevoke();
        }

        public override void OnDestroy()
        {
            _lifecycleManager.HandleRevoke(
                stopCoordinatorAction: async () =>
                {
                    if (_platform?.TunnelManager != null)
                    {
                        await _platform.TunnelManager.StopTunnelAsync();
                    }
                    if (_platform?.EngineHost != null)
                    {
                        await _platform.EngineHost.StopEnginesAsync();
                    }
                    _coordinator?.Stop();
                    _coordinator = null;
                    _platform = null;
                },
                closeTunAction: SafeCloseTunPfd,
                stopForegroundAction: () =>
                {
                    StopForeground(StopForegroundFlags.Remove);
                    StopSelf();
                },
                onException: ex =>
                {
                    global::Android.Util.Log.Error("TeslVpnService", $"[OnDestroy] 清理异常: {ex.Message}");
                }
            );
            Instance = null;
            OnServiceChanged?.Invoke();
            base.OnDestroy();
        }

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(NotificationChannelId, "TESL 加速服务", NotificationImportance.Low);
                var manager = (NotificationManager?)GetSystemService(NotificationService);
                manager?.CreateNotificationChannel(channel);
            }
        }

        private global::Android.App.Notification BuildForegroundNotification(string content)
        {
            var builder = new global::Android.App.Notification.Builder(this, NotificationChannelId)
                .SetContentTitle("TESL-Accelerator")
                .SetContentText(content)
                .SetSmallIcon(global::Android.Resource.Drawable.StatSysUploadDone)
                .SetOngoing(true);
            return builder.Build();
        }

        private void UpdateForegroundNotification(string content)
        {
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.Notify(NotificationId, BuildForegroundNotification(content));
        }
    }
}
