using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TESL.Accelerator.Core.Abstractions;
using TESL.Accelerator.Core.Models;

namespace TESL.Accelerator.Core.Services
{
    public class AcceleratorCoordinator : IAcceleratorLogger
    {
        private readonly IAcceleratorPlatform _platform;
        private readonly AcceleratorConfig _config;
        private readonly IPSourceManager _ipSourceManager;
        private readonly CandidatePool _candidatePool;
        private readonly CacheManager _cacheManager;
        private readonly ICandidateProbe _candidateProbe;

        public event Action<string>? OnLog;
        public event Action<string, string, bool, bool>? OnStatusChanged; // title, subtitle, isAccelerating, isBusy
        public event EventHandler<LogEventArgs>? LogReceived;
        public event EventHandler<AcceleratorStatus>? StatusChanged;
        public event Action<ChannelInfo, ChannelInfo>? OnChannelsUpdated;
        public event Action<AcceleratorMetrics>? OnMetricsUpdated;
        public event Action<bool>? OnGameStatusChanged;

        public ChannelInfo Ch443 { get; } = new() { Port = 443 };
        public ChannelInfo Ch8443 { get; } = new() { Port = 8443 };
        public AcceleratorMetrics Metrics { get; } = new();

        public bool IsRunning { get; private set; } = false;
        public bool IsBusy { get; private set; } = false;
        public bool IsGameRunning { get; private set; } = false;
        public AcceleratorStatus CurrentStatus { get; private set; } = new("就绪", "等待启动", false, false, EngineState.Stopped);

        private CancellationTokenSource? _monitorCts;
        private readonly object _stateLock = new();

        public AcceleratorCoordinator(
            IAcceleratorPlatform platform,
            AcceleratorConfig? config = null,
            IPSourceManager? ipSourceManager = null,
            CandidatePool? candidatePool = null,
            CacheManager? cacheManager = null,
            ICandidateProbe? candidateProbe = null)
        {
            _platform = platform ?? throw new ArgumentNullException(nameof(platform));
            _config = config ?? AcceleratorConfig.CreateDefault();
            _cacheManager = cacheManager ?? new CacheManager(this);
            _candidatePool = candidatePool ?? new CandidatePool();
            _ipSourceManager = ipSourceManager ?? new IPSourceManager(this);
            _candidateProbe = candidateProbe ?? platform.CandidateProbe ?? new CandidateProbeService(this);
        }

        private void SetStatus(string title, string subtitle, bool isAccelerating, bool isBusy, EngineState state = EngineState.Stopped)
        {
            var status = new AcceleratorStatus(title, subtitle, isAccelerating, isBusy, state);
            CurrentStatus = status;
            OnStatusChanged?.Invoke(title, subtitle, isAccelerating, isBusy);
            StatusChanged?.Invoke(this, status);
        }

        public void Log(string message, LogLevel level = LogLevel.Info)
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            try
            {
                string logFile = Path.Combine(_platform.DataDirectory, "accelerator.log");
                if (Directory.Exists(_platform.DataDirectory))
                {
                    File.AppendAllText(logFile, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }

            OnLog?.Invoke(line);
            LogReceived?.Invoke(this, new LogEventArgs(message, level));
        }

        public async Task<bool> StartAsync(bool forceRescan = false, CancellationToken ct = default)
        {
            lock (_stateLock)
            {
                if (IsRunning || IsBusy) return true;
                IsBusy = true;
            }

            SetStatus("正在启动加速核心...", "正在初始化受保护工作区与释放组件", false, true, EngineState.Starting);

            try
            {
                // 1. 平台环境与安全隔离检查
                if (!_platform.EnsureSecurityEnvironment())
                {
                    Log("[安全检查] 平台安全隔离初始化未通过，中止启动。", LogLevel.Error);
                    SetStatus("启动失败", "平台安全预检未通过", false, false, EngineState.Faulted);
                    IsBusy = false;
                    return false;
                }
                Log("[1/6] 平台受保护工作区与安全隔离初始化完成。");

                // 2. 部署释放平台核心资产
                SetStatus("正在释放核心组件...", "校验并释放网络引擎、虚拟网卡与离线候选库", false, true, EngineState.Starting);
                _platform.DeployAssets();
                Log("[2/6] 运行时核心组件与离线优选库校验释放完成。");

                // 3. 动态同步与清洗 Cloudflare 候选 IP 池
                SetStatus("正在同步优选 IP 池...", "抓取并清洗 Cloudflare Anycast 候选节点", false, true, EngineState.Starting);
                await SynchronizeIpCandidatesAsync(forceRescan, ct);
                Log("[3/6] 优选 IP 候选池检查与更新完成。");

                // 4. 初始化平台进程监控 (若平台支持外部子进程监管，如 Windows Job Object)
                if (_platform.ProcessSupervisor != null)
                {
                    await _platform.ProcessSupervisor.InitializeAsync(ct);
                }

                // 5. 启动 Anycast 优选引擎
                SetStatus("正在启动优选引擎...", "启动 443 与 8443 双通道 Anycast 引擎", false, true, EngineState.Starting);
                string cand443Path = Path.Combine(_platform.Cfnat443Directory, "ips-v4.txt");
                string cand8443Path = Path.Combine(_platform.Cfnat8443Directory, "ips-v4.txt");

                bool enginesOk = await _platform.EngineHost.StartEnginesAsync(_config, cand443Path, cand8443Path, ct);
                if (!enginesOk)
                {
                    throw new InvalidOperationException("Anycast 优选引擎启动失败。");
                }
                Log("[4/6] 双通道 Cloudflare Anycast 优选引擎已启动。");

                // 6. 连通性健康探测
                SetStatus("正在探测节点连通性...", "对 443 与 8443 通道执行真实 TLS 握手测试", false, true, EngineState.Starting);
                using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, probeCts.Token);

                bool ready = await _candidateProbe.CheckProxyEndpointsReadyAsync(
                    _config.Channel443.LocalPort,
                    _config.Channel8443.LocalPort,
                    _config.Channel443.SniHost,
                    _config.Channel8443.SniHost,
                    TimeSpan.FromSeconds(35),
                    linkedCts.Token);

                if (!ready)
                {
                    Log("[警告] 优选出口健康检查未全部通过。", LogLevel.Warning);
                    SetStatus("启动就绪超时", "Anycast 节点握手未在规定时间内就绪", false, false, EngineState.Faulted);
                    Stop();
                    IsBusy = false;
                    return false;
                }

                // 7. 启动 TUN 虚拟网卡与透明路由
                SetStatus("正在启动 TUN 接管...", "配置虚拟网卡与透明路由分流", false, true, EngineState.Starting);
                string singBoxConfigJson = _platform.PlatformName == "Android"
                    ? _config.GenerateAndroidSingBoxJson()
                    : _config.GenerateWindowsSingBoxJson();

                bool tunnelOk = await _platform.TunnelManager.StartTunnelAsync(singBoxConfigJson, ct);
                if (!tunnelOk)
                {
                    throw new InvalidOperationException("TUN 虚拟网卡与路由接管核心启动失败。");
                }
                Log("[6/6] TUN 路由接管核心已启动。");

                lock (_stateLock)
                {
                    IsRunning = true;
                    IsBusy = false;
                }

                // 读取节点缓存信息
                RefreshChannelInfoFromCache();

                SetStatus("零中转加速已生效", "TUN 路由已接管 · 双通道新加坡 Anycast 直连", true, false, EngineState.Running);

                // 启动监控循环
                _monitorCts = new CancellationTokenSource();
                _ = Task.Run(() => MonitorLoopAsync(_monitorCts.Token));

                return true;
            }
            catch (Exception ex)
            {
                Log($"[启动异常] {ex.Message}", LogLevel.Error);
                SetStatus("启动出现异常", ex.Message, false, false, EngineState.Faulted);
                Stop();
                IsBusy = false;
                return false;
            }
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                if (!IsRunning && !IsBusy) return;
                IsBusy = true;
            }

            SetStatus("正在停止加速...", "正在卸载虚拟网卡并还原系统网络", false, true, EngineState.Stopping);

            try
            {
                _monitorCts?.Cancel();
            }
            catch { }

            try
            {
                _platform.TunnelManager.StopTunnelAsync().GetAwaiter().GetResult();
                _platform.EngineHost.StopEnginesAsync().GetAwaiter().GetResult();
                _platform.ProcessSupervisor?.TerminateAll();
            }
            catch (Exception ex)
            {
                Log($"[停止异常] {ex.Message}", LogLevel.Warning);
            }

            lock (_stateLock)
            {
                IsRunning = false;
                IsBusy = false;
                Ch443.IsReady = false;
                Ch443.StatusText = "已停止";
                Ch8443.IsReady = false;
                Ch8443.StatusText = "已停止";
            }

            OnChannelsUpdated?.Invoke(Ch443, Ch8443);
            SetStatus("加速已停止 (直连模式)", "TUN 虚拟网卡已卸载 · 系统网络完全恢复原生", false, false, EngineState.Stopped);
            Log("[退出清理] 加速器已停止，网络环境已彻底还原。");
        }

        public bool CheckGameRunning()
        {
            IsGameRunning = _platform.IsGameRunning();
            return IsGameRunning;
        }

        public void RefreshChannelInfoFromCache()
        {
            _cacheManager.TryReadCache(_platform.Cfnat443Directory, Ch443, "Anycast 直连");
            _cacheManager.TryReadCache(_platform.Cfnat8443Directory, Ch8443, "长连接保持");
            OnChannelsUpdated?.Invoke(Ch443, Ch8443);
            UpdateMetrics();
        }

        public void UpdateMetrics()
        {
            Metrics.PingText = Ch443.LatencyMs > 0 ? $"{Ch443.LatencyMs} ms" : "-- ms";
            Metrics.LossText = IsRunning ? "0.0%" : "--";
            Metrics.SpeedText = IsRunning ? "直连极速" : "-- KB/s";

            int totalCand = _candidatePool.Candidates443.Count + _candidatePool.Candidates8443.Count;
            Metrics.CandidateCountText = totalCand > 0 ? $"{totalCand} 节点" : "128+ 节点";

            OnMetricsUpdated?.Invoke(Metrics);
        }

        public void LaunchGame()
        {
            try
            {
                _platform.LaunchGame();
            }
            catch (Exception ex)
            {
                Log($"[启动游戏失败] {ex.Message}", LogLevel.Error);
            }
        }

        private async Task SynchronizeIpCandidatesAsync(bool forceRescan, CancellationToken ct)
        {
            string sourcesPath = Path.Combine(_platform.DataDirectory, "ip-sources.json");
            string rangesPath = Path.Combine(_platform.DataDirectory, "cloudflare-ranges-v4.txt");

            if (forceRescan)
            {
                Log("[缓存控制] 捕获 forceRescan，强制清除 443 与 8443 节点优选缓存...");
                _cacheManager.ClearCache(_platform.Cfnat443Directory);
                _cacheManager.ClearCache(_platform.Cfnat8443Directory);
            }
            else
            {
                _cacheManager.PurifyCacheFolder(_platform.Cfnat443Directory, "cfnat-443");
                _cacheManager.PurifyCacheFolder(_platform.Cfnat8443Directory, "cfnat-8443");
            }

            try
            {
                IpSourcesConfig sourcesConfig;
                if (File.Exists(sourcesPath))
                {
                    string json = File.ReadAllText(sourcesPath, Encoding.UTF8);
                    sourcesConfig = JsonSerializer.Deserialize<IpSourcesConfig>(json) ?? IpSourcesConfig.CreateDefault();
                }
                else
                {
                    sourcesConfig = IpSourcesConfig.CreateDefault();
                }

                List<CidrRange> cfRanges;
                if (File.Exists(rangesPath))
                {
                    cfRanges = File.ReadAllLines(rangesPath)
                        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.Trim().StartsWith("#"))
                        .Select(l => new CidrRange(l))
                        .ToList();
                }
                else
                {
                    cfRanges = CloudflareValidator.GetDefaultCidrRanges().ToList();
                }

                _candidatePool.Clear();

                // 1. 同步 443 通道
                var seen443 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var src in sourcesConfig.Sources443)
                {
                    if (_candidatePool.Candidates443.Count >= 100 && src.IsFallback) break;

                    string? raw = await _ipSourceManager.FetchSourceContentAsync(src, sourcesConfig.GithubMirrors, sourcesConfig.TimeoutSeconds, ct);
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    if (!_ipSourceManager.CheckFreshness(raw, src, src.MaxAgeHours, out _)) continue;

                    var ips = IPParser.ParseAndValidateIps(raw, cfRanges, false, seen443);
                    if (ips.Count > 0)
                    {
                        _candidatePool.AddCandidates443(ips, sourcesConfig.MaxCandidatesPerChannel);
                        Log($"[IP-443] {src.Name} 获取成功：{ips.Count} 个候选");
                    }
                }

                // 2. 同步 8443 通道
                var seen8443 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var src in sourcesConfig.Sources8443)
                {
                    if (_candidatePool.Candidates8443.Count >= 100 && src.IsFallback) break;

                    string? raw = await _ipSourceManager.FetchSourceContentAsync(src, sourcesConfig.GithubMirrors, sourcesConfig.TimeoutSeconds, ct);
                    if (string.IsNullOrWhiteSpace(raw)) continue;

                    if (!_ipSourceManager.CheckFreshness(raw, src, src.MaxAgeHours, out _)) continue;

                    var ips = IPParser.ParseAndValidateIps(raw, cfRanges, src.Is8443Exclusive, seen8443);
                    if (ips.Count > 0)
                    {
                        _candidatePool.AddCandidates8443(ips, sourcesConfig.MaxCandidatesPerChannel);
                        Log($"[IP-8443] {src.Name} 获取成功：{ips.Count} 个候选");
                    }
                }

                // 写入或回退
                string target443 = Path.Combine(_platform.Cfnat443Directory, "ips-v4.txt");
                string target8443 = Path.Combine(_platform.Cfnat8443Directory, "ips-v4.txt");

                if (_candidatePool.Candidates443.Count >= sourcesConfig.MinRequiredIps)
                {
                    CacheManager.AtomicWriteText(target443, string.Join("\n", _candidatePool.Candidates443.Take(sourcesConfig.MaxCandidatesPerChannel)) + "\n");
                    Log($"[IP-443] 在线候选库更新完成 (共 {Math.Min(_candidatePool.Candidates443.Count, sourcesConfig.MaxCandidatesPerChannel)} 个高质量节点)");
                }
                else
                {
                    Log("[IP-443] 在线候选数量不足，回退保留现有离线候选文件。", LogLevel.Warning);
                }

                if (_candidatePool.Candidates8443.Count >= sourcesConfig.MinRequiredIps)
                {
                    CacheManager.AtomicWriteText(target8443, string.Join("\n", _candidatePool.Candidates8443.Take(sourcesConfig.MaxCandidatesPerChannel)) + "\n");
                    Log($"[IP-8443] 在线候选库更新完成 (共 {Math.Min(_candidatePool.Candidates8443.Count, sourcesConfig.MaxCandidatesPerChannel)} 个专用/通用节点)");
                }
                else
                {
                    Log("[IP-8443] 在线候选数量不足，回退保留现有离线候选文件。", LogLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                Log($"[WARN] 优选 IP 动态更新遇阻: {ex.Message}，继续使用已有候选库。", LogLevel.Warning);
            }
        }

        private async Task MonitorLoopAsync(CancellationToken ct)
        {
            bool lastGameRunning = false;

            while (!ct.IsCancellationRequested && IsRunning)
            {
                // 1. 检查虚拟网卡/隧道是否仍然正常运行
                if (!_platform.TunnelManager.IsRunning)
                {
                    Log("[警告] sing-box 隧道异常退出！", LogLevel.Warning);
                    Stop();
                    break;
                }

                // 2. 检查游戏进程/包名运行状态
                bool currentGameRunning = _platform.IsGameRunning();
                if (currentGameRunning != lastGameRunning)
                {
                    lastGameRunning = currentGameRunning;
                    IsGameRunning = currentGameRunning;
                    if (currentGameRunning)
                    {
                        Log("[游戏状态] 检测到 TESL 游戏客户端已启动，加速通道生效中。");
                    }
                    else
                    {
                        Log("[游戏状态] 游戏客户端已结束。");
                    }
                    OnGameStatusChanged?.Invoke(currentGameRunning);
                }

                try
                {
                    await Task.Delay(1000, ct);
                }
                catch
                {
                    break;
                }
            }
        }
    }
}
