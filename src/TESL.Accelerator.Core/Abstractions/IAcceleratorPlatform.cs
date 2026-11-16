using System;
using System.Threading;
using System.Threading.Tasks;

namespace TESL.Accelerator.Core.Abstractions
{
    public interface IAcceleratorPlatform
    {
        string PlatformName { get; }
        string DataDirectory { get; }
        string Cfnat443Directory { get; }
        string Cfnat8443Directory { get; }

        ITunnelManager TunnelManager { get; }
        IEngineHost EngineHost { get; }
        ICandidateProbe CandidateProbe { get; }
        IProtectedSocketProvider ProtectedSocketProvider { get; }
        /// <summary>
        /// 外部子进程生命周期监管器 (Windows 下为 Job Object；Android 原生在应用进程内部运行，此项可为 null)
        /// </summary>
        IProcessSupervisor? ProcessSupervisor { get; }

        /// <summary>
        /// 校验并构建受保护运行目录及安全 ACL 隔离
        /// </summary>
        bool EnsureSecurityEnvironment();

        /// <summary>
        /// 释放或验证平台专属二进制资产
        /// </summary>
        void DeployAssets();

        /// <summary>
        /// 探测当前平台游戏是否正在运行
        /// </summary>
        bool IsGameRunning();

        /// <summary>
        /// 平台拉起游戏客户端
        /// </summary>
        void LaunchGame();
    }
}
