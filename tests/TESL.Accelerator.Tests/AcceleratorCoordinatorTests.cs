using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TESL.Accelerator.Core.Abstractions;
using TESL.Accelerator.Core.Models;
using TESL.Accelerator.Core.Services;

namespace TESL.Accelerator.Tests
{
    public static class AcceleratorCoordinatorTests
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

        private class MockTunnelManager : ITunnelManager
        {
            public bool IsRunning { get; private set; }
            public string? LastConfigJson { get; private set; }
            public int StartCount { get; private set; }
            public int StopCount { get; private set; }

            public Task<bool> StartTunnelAsync(string singBoxConfigJson, CancellationToken ct = default)
            {
                StartCount++;
                LastConfigJson = singBoxConfigJson;
                IsRunning = true;
                return Task.FromResult(true);
            }

            public Task StopTunnelAsync()
            {
                StopCount++;
                IsRunning = false;
                return Task.CompletedTask;
            }
        }

        private class MockProcessSupervisor : IProcessSupervisor
        {
            public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
            public void RegisterProcess(IntPtr handle, int processId, string tag) { }
            public void TerminateAll() { }
        }

        private class MockCandidateProbe : ICandidateProbe
        {
            public bool ProbeResult { get; set; } = true;
            public Task<bool> ProbeTlsHandshakeAsync(string host, int port, string sniHost, int timeoutMs, CancellationToken ct = default)
            {
                return Task.FromResult(ProbeResult);
            }

            public Task<bool> CheckProxyEndpointsReadyAsync(int port443, int port8443, string sni443, string sni8443, TimeSpan timeout, CancellationToken ct = default)
            {
                return Task.FromResult(ProbeResult);
            }
        }

        private class MockProtectedSocketProvider : IProtectedSocketProvider
        {
            public bool RequiresSocketProtection => false;
            public bool ProtectSocket(long socketHandleOrFd) => true;
        }

        private class MockPlatform : IAcceleratorPlatform
        {
            public string PlatformName { get; set; } = "Windows";
            public string DataDirectory { get; set; }
            public string Cfnat443Directory { get; set; }
            public string Cfnat8443Directory { get; set; }
            public bool SecurityPass { get; set; } = true;

            public IEngineHost EngineHost { get; }
            public ITunnelManager TunnelManager { get; }
            public IProcessSupervisor ProcessSupervisor { get; }
            public ICandidateProbe CandidateProbe { get; }
            public IProtectedSocketProvider ProtectedSocketProvider { get; } = new MockProtectedSocketProvider();

            public MockPlatform(string baseDir, MockEngineHost engineHost, MockTunnelManager tunnelManager, MockCandidateProbe probe)
            {
                DataDirectory = Path.Combine(baseDir, "data");
                Cfnat443Directory = Path.Combine(baseDir, "cfnat443");
                Cfnat8443Directory = Path.Combine(baseDir, "cfnat8443");
                Directory.CreateDirectory(DataDirectory);
                Directory.CreateDirectory(Cfnat443Directory);
                Directory.CreateDirectory(Cfnat8443Directory);

                EngineHost = engineHost;
                TunnelManager = tunnelManager;
                ProcessSupervisor = new MockProcessSupervisor();
                CandidateProbe = probe;
            }

            public bool EnsureSecurityEnvironment() => SecurityPass;
            public void DeployAssets() { }
            public bool IsGameRunning() => false;
            public void LaunchGame() { }
        }

        public static void Run()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "tesl_coord_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var engineHost = new MockEngineHost();
                var tunnelMgr = new MockTunnelManager();
                var probe = new MockCandidateProbe();
                var platform = new MockPlatform(tempDir, engineHost, tunnelMgr, probe);

                var coordinator = new AcceleratorCoordinator(platform);

                var statusList = new List<AcceleratorStatus>();
                var logList = new List<LogEventArgs>();

                coordinator.StatusChanged += (s, e) => statusList.Add(e);
                coordinator.LogReceived += (s, e) => logList.Add(e);

                // 1. 验证启动前状态
                Assert.False(coordinator.IsRunning, "Initially coordinator must not be running");
                Assert.False(coordinator.IsBusy, "Initially coordinator must not be busy");
                Assert.Equal(EngineState.Stopped, coordinator.CurrentStatus.State);

                // 2. 验证正常启动流程
                bool startOk = coordinator.StartAsync().GetAwaiter().GetResult();
                Assert.True(startOk, "StartAsync must succeed");
                Assert.True(coordinator.IsRunning, "IsRunning must be true after start");
                Assert.False(coordinator.IsBusy, "IsBusy must be false after start finishes");
                Assert.Equal(EngineState.Running, coordinator.CurrentStatus.State);
                Assert.True(coordinator.CurrentStatus.IsAccelerating, "CurrentStatus.IsAccelerating must be true");
                Assert.True(engineHost.IsRunning, "EngineHost must be running");
                Assert.True(tunnelMgr.IsRunning, "TunnelManager must be running");
                Assert.Equal(1, engineHost.StartCount, "EngineHost started once");
                Assert.Equal(1, tunnelMgr.StartCount, "TunnelManager started once");

                // 3. 验证幂等启动 (再次启动直接返回 true，不重复启动引擎)
                bool secondStart = coordinator.StartAsync().GetAwaiter().GetResult();
                Assert.True(secondStart, "Second StartAsync must return true immediately");
                Assert.Equal(1, engineHost.StartCount, "EngineHost should not start again");

                // 4. 验证正常停止流程与幂等性
                coordinator.Stop();
                Assert.False(coordinator.IsRunning, "IsRunning must be false after Stop");
                Assert.False(coordinator.IsBusy, "IsBusy must be false after Stop finishes");
                Assert.Equal(EngineState.Stopped, coordinator.CurrentStatus.State);
                Assert.False(coordinator.CurrentStatus.IsAccelerating, "CurrentStatus.IsAccelerating must be false");
                Assert.False(engineHost.IsRunning, "EngineHost must be stopped");
                Assert.False(tunnelMgr.IsRunning, "TunnelManager must be stopped");

                // 再次调用 Stop 不应抛异常且状态保持
                coordinator.Stop();
                Assert.False(coordinator.IsRunning, "Repeated Stop must remain stopped");

                // 5. 验证事件分发
                Assert.True(statusList.Count > 0, "StatusChanged events must have been recorded");
                Assert.True(logList.Count > 0, "LogReceived events must have been recorded");

                // 6. 验证安全检查失败时的防御性中断
                var failPlatform = new MockPlatform(tempDir, new MockEngineHost(), new MockTunnelManager(), new MockCandidateProbe())
                {
                    SecurityPass = false
                };
                var failCoord = new AcceleratorCoordinator(failPlatform);
                bool failStart = failCoord.StartAsync().GetAwaiter().GetResult();
                Assert.False(failStart, "StartAsync must fail when EnsureSecurityEnvironment returns false");
                Assert.False(failCoord.IsRunning, "Coordinator must not be running after security check failure");

                // 7. 验证连通性健康探测超时/失败时的自动回退与状态回滚
                var probeFailPlatform = new MockPlatform(tempDir, new MockEngineHost(), new MockTunnelManager(), new MockCandidateProbe { ProbeResult = false });
                var probeFailCoord = new AcceleratorCoordinator(probeFailPlatform);
                bool probeFailStart = probeFailCoord.StartAsync().GetAwaiter().GetResult();
                Assert.False(probeFailStart, "StartAsync must fail when proxy endpoints probe fails");
                Assert.False(probeFailCoord.IsRunning, "Coordinator must rollback to stopped after probe failure");
                Assert.False(probeFailPlatform.EngineHost.IsRunning, "EngineHost must be rolled back and stopped");
                Assert.False(probeFailPlatform.TunnelManager.IsRunning, "TunnelManager must not be running");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }
}
