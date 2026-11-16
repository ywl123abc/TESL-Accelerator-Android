using System;
using System.IO;
using TESL.Accelerator.Core.Abstractions;
using TESL.Accelerator.Core.Services;

namespace TESL.Accelerator.AndroidEngine
{
    public class AndroidPlatform : IAcceleratorPlatform
    {
        public const string DefaultAndroidPackageName = "com.bethsoft.theelderscrollslegends";

        public string PlatformName => "Android";
        public string DataDirectory { get; }
        public string Cfnat443Directory { get; }
        public string Cfnat8443Directory { get; }

        public ITunnelManager TunnelManager { get; }
        public IEngineHost EngineHost { get; }
        public ICandidateProbe CandidateProbe { get; }
        public IProtectedSocketProvider ProtectedSocketProvider { get; }
        public IProcessSupervisor ProcessSupervisor { get; }

        private readonly IAcceleratorLogger? _logger;
        private readonly Func<bool>? _isGameRunningCallback;
        private readonly Action? _launchGameCallback;

        public AndroidPlatform(
            string? baseDirectory = null,
            Func<int, bool>? vpnProtectCallback = null,
            Func<bool>? isGameRunningCallback = null,
            Action? launchGameCallback = null,
            IAcceleratorLogger? logger = null)
        {
            DataDirectory = baseDirectory ?? "/data/data/com.tesl.accelerator/files";
            Cfnat443Directory = Path.Combine(DataDirectory, "cfnat_443");
            Cfnat8443Directory = Path.Combine(DataDirectory, "cfnat_8443");
            _logger = logger;
            _isGameRunningCallback = isGameRunningCallback;
            _launchGameCallback = launchGameCallback;

            ProtectedSocketProvider = new AndroidProtectedSocketProvider(vpnProtectCallback);
            TunnelManager = new AndroidTunnelManager(DataDirectory, _logger);
            EngineHost = new AndroidEngineHost(Cfnat443Directory, Cfnat8443Directory, ProtectedSocketProvider, _logger);
            CandidateProbe = new AndroidCandidateProbeService(ProtectedSocketProvider, _logger);
            ProcessSupervisor = new AndroidProcessSupervisor(_logger);
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
            catch (Exception ex)
            {
                _logger?.Log($"[Android-Security] 目录初始化异常: {ex.Message}", LogLevel.Error);
                return false;
            }
        }

        public void DeployAssets()
        {
            // 在 Android 下从 APK assets 拷贝到 DataDirectory
            _logger?.Log("[Android-Assets] 验证并同步 Android 专属内置资源。");
        }

        public bool IsGameRunning()
        {
            if (_isGameRunningCallback != null)
            {
                return _isGameRunningCallback.Invoke();
            }
            return false;
        }

        public void LaunchGame()
        {
            if (_launchGameCallback != null)
            {
                _launchGameCallback.Invoke();
            }
            else
            {
                _logger?.Log($"[Android-Game] 准备通过 Intent 唤起游戏包名: {DefaultAndroidPackageName}");
            }
        }
    }
}
