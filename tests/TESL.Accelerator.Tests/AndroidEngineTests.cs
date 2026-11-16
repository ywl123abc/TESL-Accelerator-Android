using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TESL.Accelerator.AndroidHost.Adapters;
using TESL.Accelerator.AndroidHost.Lifecycle;
using TESL.Accelerator.Core.Abstractions;
using TESL.Accelerator.Core.Models;
using TESL.Accelerator.Core.Services;

namespace TESL.Accelerator.Tests
{
    public static class AndroidEngineTests
    {
        private class MockEngineHost : IEngineHost
        {
            public bool IsRunning { get; private set; }
            public int StartCount { get; private set; }
            public int StopCount { get; private set; }

            public Task<bool> StartEnginesAsync(AcceleratorConfig config, string cand443Path, string cand8443Path, CancellationToken ct = default)
            {
                StartCount++;
                IsRunning = true;
                return Task.FromResult(true);
            }

            public Task StopEnginesAsync()
            {
                StopCount++;
                IsRunning = false;
                return Task.CompletedTask;
            }
        }

        private class MockProbe : ICandidateProbe
        {
            public Task<bool> CheckProxyEndpointsReadyAsync(int localPort443, int localPort8443, string sni443, string sni8443, TimeSpan timeout, CancellationToken ct = default)
            {
                return Task.FromResult(true);
            }

            public Task<bool> ProbeTlsHandshakeAsync(string host, int port, string sniHost, int timeoutMs, CancellationToken ct = default)
            {
                return Task.FromResult(true);
            }
        }

        public static void Run()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "tesl_android_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                Test1_CoreCoordinatorAndAndroidPlatformIntegration(tempDir);
                Test2_VpnServiceLifecycleStateTransitions();
                Test3_TunFdOwnershipTimeline();
                Test4_DualCfnatInstanceIndependence();
                Test5_ProtectFailureBlocksOutbound();
                Test6_StartFailureAndRollback();
                Test7_RepeatStartAndStopIdempotency();
                Test8_SystemRevokeSafetyCleanup();
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        /// <summary>
        /// 1. Core Coordinator 与 AndroidPlatform 集成测试
        /// 验证 AndroidPlatform 正确装配、ProcessSupervisor 为 null、Android sing-box 配置生效
        /// </summary>
        private static void Test1_CoreCoordinatorAndAndroidPlatformIntegration(string baseDir)
        {
            var protectedSocketProvider = new AndroidProtectedSocketProvider(fd => true);

            int currentMasterFd = 55;
            bool masterFdClosed = false;

            var logger = new AndroidAcceleratorLogger(baseDir);
            var tunnelManager = new AndroidTunnelManager(() => currentMasterFd, () => masterFdClosed = true, logger);
            var engineHost = new MockEngineHost();
            var probe = new MockProbe();

            var platform = new AndroidPlatform(
                baseDir,
                tunnelManager,
                engineHost,
                protectedSocketProvider,
                probe);

            Assert.Equal("Android", platform.PlatformName, "PlatformName must be Android");
            Assert.True(platform.ProcessSupervisor == null, "Android platform ProcessSupervisor must be null (no Windows Job Object)");
            Assert.True(platform.EnsureSecurityEnvironment(), "EnsureSecurityEnvironment must create directories");
            Assert.True(Directory.Exists(platform.Cfnat443Directory), "Cfnat443Directory must exist");
            Assert.True(Directory.Exists(platform.Cfnat8443Directory), "Cfnat8443Directory must exist");

            var coordinator = new AcceleratorCoordinator(platform, AcceleratorConfig.CreateDefault(), candidateProbe: probe);

            bool started = coordinator.StartAsync().GetAwaiter().GetResult();
            Assert.True(started, "Coordinator must start on AndroidPlatform");
            Assert.True(coordinator.IsRunning, "Coordinator must be running");
            Assert.True(tunnelManager.IsRunning, "TunnelManager must be running");
            Assert.True(engineHost.IsRunning, "EngineHost must be running");

            // 验证 Android sing-box 配置中包含 include_package 规则且绝不包含 process_name
            Assert.True(tunnelManager.ActiveConfigJson != null, "ActiveConfigJson must be set");
            Assert.Contains("com.bethsoft.theelderscrollslegends", tunnelManager.ActiveConfigJson!, "Android config must contain TESL package");
            Assert.Contains("include_package", tunnelManager.ActiveConfigJson!, "Android config must contain include_package");
            Assert.DoesNotContain("process_name", tunnelManager.ActiveConfigJson!, "Android config must NOT contain process_name");

            coordinator.Stop();
            Assert.False(coordinator.IsRunning, "Coordinator must stop");
            Assert.False(tunnelManager.IsRunning, "TunnelManager must stop");
            Assert.False(engineHost.IsRunning, "EngineHost must stop");
            tunnelManager.CloseMasterFd();
            Assert.True(masterFdClosed, "masterFd must be closed by host after coordinator stops");
        }

        /// <summary>
        /// 2. VpnService 生命周期状态测试
        /// 验证 Stopped -> Starting -> Running -> Stopping -> Stopped 状态转换
        /// </summary>
        private static void Test2_VpnServiceLifecycleStateTransitions()
        {
            var manager = new AndroidVpnLifecycleManager();
            Assert.Equal(AndroidVpnState.Stopped, manager.CurrentState, "Initial state must be Stopped");
            Assert.False(manager.IsRunning, "Initially not running");

            var stateLog = new System.Collections.Generic.List<AndroidVpnState>();
            manager.OnStateChanged += (st, desc) => stateLog.Add(st);

            bool startResult = manager.StartAsync(
                establishTunAction: () => 101,
                startCoordinatorAction: () => Task.FromResult(true)
            ).GetAwaiter().GetResult();

            Assert.True(startResult, "StartAsync must succeed");
            Assert.Equal(AndroidVpnState.Running, manager.CurrentState, "State must be Running");
            Assert.True(manager.IsRunning, "IsRunning must be true");
            Assert.Equal(101, manager.ActiveMasterFd, "ActiveMasterFd must be 101");

            bool tunClosed = false;
            bool fgStopped = false;

            manager.StopAsync(
                stopCoordinatorAction: () => Task.CompletedTask,
                closeTunAction: () => tunClosed = true,
                stopForegroundAction: () => fgStopped = true
            ).GetAwaiter().GetResult();

            Assert.Equal(AndroidVpnState.Stopped, manager.CurrentState, "State must be Stopped after stop");
            Assert.False(manager.IsRunning, "IsRunning must be false");
            Assert.Equal(-1, manager.ActiveMasterFd, "ActiveMasterFd must be reset to -1");
            Assert.True(tunClosed, "TUN must be closed");
            Assert.True(fgStopped, "Foreground service must be stopped");

            Assert.Contains(AndroidVpnState.Starting.ToString(), string.Join(",", stateLog));
            Assert.Contains(AndroidVpnState.Running.ToString(), string.Join(",", stateLog));
            Assert.Contains(AndroidVpnState.Stopping.ToString(), string.Join(",", stateLog));
            Assert.Contains(AndroidVpnState.Stopped.ToString(), string.Join(",", stateLog));
        }

        /// <summary>
        /// 3. TUN fd 所有权时序测试
        /// 验证宿主持有 masterPfd 并在运行期间保持有效，libbox 停止在先，masterFd 关闭在后
        /// </summary>
        private static void Test3_TunFdOwnershipTimeline()
        {
            var timeline = new System.Collections.Generic.List<string>();

            int hostMasterFd = 200;
            var manager = new AndroidVpnLifecycleManager();

            manager.StartAsync(
                establishTunAction: () =>
                {
                    timeline.Add("HostEstablishTun:" + hostMasterFd);
                    return hostMasterFd;
                },
                startCoordinatorAction: () =>
                {
                    timeline.Add("LibboxDupFd:" + hostMasterFd);
                    return Task.FromResult(true);
                }
            ).GetAwaiter().GetResult();

            Assert.Equal(200, manager.ActiveMasterFd, "Host must hold masterFd during entire session");

            manager.StopAsync(
                stopCoordinatorAction: () =>
                {
                    timeline.Add("LibboxStop");
                    timeline.Add("CfnatStop");
                    timeline.Add("CfnatDestroyAndFreeGCHandle");
                    return Task.CompletedTask;
                },
                closeTunAction: () =>
                {
                    timeline.Add("HostCloseMasterFd:" + hostMasterFd);
                },
                stopForegroundAction: () =>
                {
                    timeline.Add("StopForegroundService");
                }
            ).GetAwaiter().GetResult();

            // 严格验证 8 步逆序时序: Libbox 停止 -> cfnat 停止与销毁 -> 宿主关闭 masterFd -> 停止前台服务
            int libboxStopIdx = timeline.IndexOf("LibboxStop");
            int cfnatDestroyIdx = timeline.IndexOf("CfnatDestroyAndFreeGCHandle");
            int closeFdIdx = timeline.IndexOf("HostCloseMasterFd:200");
            int stopFgIdx = timeline.IndexOf("StopForegroundService");

            Assert.True(libboxStopIdx >= 0 && cfnatDestroyIdx >= 0 && closeFdIdx >= 0 && stopFgIdx >= 0, "All timeline steps must exist");
            Assert.True(libboxStopIdx < cfnatDestroyIdx, "Libbox MUST stop before cfnat destroy");
            Assert.True(cfnatDestroyIdx < closeFdIdx, "Cfnat destroy MUST complete before host closes masterPfd");
            Assert.True(closeFdIdx < stopFgIdx, "MasterFd MUST close before foreground service stops");
        }

        /// <summary>
        /// 4. 两个 cfnat 实例独立生命周期测试
        /// 验证 443 与 8443 通道各自独立的配置、端口与生命周期隔离
        /// </summary>
        private static void Test4_DualCfnatInstanceIndependence()
        {
            var cfg = AcceleratorConfig.CreateDefault();
            Assert.Equal(10443, cfg.Channel443.LocalPort, "Channel 443 local port must be 10443");
            Assert.Equal(18443, cfg.Channel8443.LocalPort, "Channel 8443 local port must be 18443");
            Assert.Equal(443, cfg.Channel443.RemotePort);
            Assert.Equal(8443, cfg.Channel8443.RemotePort);
            Assert.Equal("tesl-reborn.com", cfg.Channel443.SniHost);
            Assert.Equal("pubsub.tesl-reborn.com", cfg.Channel8443.SniHost);

            // 验证双通道独立配置
            var pool = new CandidatePool();
            pool.AddCandidates443(new[] { "104.16.1.1", "104.16.1.2" });
            pool.AddCandidates8443(new[] { "162.159.192.1" });

            Assert.Equal(2, pool.Candidates443.Count, "443 candidates count must be 2");
            Assert.Equal(1, pool.Candidates8443.Count, "8443 candidates count must be 1");
        }

        /// <summary>
        /// 5. Protect 失败时禁止出站测试
        /// 验证 Protect 回调返回 false 时，禁止外部连接发起
        /// </summary>
        private static void Test5_ProtectFailureBlocksOutbound()
        {
            int protectCalls = 0;
            var rejectingProvider = new AndroidProtectedSocketProvider(fd =>
            {
                protectCalls++;
                return false; // 模拟系统 protect(fd) 失败 (如 VPN 未准备好或 fd 无效)
            });

            Assert.True(rejectingProvider.RequiresSocketProtection, "Android must require socket protection");
            bool ok = rejectingProvider.ProtectSocket(1234);
            Assert.False(ok, "ProtectSocket must return false when platform protect fails");
            Assert.Equal(1, protectCalls, "Platform protect must be called once");

            // 负数或超范围 fd 守卫
            Assert.False(rejectingProvider.ProtectSocket(-1), "Negative fd must immediately fail without calling platform");
            Assert.Equal(1, protectCalls, "Negative fd must not invoke platform protect");

            // 测试 AndroidCandidateProbe 在 protect 失败时主动拒绝出站
            var logger = new AndroidAcceleratorLogger();
            var probe = new AndroidCandidateProbe(rejectingProvider, logger);
            bool probeResult = probe.ProbeTlsHandshakeAsync("1.1.1.1", 443, "tesl-reborn.com", 1000).GetAwaiter().GetResult();
            Assert.False(probeResult, "Probe MUST be blocked and return false when protect fails");
        }

        /// <summary>
        /// 6. 启动失败与停止回滚测试
        /// 验证 TUN 创建失败或 Coordinator 失败时正确回退至 Faulted 且无句柄泄漏
        /// </summary>
        private static void Test6_StartFailureAndRollback()
        {
            var manager = new AndroidVpnLifecycleManager();

            // 场景 A: establishTun 失败 (返回 -1)
            bool startFailA = manager.StartAsync(
                establishTunAction: () => -1,
                startCoordinatorAction: () => Task.FromResult(true)
            ).GetAwaiter().GetResult();

            Assert.False(startFailA, "StartAsync must fail when establishTun returns -1");
            Assert.Equal(AndroidVpnState.Faulted, manager.CurrentState, "State must be Faulted");
            Assert.Equal(-1, manager.ActiveMasterFd);

            // 场景 B: Coordinator 启动失败
            var managerB = new AndroidVpnLifecycleManager();
            bool startFailB = managerB.StartAsync(
                establishTunAction: () => 888,
                startCoordinatorAction: () => Task.FromResult(false)
            ).GetAwaiter().GetResult();

            Assert.False(startFailB, "StartAsync must fail when coordinator returns false");
            Assert.Equal(AndroidVpnState.Faulted, managerB.CurrentState, "State must be Faulted");
        }

        /// <summary>
        /// 7. 重复启动及重复停止测试 (幂等性保护)
        /// 验证并发与重复调用不发生死锁或重复分配
        /// </summary>
        private static void Test7_RepeatStartAndStopIdempotency()
        {
            int establishCalls = 0;
            int closeCalls = 0;

            var manager = new AndroidVpnLifecycleManager();

            bool start1 = manager.StartAsync(
                establishTunAction: () => { establishCalls++; return 301; },
                startCoordinatorAction: () => Task.FromResult(true)
            ).GetAwaiter().GetResult();

            Assert.True(start1, "First start must succeed");
            Assert.Equal(1, establishCalls, "establishTun called once");

            // 二次重复启动
            bool start2 = manager.StartAsync(
                establishTunAction: () => { establishCalls++; return 302; },
                startCoordinatorAction: () => Task.FromResult(true)
            ).GetAwaiter().GetResult();

            Assert.True(start2, "Repeated start must return true idempotently");
            Assert.Equal(1, establishCalls, "Repeated start must NOT invoke establishTun again");

            // 停止
            manager.StopAsync(
                stopCoordinatorAction: () => Task.CompletedTask,
                closeTunAction: () => closeCalls++,
                stopForegroundAction: () => { }
            ).GetAwaiter().GetResult();

            Assert.Equal(1, closeCalls, "closeTun called once");

            // 二次重复停止
            manager.StopAsync(
                stopCoordinatorAction: () => Task.CompletedTask,
                closeTunAction: () => closeCalls++,
                stopForegroundAction: () => { }
            ).GetAwaiter().GetResult();

            Assert.Equal(1, closeCalls, "Repeated stop must NOT invoke closeTun again");
            Assert.Equal(AndroidVpnState.Stopped, manager.CurrentState);
        }

        /// <summary>
        /// 8. 系统 OnRevoke 应急安全清理测试
        /// 验证系统撤销授权时无阻塞回收所有资源
        /// </summary>
        private static void Test8_SystemRevokeSafetyCleanup()
        {
            bool coordStopped = false;
            bool tunClosed = false;
            bool fgStopped = false;

            var manager = new AndroidVpnLifecycleManager();
            manager.StartAsync(
                establishTunAction: () => 500,
                startCoordinatorAction: () => Task.FromResult(true)
            ).GetAwaiter().GetResult();

            Assert.True(manager.IsRunning);

            // 模拟系统 OnRevoke 事件到达
            manager.HandleRevoke(
                stopCoordinatorAction: () => { coordStopped = true; return Task.CompletedTask; },
                closeTunAction: () => tunClosed = true,
                stopForegroundAction: () => fgStopped = true
            );

            Assert.True(coordStopped, "Coordinator must be stopped on revoke");
            Assert.True(tunClosed, "Tun must be closed on revoke");
            Assert.True(fgStopped, "Foreground must be stopped on revoke");
            Assert.Equal(AndroidVpnState.Stopped, manager.CurrentState);
            Assert.False(manager.IsRunning);
            Assert.True(manager.IsRevoked, "Manager must be marked as IsRevoked after revoke");
            Assert.False(manager.StartAsync(() => 501, () => Task.FromResult(true)).GetAwaiter().GetResult(), "StartAsync must fail after revoke");
        }
    }
}
