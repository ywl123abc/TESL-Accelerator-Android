namespace TESL.Accelerator.Core.Abstractions
{
    public interface IProtectedSocketProvider
    {
        /// <summary>
        /// 当前平台是否需要执行 TUN 流量回环保护 (Android VpnService 必须 protect；Windows 则由系统路由分流或直连)
        /// </summary>
        bool RequiresSocketProtection { get; }

        /// <summary>
        /// 保护套接字句柄或文件描述符绕过 TUN 路由
        /// </summary>
        /// <param name="socketHandleOrFd">Windows 下为 Socket Handle，Linux/Android 下为 int fd</param>
        bool ProtectSocket(long socketHandleOrFd);
    }
}
