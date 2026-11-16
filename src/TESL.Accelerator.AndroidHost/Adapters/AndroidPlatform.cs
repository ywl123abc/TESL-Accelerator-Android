using System;
using System.IO;
using TESL.Accelerator.Core.Abstractions;

namespace TESL.Accelerator.AndroidHost.Adapters
{
    /// <summary>
    /// Android 平台核心宿主适配器。
    /// 聚合 TUN 路由、双通道 cfnat 引擎、套接字保护及私有目录管理。
    /// 【架构设计】：
    /// 1. ProcessSupervisor 明确为 null (Android 内部服务自主托管，不模拟 Windows Job Object)。
    /// 2. 隔离 Android OS API，防止其向 Core 业务核心渗透。
    /// </summary>
    public class AndroidPlatform : IAcceleratorPlatform
    {
        public string PlatformName => "Android";

        public string DataDirectory { get; }
        public string Cfnat443Directory { get; }
        public string Cfnat8443Directory { get; }

        public ITunnelManager TunnelManager { get; }
        public IEngineHost EngineHost { get; }
        public ICandidateProbe CandidateProbe { get; }
        public IProtectedSocketProvider ProtectedSocketProvider { get; }

        /// <summary>
        /// Android 平台无外部子进程，不使用 Windows Job Object，此项固定为 null。
        /// </summary>
        public IProcessSupervisor? ProcessSupervisor => null;

        private readonly Func<bool>? _gameRunningChecker;
        private readonly Action? _gameLauncher;

        public AndroidPlatform(
            string dataDirectory,
            ITunnelManager tunnelManager,
            IEngineHost engineHost,
            IProtectedSocketProvider protectedSocketProvider,
            ICandidateProbe? candidateProbe = null,
            Func<bool>? gameRunningChecker = null,
            Action? gameLauncher = null)
        {
            DataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
            Cfnat443Directory = Path.Combine(DataDirectory, "cfnat443");
            Cfnat8443Directory = Path.Combine(DataDirectory, "cfnat8443");

            TunnelManager = tunnelManager ?? throw new ArgumentNullException(nameof(tunnelManager));
            EngineHost = engineHost ?? throw new ArgumentNullException(nameof(engineHost));
            ProtectedSocketProvider = protectedSocketProvider ?? throw new ArgumentNullException(nameof(protectedSocketProvider));
            CandidateProbe = candidateProbe ?? new AndroidCandidateProbe(ProtectedSocketProvider, new AndroidAcceleratorLogger(DataDirectory));

            _gameRunningChecker = gameRunningChecker;
            _gameLauncher = gameLauncher;
        }

        public bool EnsureSecurityEnvironment()
        {
            try
            {
                if (!Directory.Exists(DataDirectory)) Directory.CreateDirectory(DataDirectory);
                if (!Directory.Exists(Cfnat443Directory)) Directory.CreateDirectory(Cfnat443Directory);
                if (!Directory.Exists(Cfnat8443Directory)) Directory.CreateDirectory(Cfnat8443Directory);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void DeployAssets()
        {
            // Android 资产部署逻辑: 释放内嵌 ips-v4.txt 与配置文件至私有目录
            EnsureSecurityEnvironment();
            DeployResourceIfMissing("TESL.Accelerator.AndroidHost.Assets.ips-v4.txt", Path.Combine(Cfnat443Directory, "ips-v4.txt"));
            DeployResourceIfMissing("TESL.Accelerator.AndroidHost.Assets.ips-v4-8443.txt", Path.Combine(Cfnat8443Directory, "ips-v4.txt"));
            DeployResourceIfMissing("TESL.Accelerator.AndroidHost.Assets.cloudflare-ranges-v4.txt", Path.Combine(DataDirectory, "cloudflare-ranges-v4.txt"));
            DeployResourceIfMissing("TESL.Accelerator.AndroidHost.Assets.ip-sources.json", Path.Combine(DataDirectory, "ip-sources.json"));
            DeployResourceIfMissing("TESL.Accelerator.AndroidHost.Assets.locations.json", Path.Combine(DataDirectory, "locations.json"));
        }

        private static void DeployResourceIfMissing(string resourceName, string targetPath)
        {
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
                return;

            var asm = typeof(AndroidPlatform).Assembly;
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream == null) return;

            string dir = Path.GetDirectoryName(targetPath) ?? "";
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            stream.CopyTo(fs);
        }

        public bool IsGameRunning()
        {
            return _gameRunningChecker?.Invoke() ?? false;
        }

        public void LaunchGame()
        {
            _gameLauncher?.Invoke();
        }
    }
}
