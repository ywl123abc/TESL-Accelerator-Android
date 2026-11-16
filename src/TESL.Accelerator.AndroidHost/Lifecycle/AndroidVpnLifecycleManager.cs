using System;
using System.Threading;
using System.Threading.Tasks;

namespace TESL.Accelerator.AndroidHost.Lifecycle
{
    public enum AndroidVpnState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
        Faulted
    }

    /// <summary>
    /// Android VpnService 生命周期、并发与停机时序协调器。
    /// 【统一停机 8 步规范】：
    /// 1. 阻止新的启动和流量请求 (状态转移为 Stopping，IsRevoked 置为 true)。
    /// 2. 停止 sing-box，等待其完全退出。
    /// 3. 停止 443 和 8443 两个 cfnat 实例。
    /// 4. 等待所有 Worker、健康检查线程和在途 Protect 回调退出。
    /// 5. destroy cfnat 实例。
    /// 6. 释放相关 Delegate 和 GCHandle。
    /// 7. 关闭 masterPfd。
    /// 8. StopForeground / StopSelf。
    /// 正常停止、启动失败回滚和 OnRevoke 必须复用幂等清理路径。
    /// </summary>
    public class AndroidVpnLifecycleManager
    {
        private readonly SemaphoreSlim _syncLock = new(1, 1);
        private readonly object _stateLock = new();
        private volatile bool _isRevoked;

        public AndroidVpnState CurrentState { get; private set; } = AndroidVpnState.Stopped;
        public int ActiveMasterFd { get; private set; } = -1;
        public bool IsRunning => CurrentState == AndroidVpnState.Running;
        public bool IsBusy => CurrentState == AndroidVpnState.Starting || CurrentState == AndroidVpnState.Stopping;
        public bool IsRevoked => _isRevoked;

        public event Action<AndroidVpnState, string>? OnStateChanged;

        public async Task<bool> StartAsync(
            Func<int> establishTunAction,
            Func<Task<bool>> startCoordinatorAction,
            Func<Task>? rollbackCoordinatorAction = null,
            Action? rollbackTunAction = null,
            Action? rollbackForegroundAction = null)
        {
            await _syncLock.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_stateLock)
                {
                    if (_isRevoked)
                    {
                        OnStateChanged?.Invoke(AndroidVpnState.Faulted, "VPN 权限已被系统撤销，禁止发起新启动。");
                        return false;
                    }

                    if (CurrentState == AndroidVpnState.Running || CurrentState == AndroidVpnState.Starting)
                    {
                        // 幂等防御：已处于运行或启动中，直接返回
                        return true;
                    }
                    CurrentState = AndroidVpnState.Starting;
                }

                OnStateChanged?.Invoke(AndroidVpnState.Starting, "正在启动前台 VPN 服务与配置 TUN...");

                // 1. 建立系统 TUN 虚拟网卡并取得主描述符 (此时 Protect 权限已开放)
                int masterFd = establishTunAction();
                if (masterFd < 0)
                {
                    lock (_stateLock)
                    {
                        CurrentState = AndroidVpnState.Faulted;
                        ActiveMasterFd = -1;
                    }
                    OnStateChanged?.Invoke(AndroidVpnState.Faulted, "系统 TUN 创建失败 (masterPfd < 0)");
                    return false;
                }

                lock (_stateLock)
                {
                    ActiveMasterFd = masterFd;
                }

                // 2. 启动核心协调器 (初始化双通道 cfnat 引擎与 sing-box libbox 路由接管)
                bool coordOk = false;
                try
                {
                    coordOk = await startCoordinatorAction().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    OnStateChanged?.Invoke(AndroidVpnState.Faulted, $"协调器启动异常: {ex.Message}");
                }

                if (!coordOk)
                {
                    OnStateChanged?.Invoke(AndroidVpnState.Faulted, "Core 业务协调器启动失败，触发统一回滚清理");
                    
                    // 启动失败时，严格复用统一清理路径回滚已分配的 TUN 和服务
                    await ExecuteStopInternalAsync(rollbackCoordinatorAction, rollbackTunAction, rollbackForegroundAction).ConfigureAwait(false);

                    lock (_stateLock)
                    {
                        CurrentState = AndroidVpnState.Faulted;
                    }
                    return false;
                }

                lock (_stateLock)
                {
                    CurrentState = AndroidVpnState.Running;
                }
                OnStateChanged?.Invoke(AndroidVpnState.Running, "VPN 服务已激活，双通道直连运行中");
                return true;
            }
            finally
            {
                _syncLock.Release();
            }
        }

        public async Task StopAsync(
            Func<Task> stopCoordinatorAction,
            Action closeTunAction,
            Action stopForegroundAction)
        {
            await _syncLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await ExecuteStopInternalAsync(stopCoordinatorAction, closeTunAction, stopForegroundAction).ConfigureAwait(false);
            }
            finally
            {
                _syncLock.Release();
            }
        }

        public async Task HandleRevokeAsync(
            Func<Task> stopCoordinatorAction,
            Action closeTunAction,
            Action stopForegroundAction)
        {
            // 标记已撤销：立即切断 Protect 权限，阻止后续任何外部新出站发起
            _isRevoked = true;

            await _syncLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await ExecuteStopInternalAsync(stopCoordinatorAction, closeTunAction, stopForegroundAction).ConfigureAwait(false);
            }
            finally
            {
                _syncLock.Release();
            }
        }

        public void HandleRevoke(
            Func<Task> stopCoordinatorAction,
            Action closeTunAction,
            Action stopForegroundAction,
            Action<Exception>? onException = null)
        {
            // 标记系统已撤销：立即切断 Protect 权限
            _isRevoked = true;

            // 无论在 Android 主线程还是 Binder 线程被触发，均通过 Task.Run 在后台线程池安全调度，
            // 严格共享 _syncLock，杜绝主线程阻塞与死锁，并强制观察并记录异常
            try
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await _syncLock.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            await ExecuteStopInternalAsync(stopCoordinatorAction, closeTunAction, stopForegroundAction).ConfigureAwait(false);
                        }
                        finally
                        {
                            _syncLock.Release();
                        }
                    }
                    catch (Exception ex)
                    {
                        onException?.Invoke(ex);
                    }
                }).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                onException?.Invoke(ex);
            }
        }

        /// <summary>
        /// 统一停机核心内部实现 (必须在持有 _syncLock 时调用)。
        /// 严格执行统一停机 8 步规范。
        /// </summary>
        private async Task ExecuteStopInternalAsync(
            Func<Task>? stopCoordinatorAction,
            Action? closeTunAction,
            Action? stopForegroundAction)
        {
            lock (_stateLock)
            {
                if (CurrentState == AndroidVpnState.Stopped || CurrentState == AndroidVpnState.Stopping)
                {
                    // 幂等防御：已处于停止或停止中，直接返回
                    return;
                }
                // 步骤 1: 阻止新的启动和流量请求 (状态置为 Stopping)
                CurrentState = AndroidVpnState.Stopping;
            }

            OnStateChanged?.Invoke(AndroidVpnState.Stopping, "正在停止加速并按 8 步规范回收资源...");

            try
            {
                // 步骤 2 ~ 6: 由 Coordinator / EngineHost 统一执行:
                //   步骤 2: 停止 sing-box，等待其完全退出
                //   步骤 3: 停止 443 和 8443 两个 cfnat 实例
                //   步骤 4: 等待所有 Worker、健康检查线程和在途 Protect 回调退出
                //   步骤 5: destroy cfnat 实例
                //   步骤 6: 释放相关 Delegate 和 GCHandle
                if (stopCoordinatorAction != null)
                {
                    try
                    {
                        await stopCoordinatorAction().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        OnStateChanged?.Invoke(AndroidVpnState.Stopping, $"Coordinator 停止阶段警告: {ex.Message}");
                    }
                }

                // 步骤 7: 确保 Coordinator 与 cfnat 已彻底退出后，关闭并释放宿主 masterPfd
                if (closeTunAction != null)
                {
                    try
                    {
                        closeTunAction();
                    }
                    catch (Exception ex)
                    {
                        OnStateChanged?.Invoke(AndroidVpnState.Stopping, $"关闭 masterPfd 警告: {ex.Message}");
                    }
                }

                // 步骤 8: StopForeground / StopSelf
                if (stopForegroundAction != null)
                {
                    try
                    {
                        stopForegroundAction();
                    }
                    catch (Exception ex)
                    {
                        OnStateChanged?.Invoke(AndroidVpnState.Stopping, $"停止前台服务警告: {ex.Message}");
                    }
                }
            }
            finally
            {
                lock (_stateLock)
                {
                    CurrentState = AndroidVpnState.Stopped;
                    ActiveMasterFd = -1;
                }
                OnStateChanged?.Invoke(AndroidVpnState.Stopped, "VPN 服务已完全停止，网络已还原");
            }
        }
    }
}
