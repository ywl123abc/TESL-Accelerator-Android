using System;
using System.Runtime.InteropServices;
using System.Text;

namespace TESL.Accelerator.AndroidHost.Interop
{
    public enum CfnatState : int
    {
        Stopped = 0,
        Starting = 1,
        Listening = 2,
        Ready = 3,
        Stopping = 4,
        Error = 5
    }

    public enum CfnatLogLevel : int
    {
        Silent = 0,
        Error = 1,
        Warn = 2,
        Info = 3,
        Debug = 4
    }

    /// <summary>
    /// 套接字保护回调。
    /// 核心规范：
    /// 1. socketFd 严格使用 32 位整型 (int32_t / int)，绝不可使用指针或 64 位类型。
    /// 2. 返回值：1 表示保护成功，允许出站发起 connect；0 表示保护失败，C 引擎必须立即关闭 Socket 并终止连接。
    /// 3. 不允许任何托管异常越过 ABI 边界扩散至原生 C 引擎。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int CfnatProtectSocketCallback(int socketFd, IntPtr userData);

    /// <summary>
    /// 原生日志回调。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void CfnatLogCallback(CfnatLogLevel level, IntPtr msgUtf8, IntPtr userData);

    /// <summary>
    /// 映射 cfnat_api.h 中的 cfnat_config_t 结构体。
    /// 内存布局：64 位系统下严格对齐为 144 字节，字段偏移与 GCC C 编译器 100% 逐字节对齐。
    /// 所有字符串指针采用 IntPtr 显式管理，杜绝 CLR 临时封送时的生命周期与编码不确定性。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CfnatConfig
    {
        public uint AbiVersion;                 // offset 0,  size 4
        public uint StructSize;                 // offset 4,  size 4
        public IntPtr LocalListenAddr;          // offset 8,  size 8 (UTF-8 const char*)
        public ushort RemoteTlsPort;            // offset 16, size 2
        public ushort RemoteHttpPort;           // offset 18, size 2
                                                // offset 20, 4 bytes padding (64-bit alignment)
        public IntPtr ProbeSni;                 // offset 24, size 8 (UTF-8 const char*)
        public int ConnectTimeoutMs;            // offset 32, size 4
        public int TlsProbeTimeoutMs;           // offset 36, size 4
        public int HealthCheckIntervalMs;       // offset 40, size 4
        public int IpVersion;                   // offset 44, size 4
        public int RandomMode;                  // offset 48, size 4
        public int Ipnum;                       // offset 52, size 4
        public int Num;                         // offset 56, size 4
        public int Task;                        // offset 60, size 4
        public int Code;                        // offset 64, size 4
                                                // offset 68, 4 bytes padding (64-bit alignment)
        public IntPtr Colo;                     // offset 72, size 8 (UTF-8 const char*)
        public IntPtr IpSourcePath;             // offset 80, size 8 (UTF-8 const char*)
        public IntPtr CacheFilePath;            // offset 88, size 8 (UTF-8 const char*)
        public IntPtr LocationsPath;            // offset 96, size 8 (UTF-8 const char*)
        public CfnatLogLevel LogLevel;          // offset 104, size 4
                                                // offset 108, 4 bytes padding (64-bit alignment)
        public IntPtr ProtectSocketCb;          // offset 112, size 8 (cfnat_protect_socket_fn)
        public IntPtr ProtectUserData;          // offset 120, size 8 (void* context)
        public IntPtr LogCb;                    // offset 128, size 8 (cfnat_log_fn)
        public IntPtr LogUserData;              // offset 136, size 8 (void* context)
    }

    /// <summary>
    /// 映射 cfnat_api.h 中的 cfnat_status_t 结构体。
    /// 内存布局：全平台固定为 344 字节。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct CfnatStatus
    {
        public uint StructSize;                 // offset 0,  size 4
        public CfnatState State;                // offset 4,  size 4
        public uint ActiveConnections;          // offset 8,  size 4

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string CurrentSelectedIp;        // offset 12, size 64

        public int CurrentLatencyMs;            // offset 76, size 4
        public uint TotalCandidates;            // offset 80, size 4
        public int LastErrorCode;               // offset 84, size 4

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string LastErrorMsg;             // offset 88, size 256
    }

    /// <summary>
    /// 纯 C ABI P/Invoke 导出声明。
    /// </summary>
    public static class CfnatNative
    {
        private const string LibName = "cfnat";

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cfnat_config_init_default(ref CfnatConfig cfg);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cfnat_create_instance(ref CfnatConfig cfg, out IntPtr outInstance);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cfnat_start(IntPtr inst);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cfnat_wait_ready(IntPtr inst, int timeoutMs);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cfnat_get_status(IntPtr inst, out CfnatStatus outStatus);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cfnat_rescan(IntPtr inst);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int cfnat_stop(IntPtr inst);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void cfnat_destroy(IntPtr inst);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr cfnat_version();
    }

    /// <summary>
    /// 单个 cfnat 原生实例的生命周期桥接与强引用根对象保护封装。
    /// 保证委托函数指针与外部回调上下文在原生线程活跃期间不被 GC 回收。
    /// </summary>
    public class CfnatInstanceBridge : IDisposable
    {
        private IntPtr _inst = IntPtr.Zero;
        private CfnatProtectSocketCallback? _protectCallback;
        private GCHandle _protectCallbackHandle;
        private CfnatLogCallback? _logCallback;
        private GCHandle _logCallbackHandle;
        private readonly Func<int, bool> _platformProtect;
        private readonly Core.Abstractions.IAcceleratorLogger? _logger;
        private ushort _port;
        private int _inFlightCallbacks;
        private volatile bool _isDisposed;

        public IntPtr NativeInstanceHandle => _inst;
        public bool IsActive => _inst != IntPtr.Zero;

        public CfnatInstanceBridge(Func<int, bool> platformProtect, Core.Abstractions.IAcceleratorLogger? logger = null)
        {
            _platformProtect = platformProtect ?? throw new ArgumentNullException(nameof(platformProtect));
            _logger = logger;
        }

        public bool Create(string listenAddr, ushort tlsPort, ushort httpPort, string probeSni, string cachePath, string ipSourcePath)
        {
            _port = tlsPort;
            var cfg = new CfnatConfig();
            CfnatNative.cfnat_config_init_default(ref cfg);

            // 显式校验 ABI 版本与结构体尺寸 (双向防御性防伪)
            const uint ExpectedAbiVersion = 0x00010000;
            const uint ExpectedConfigSize = 144;
            if (cfg.AbiVersion != ExpectedAbiVersion || cfg.StructSize != ExpectedConfigSize)
            {
                _logger?.Log($"[cfnat-{tlsPort}] 原生 ABI 版本或结构体尺寸不匹配: ABI=0x{cfg.AbiVersion:X8}(期望:0x{ExpectedAbiVersion:X8}), StructSize={cfg.StructSize}(期望:{ExpectedConfigSize})", Core.Abstractions.LogLevel.Error);
                return false;
            }

            // 1. 明确分配原生 UTF-8 内存，彻底规避 CLR 默认 ANSI 编码在不同平台上的歧义
            IntPtr pListenAddr = AllocUtf8(listenAddr);
            IntPtr pProbeSni = AllocUtf8(probeSni);
            IntPtr pCachePath = AllocUtf8(cachePath);
            IntPtr pIpSource = AllocUtf8(ipSourcePath);

            try
            {
                cfg.LocalListenAddr = pListenAddr;
                cfg.RemoteTlsPort = tlsPort;
                cfg.RemoteHttpPort = httpPort;
                cfg.ProbeSni = pProbeSni;
                cfg.CacheFilePath = pCachePath;
                cfg.IpSourcePath = pIpSource;

                // 2. 保持委托强引用并由 GCHandle.Alloc(Normal) 保护为根对象，防止 Delegate 在原生线程运行期间被 GC 回收
                _protectCallback = new CfnatProtectSocketCallback(OnProtectSocketInternal);
                _protectCallbackHandle = GCHandle.Alloc(_protectCallback, GCHandleType.Normal);
                cfg.ProtectSocketCb = Marshal.GetFunctionPointerForDelegate(_protectCallback);
                cfg.ProtectUserData = IntPtr.Zero;

                _logCallback = new CfnatLogCallback(OnLogInternal);
                _logCallbackHandle = GCHandle.Alloc(_logCallback, GCHandleType.Normal);
                cfg.LogCb = Marshal.GetFunctionPointerForDelegate(_logCallback);
                cfg.LogUserData = IntPtr.Zero;
                cfg.LogLevel = CfnatLogLevel.Debug;

                // 3. cfnat_create_instance 内部对所有传入字符串执行 strdup 深拷贝
                int rc = CfnatNative.cfnat_create_instance(ref cfg, out _inst);
                return rc == 0 && _inst != IntPtr.Zero;
            }
            finally
            {
                // 4. 原生引擎完成深拷贝后，立即释放托管层分配的临时 UTF-8 原生内存，杜绝内存泄漏
                FreeUtf8(pListenAddr);
                FreeUtf8(pProbeSni);
                FreeUtf8(pCachePath);
                FreeUtf8(pIpSource);
            }
        }

        public bool Start()
        {
            if (_inst == IntPtr.Zero) return false;
            return CfnatNative.cfnat_start(_inst) == 0;
        }

        public bool WaitReady(int timeoutMs)
        {
            if (_inst == IntPtr.Zero) return false;
            return CfnatNative.cfnat_wait_ready(_inst, timeoutMs) == 1;
        }

        public CfnatStatus GetStatus()
        {
            if (_inst == IntPtr.Zero)
            {
                return new CfnatStatus { State = CfnatState.Stopped };
            }
            CfnatNative.cfnat_get_status(_inst, out var status);
            const uint ExpectedStatusSize = 344;
            if (status.StructSize != ExpectedStatusSize)
            {
                _logger?.Log($"[cfnat-{_port}] 原生状态结构体尺寸不匹配: StructSize={status.StructSize}(期望:{ExpectedStatusSize})", Core.Abstractions.LogLevel.Error);
            }
            return status;
        }

        public void TriggerRescan()
        {
            if (_inst != IntPtr.Zero)
            {
                CfnatNative.cfnat_rescan(_inst);
            }
        }

        public void Stop()
        {
            if (_inst != IntPtr.Zero)
            {
                CfnatNative.cfnat_stop(_inst);
            }
        }

        /// <summary>
        /// 原生 C 引擎出站套接字保护内部回调。
        /// 核心准则：绝对捕获所有托管异常，严禁异常泄露穿透 ABI 边界。
        /// </summary>
        private int OnProtectSocketInternal(int socketFd, IntPtr userData)
        {
            System.Threading.Interlocked.Increment(ref _inFlightCallbacks);
            try
            {
                if (_isDisposed)
                {
                    return 0; // 实例已开始处置，立即拒绝出站
                }

                // 回调 Android VpnService.protect(int fd)
                bool ok = _platformProtect(socketFd);
                _logger?.Log($"[cfnat-{_port}-Protect] socketFd={socketFd} protectResult={ok}");
                return ok ? 1 : 0;
            }
            catch (Exception ex)
            {
                _logger?.Log($"[cfnat-{_port}-Protect] socketFd={socketFd} 保护异常: {ex.Message}", Core.Abstractions.LogLevel.Error);
                return 0;
            }
            finally
            {
                System.Threading.Interlocked.Decrement(ref _inFlightCallbacks);
            }
        }

        private void OnLogInternal(CfnatLogLevel level, IntPtr msgUtf8, IntPtr userData)
        {
            try
            {
                string? msg = Marshal.PtrToStringUTF8(msgUtf8);
                if (!string.IsNullOrEmpty(msg))
                {
                    var coreLvl = level switch
                    {
                        CfnatLogLevel.Error => Core.Abstractions.LogLevel.Error,
                        CfnatLogLevel.Warn => Core.Abstractions.LogLevel.Warning,
                        _ => Core.Abstractions.LogLevel.Info
                    };
                    _logger?.Log($"[cfnat-{_port}] {msg}", coreLvl);
                    Logging.AppLogBuffer.Append($"cfnat-{_port}", level.ToString().ToUpperInvariant(), msg);
                }
            }
            catch { }
        }

        /// <summary>
        /// 资源释放：严格遵循 8 步逆序时序规范！
        /// 1. 先调用 cfnat_stop 通知工作线程退出；
        /// 2. 等待所有后台 Worker 线程与在途 Protect 回调彻底终止退出；
        /// 3. 调用 cfnat_destroy 销毁原生实例；
        /// 4. 仅在原生实例销毁且回调完全退出后，才释放 GCHandle 强引用根对象。
        /// </summary>
        public void Dispose()
        {
            _isDisposed = true;

            if (_inst != IntPtr.Zero)
            {
                // 1. 停止 cfnat 引擎，通知所有 worker 退出
                CfnatNative.cfnat_stop(_inst);

                // 2. 等待在途 Protect 回调全部返回 (最长等待 2 秒)
                int waitCount = 0;
                while (System.Threading.Volatile.Read(ref _inFlightCallbacks) > 0 && waitCount < 200)
                {
                    System.Threading.Thread.Sleep(10);
                    waitCount++;
                }

                // 3. 彻底销毁 cfnat 原生实例资源
                CfnatNative.cfnat_destroy(_inst);
                _inst = IntPtr.Zero;
            }

            // 4. 原生引擎已完全销毁且无在途回调后，才释放 GCHandle 强引用保护
            if (_protectCallbackHandle.IsAllocated)
            {
                _protectCallbackHandle.Free();
                _protectCallback = null;
            }

            if (_logCallbackHandle.IsAllocated)
            {
                _logCallbackHandle.Free();
                _logCallback = null;
            }
        }

        private static IntPtr AllocUtf8(string? str)
        {
            if (str == null) return IntPtr.Zero;
            byte[] bytes = Encoding.UTF8.GetBytes(str);
            IntPtr ptr = Marshal.AllocCoTaskMem(bytes.Length + 1);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr, bytes.Length, 0); // null terminator
            return ptr;
        }

        private static void FreeUtf8(IntPtr ptr)
        {
            if (ptr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(ptr);
            }
        }
    }
}
