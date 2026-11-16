using System;
using TESL.Accelerator.Core.Abstractions;

namespace TESL.Accelerator.AndroidHost.Adapters
{
    /// <summary>
    /// Android 平台出站套接字绕过保护实现。
    /// 确保所有由 cfnat 原生引擎或探测服务发起的外部出站 TCP 套接字，
    /// 都通过 Android VpnService.protect() 系统调用，从而绕过 TUN 虚拟网卡，
    /// 杜绝流量在本地形成无限死循环。
    /// </summary>
    public class AndroidProtectedSocketProvider : IProtectedSocketProvider
    {
        private readonly Func<int, bool> _platformProtectFn;

        public bool RequiresSocketProtection => true;

        public AndroidProtectedSocketProvider(Func<int, bool> platformProtectFn)
        {
            _platformProtectFn = platformProtectFn ?? throw new ArgumentNullException(nameof(platformProtectFn));
        }

        public bool ProtectSocket(long socketHandleOrFd)
        {
            if (socketHandleOrFd < 0 || socketHandleOrFd > int.MaxValue)
            {
                return false;
            }

            try
            {
                return _platformProtectFn((int)socketHandleOrFd);
            }
            catch
            {
                return false;
            }
        }
    }
}
