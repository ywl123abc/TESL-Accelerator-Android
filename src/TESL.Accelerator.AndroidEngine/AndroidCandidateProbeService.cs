using System;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using TESL.Accelerator.Core.Abstractions;
using TESL.Accelerator.Core.Models;

namespace TESL.Accelerator.AndroidEngine
{
    /// <summary>
    /// Android 专用候选节点探测服务：
    /// 必须在 Socket 发起 Connect 之前，将其底层 Linux fd 通过 VpnService.protect() 注册到底层网络，
    /// 严格防止 TLS 探测数据包回环注入 TUN 导致路由死循环。
    /// </summary>
    public class AndroidCandidateProbeService : ICandidateProbe
    {
        private readonly IProtectedSocketProvider _protectedSocketProvider;
        private readonly IAcceleratorLogger? _logger;

        public AndroidCandidateProbeService(
            IProtectedSocketProvider protectedSocketProvider,
            IAcceleratorLogger? logger = null)
        {
            _protectedSocketProvider = protectedSocketProvider ?? throw new ArgumentNullException(nameof(protectedSocketProvider));
            _logger = logger;
        }

        public async Task<bool> ProbeTlsHandshakeAsync(
            string host,
            int port,
            string sniHost,
            int timeoutMs,
            CancellationToken ct = default)
        {
            Socket? socket = null;
            try
            {
                // 1. 创建底层 TCP 原始 Socket (获取 Linux 原生 fd)
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.NoDelay = true;

                // 2. 关键防护：在连接之前，调用 Android VpnService.protect(fd) 避开 VPN 路由
                int nativeFd = (int)socket.Handle;
                bool isProtected = _protectedSocketProvider.ProtectSocket(nativeFd);
                if (!isProtected)
                {
                    _logger?.Log($"[Android-Probe-Warning] Socket fd={nativeFd} protect 失败，可能存在回环风险！", LogLevel.Warning);
                }

                // 3. 执行连接 (直连底层物理接口 Wi-Fi / Cellular)
                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                if (IPAddress.TryParse(host, out var ipAddr))
                {
                    await socket.ConnectAsync(new IPEndPoint(ipAddr, port), linkedCts.Token);
                }
                else
                {
                    await socket.ConnectAsync(host, port, linkedCts.Token);
                }

                // 4. 包装并执行客户端 TLS 握手 (验证证书与 SNI 连通性)
                using var netStream = new NetworkStream(socket, ownsSocket: true);
                using var sslStream = new SslStream(
                    netStream,
                    leaveInnerStreamOpen: false,
                    userCertificateValidationCallback: (sender, certificate, chain, sslPolicyErrors) => true);

                var sslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = sniHost,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                };

                await sslStream.AuthenticateAsClientAsync(sslOptions, linkedCts.Token);
                return sslStream.IsAuthenticated;
            }
            catch (Exception ex)
            {
                _logger?.Log($"[Android-Probe-Fail] {host}:{port} (SNI:{sniHost}) 探测失败: {ex.Message}", LogLevel.Debug);
                socket?.Dispose();
                return false;
            }
        }

        public async Task<bool> CheckProxyEndpointsReadyAsync(
            int localPort443,
            int localPort8443,
            string sni443,
            string sni8443,
            TimeSpan timeout,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();

            while (!ct.IsCancellationRequested && sw.Elapsed < timeout)
            {
                // 本地 127.0.0.1 环回代理端口连通性检查
                bool tls443Ok = await ProbeTlsHandshakeAsync("127.0.0.1", localPort443, sni443, 3000, ct);
                bool tls8443Ok = await ProbeTlsHandshakeAsync("127.0.0.1", localPort8443, sni8443, 3000, ct);

                if (tls443Ok && tls8443Ok)
                {
                    _logger?.Log($"[Android-Ready] 双通道 TLS 握手就绪！(耗时: {sw.Elapsed.TotalSeconds:F1}s)", LogLevel.Info);
                    return true;
                }

                try
                {
                    await Task.Delay(500, ct);
                }
                catch
                {
                    break;
                }
            }

            return false;
        }
    }
}
