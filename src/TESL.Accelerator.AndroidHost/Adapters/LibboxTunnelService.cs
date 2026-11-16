using System;
using Android.Net;
using Android.Runtime;
using IO.Nekohasekai.Libbox;
using TESL.Accelerator.Core.Abstractions;

namespace TESL.Accelerator.AndroidHost.Adapters
{
    /// <summary>
    /// sing-box v1.11.10 libbox 运行时隧道服务。
    /// 负责与 Java LibboxPlatformAdapter 以及底层 libbox.so (Go core) 交互。
    /// 
    /// 核心生命周期规范：
    /// 1. Start: 验证 masterFd -> 实例化 LibboxPlatformAdapter -> Libbox.newService -> BoxService.Start()。
    /// 2. TUN fd 所有权：宿主保持 masterPfd 独占所有权，sing-box 内部自行 dup 副本。
    /// 3. Stop: BoxService.Close() -> 等待 Go core 完全退出 -> 释放 BoxService 句柄。
    /// 4. 严禁在 Stop 中关闭 masterPfd，masterPfd 必须在停机统一规范的第 7 步关闭。
    /// </summary>
    public class LibboxTunnelService : IDisposable
    {
        private readonly VpnService _vpnService;
        private readonly Func<int> _tunFdProvider;
        private readonly IAcceleratorLogger _logger;
        private Java.Lang.Object? _javaAdapter;
        private BoxService? _boxService;
        private readonly object _lock = new();

        public bool IsRunning { get; private set; }

        public LibboxTunnelService(VpnService vpnService, Func<int> tunFdProvider, IAcceleratorLogger logger)
        {
            _vpnService = vpnService ?? throw new ArgumentNullException(nameof(vpnService));
            _tunFdProvider = tunFdProvider ?? throw new ArgumentNullException(nameof(tunFdProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool Start(string configJson)
        {
            lock (_lock)
            {
                if (IsRunning) return true;

                _logger.Log("[LibboxTunnelService] 正在启动 sing-box v1.11.10 原生隧道引擎...");

                int masterFd = _tunFdProvider();
                if (masterFd < 0)
                {
                    _logger.Log($"[LibboxTunnelService] masterFd 无效 (fd={masterFd})，无法启动 libbox。", LogLevel.Error);
                    return false;
                }

                try
                {
                    // 1. 确保加载 libbox 原生库
                    Java.Lang.JavaSystem.LoadLibrary("box");
                    _logger.Log("[LibboxTunnelService] libbox.so 原生动态库加载成功。");

                    // 2. 实例化 Java LibboxPlatformAdapter
                    var adapterClass = Java.Lang.Class.ForName("com.tesl.accelerator.LibboxPlatformAdapter")
                        ?? throw new InvalidOperationException("未找到 Java 类 com.tesl.accelerator.LibboxPlatformAdapter");
                    var ctor = adapterClass.GetConstructor([Java.Lang.Class.FromType(typeof(VpnService))])
                        ?? throw new InvalidOperationException("未找到 LibboxPlatformAdapter(VpnService) 构造函数");
                    _javaAdapter = (Java.Lang.Object)ctor.NewInstance(_vpnService);

                    // 3. 设置系统 masterFd
                    var setTunFdMethod = adapterClass.GetMethod("setTunFd", [Java.Lang.Integer.Type])
                        ?? throw new InvalidOperationException("未找到 setTunFd(int) 方法");
                    setTunFdMethod.Invoke(_javaAdapter, [Java.Lang.Integer.ValueOf(masterFd)!]);

                    // 4. 调用静态工厂 createService -> Libbox.newService
                    var createServiceMethod = adapterClass.GetMethod("createService", [
                        Java.Lang.Class.FromType(typeof(Java.Lang.String)),
                        adapterClass
                    ]) ?? throw new InvalidOperationException("未找到 createService 静态工厂方法");

                    _logger.Log($"[LibboxTunnelService] 正在调用 Libbox.newService 创建 BoxService 实例 (config 长度={configJson.Length})...");
                    _logger.Log($"[LibboxTunnelService] Config 内容:\n{configJson}");
                    var serviceObj = createServiceMethod.Invoke(null, [
                        new Java.Lang.String(configJson),
                        _javaAdapter
                    ]);

                    if (serviceObj is BoxService typedBoxService)
                    {
                        _boxService = typedBoxService;
                    }
                    else if (serviceObj != null)
                    {
                        _boxService = Java.Lang.Object.GetObject<BoxService>(serviceObj.Handle, JniHandleOwnership.DoNotTransfer);
                    }
                    else
                    {
                        throw new InvalidOperationException("Libbox.newService 返回了 null。");
                    }

                    _logger.Log("[LibboxTunnelService] BoxService 实例创建成功，正在调用 Start()...");
                    _boxService?.Start();
                    _logger.Log("[LibboxTunnelService] sing-box v1.11.10 BoxService.Start() 已成功启动，TUN 透明路由接管中。");

                    IsRunning = true;
                    return true;
                }
                catch (Exception ex)
                {
                    string details = ex.ToString();
                    if (ex is Java.Lang.Reflect.InvocationTargetException ite && ite.TargetException != null)
                    {
                        details = $"TargetException: [{ite.TargetException.Class?.Name}] {ite.TargetException.Message}\n{ite.TargetException}";
                    }
                    _logger.Log($"[LibboxTunnelService] 启动 sing-box 原生引擎异常: {details}", LogLevel.Error);
                    Stop();
                    return false;
                }
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!IsRunning && _boxService == null) return;

                _logger.Log("[LibboxTunnelService] 正在停止 sing-box BoxService...");
                try
                {
                    if (_boxService != null)
                    {
                        _boxService.Close();
                        _boxService.Dispose();
                        _boxService = null;
                        _logger.Log("[LibboxTunnelService] sing-box BoxService.Close() 执行完成，dup 句柄已释放。");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Log($"[LibboxTunnelService] BoxService.Close() 异常: {ex.Message}", LogLevel.Warning);
                }
                finally
                {
                    if (_javaAdapter != null)
                    {
                        _javaAdapter.Dispose();
                        _javaAdapter = null;
                    }
                    IsRunning = false;
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
