using System;
using TESL.Accelerator.Core.Abstractions;

namespace TESL.Accelerator.AndroidEngine
{
    public class AndroidProtectedSocketProvider : IProtectedSocketProvider
    {
        private readonly Func<int, bool>? _vpnProtectCallback;

        /// <summary>
        /// Android 平台必须为 true，以防止 cfnat 自身发起的 Anycast 探测与代理流量被 sing-box TUN 拦截导致死循环
        /// </summary>
        public bool RequiresSocketProtection => true;

        public AndroidProtectedSocketProvider(Func<int, bool>? vpnProtectCallback = null)
        {
            _vpnProtectCallback = vpnProtectCallback;
        }

        public bool ProtectSocket(long socketHandleOrFd)
        {
            int fd = (int)socketHandleOrFd;
            if (_vpnProtectCallback != null)
            {
                return _vpnProtectCallback.Invoke(fd);
            }

            // 在未连接实际 Android VpnService 运行时的预研/设计桩状态下返回 true
            return true;
        }
    }
}
