using System;

namespace TESL.Accelerator.AndroidHost.Interop
{
    /* ========================================================================= */
    /*  【设计完成，未验证】: sing-box v1.11.10 契约与适配模型                      */
    /*  源码依据: sing-box v1.11.10 (experimental/libbox/platform.go & tun.go)    */
    /*  说明: 待取得实际 libbox.aar 后，由 Lightweight Java Adapter 挂载对接。        */
    /* ========================================================================= */

    /// <summary>
    /// 路由前缀模型 (对应 Go libbox.RoutePrefix)
    /// </summary>
    public class RoutePrefix
    {
        public string Address { get; set; } = string.Empty;
        public int Prefix { get; set; }
    }

    /// <summary>
    /// 路由前缀迭代器 (对应 Go libbox.RoutePrefixIterator)
    /// </summary>
    public interface IRoutePrefixIterator
    {
        bool HasNext();
        RoutePrefix? Next();
    }

    /// <summary>
    /// 字符串迭代器 (对应 Go libbox.StringIterator)
    /// </summary>
    public interface IStringIterator
    {
        bool HasNext();
        string Next();
        int Len();
    }

    /// <summary>
    /// 单值字符串安全封送容器 (对应 Go libbox.StringBox)
    /// </summary>
    public class StringBox
    {
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>
    /// 系统通知抽象 (对应 Go libbox.Notification)
    /// </summary>
    public class Notification
    {
        public string Identifier { get; set; } = string.Empty;
        public string TypeName { get; set; } = string.Empty;
        public int TypeID { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string OpenURL { get; set; } = string.Empty;
    }

    /// <summary>
    /// 接口更新监听器 (对应 Go libbox.InterfaceUpdateListener)
    /// </summary>
    public interface IInterfaceUpdateListener
    {
        void UpdateDefaultInterface(int index, string name);
    }

    /// <summary>
    /// 网络接口迭代器 (对应 Go libbox.NetworkInterfaceIterator)
    /// </summary>
    public interface INetworkInterfaceIterator
    {
        bool HasNext();
        object? Next();
    }

    /// <summary>
    /// sing-box v1.11.10 TunOptions 完整契约 (共 19 项方法)
    /// </summary>
    public interface ITunOptions
    {
        // 1-2. IPv4 / IPv6 虚拟网卡分配地址
        IRoutePrefixIterator? GetInet4Address();
        IRoutePrefixIterator? GetInet6Address();

        // 3. 最大传输单元 MTU
        int GetMTU();

        // 4-5. 路由自动化标志
        bool GetAutoRoute();
        bool GetStrictRoute();

        // 6-7. 包含的 IPv4 / IPv6 路由
        IRoutePrefixIterator? GetInet4RouteAddress();
        IRoutePrefixIterator? GetInet6RouteAddress();

        // 8-9. 排除的 IPv4 / IPv6 路由
        IRoutePrefixIterator? GetInet4RouteExcludeAddress();
        IRoutePrefixIterator? GetInet6RouteExcludeAddress();

        // 10-11. IPv4 / IPv6 路由范围
        IRoutePrefixIterator? GetInet4RouteRange();
        IRoutePrefixIterator? GetInet6RouteRange();

        // 12-13. 分应用代理包名过滤集合
        IStringIterator? GetIncludePackage();
        IStringIterator? GetExcludePackage();

        // 14. TUN DNS 服务器地址
        StringBox? GetDNSServerAddress();

        // 15. 是否启用 HTTP 代理集成
        bool IsHTTPProxyEnabled();

        // 16-17. HTTP 代理服务器地址与端口
        string GetHTTPProxyServer();
        int GetHTTPProxyServerPort();

        // 18-19. HTTP 代理直连绕过与匹配域名集合
        IStringIterator? GetHTTPProxyBypassDomain();
        IStringIterator? GetHTTPProxyMatchDomain();
    }

    /// <summary>
    /// sing-box v1.11.10 PlatformInterface 完整契约 (共 16 项方法)
    /// </summary>
    public interface ILibboxPlatformInterface
    {
        // 1. 是否使用平台自动检测接口控制
        bool UsePlatformAutoDetectInterfaceControl();

        // 2. 套接字出站路由保护 (传入 Linux int32 fd，底层由宿主调用 VpnService.protect(fd))
        void AutoDetectInterfaceControl(int fd);

        // 3. 打开 TUN 接口 (接收 TunOptions，返回创建好的底层 Linux int32 文件描述符)
        int OpenTun(ITunOptions options);

        // 4. 核心日志输出回调
        void WriteLog(string message);

        // 5. 是否允许核心使用 Linux /proc 伪文件系统进行连接属主查询
        bool UseProcFS();

        // 6. 根据网络四元组反查系统连接归属 UID
        int FindConnectionOwner(int ipProtocol, string sourceAddress, int sourcePort, string destinationAddress, int destinationPort);

        // 7. 根据 UID 获取 Android 应用包名
        string PackageNameByUID(int uid);

        // 8. 根据应用包名获取对应 UID
        int UIDByPackageName(string packageName);

        // 9. 启动系统默认网络接口监听器
        void StartDefaultInterfaceMonitor(IInterfaceUpdateListener listener);

        // 10. 关闭系统默认网络接口监听器
        void CloseDefaultInterfaceMonitor(IInterfaceUpdateListener listener);

        // 11. 获取本地全部活跃网络接口列表
        INetworkInterfaceIterator? GetInterfaces();

        // 12. 查询是否处于 NetworkExtension / VPN 环境中
        bool UnderNetworkExtension();

        // 13. 是否包含全部网络流量
        bool IncludeAllNetworks();

        // 14. 清除底层 DNS 缓存
        void ClearDNSCache();

        // 15. 读取当前 Wi-Fi 状态
        void ReadWIFIState();

        // 16. 发送系统通知
        void SendNotification(Notification notification);
    }
}
