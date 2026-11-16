using System;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using TESL.Accelerator.Core.Abstractions;

namespace TESL.Accelerator.AndroidHost.Adapters
{
    /// <summary>
    /// Android 平台 TLS 节点连通性与健康探测器。
    /// 在发起真实 TLS 握手前，调用 IProtectedSocketProvider.ProtectSocket(fd)，
    /// 确保探测流量绕过 TUN 虚拟网卡直接走物理网络。
    /// </summary>
    public class AndroidCandidateProbe : ICandidateProbe
    {
        private readonly IProtectedSocketProvider _socketProvider;
        private readonly IAcceleratorLogger _logger;

        public AndroidCandidateProbe(IProtectedSocketProvider socketProvider, IAcceleratorLogger logger)
        {
            _socketProvider = socketProvider ?? throw new ArgumentNullException(nameof(socketProvider));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<bool> ProbeTlsHandshakeAsync(string host, int port, string sniHost, int timeoutMs, CancellationToken ct = default)
        {
            Socket? socket = null;
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                    ReceiveTimeout = timeoutMs,
                    SendTimeout = timeoutMs
                };

                // 【核心保护】：在出站连接前执行 Socket 保护
                long handle = socket.Handle.ToInt64();
                if (_socketProvider.RequiresSocketProtection)
                {
                    bool protectedOk = _socketProvider.ProtectSocket(handle);
                    if (!protectedOk)
                    {
                        _logger.Log($"[AndroidCandidateProbe] 套接字 protect 失败，拒绝发起探测连接至 {host}:{port}", LogLevel.Warning);
                        return false;
                    }
                }

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                // 异步连接
                await socket.ConnectAsync(host, port, linkedCts.Token);

                using var netStream = new NetworkStream(socket, ownsSocket: false);
                using var sslStream = new SslStream(netStream, false, (sender, cert, chain, errors) => true);

                var sslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = sniHost,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                };

                await sslStream.AuthenticateAsClientAsync(sslOptions, linkedCts.Token);
                return sslStream.IsAuthenticated;
            }
            catch (Exception ex)
            {
                _logger.Log($"[AndroidCandidateProbe] 探测 {host}:{port} ({sniHost}) 失败: {ex.Message}", LogLevel.Debug);
                return false;
            }
            finally
            {
                try
                {
                    socket?.Dispose();
                }
                catch { }
            }
        }

        public async Task<bool> CheckProxyEndpointsReadyAsync(int localPort443, int localPort8443, string sni443, string sni8443, TimeSpan timeout, CancellationToken ct = default)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                try
                {
                    using var tcp443 = new TcpClient();
                    var task443 = tcp443.ConnectAsync(IPAddress.Loopback, localPort443);

                    using var tcp8443 = new TcpClient();
                    var task8443 = tcp8443.ConnectAsync(IPAddress.Loopback, localPort8443);

                    await Task.WhenAll(task443, task8443);

                    if (tcp443.Connected && tcp8443.Connected)
                    {
                        return true;
                    }
                }
                catch
                {
                    // 本地端口尚未就绪，重试
                }

                await Task.Delay(300, ct);
            }

            return false;
        }
    }
}
