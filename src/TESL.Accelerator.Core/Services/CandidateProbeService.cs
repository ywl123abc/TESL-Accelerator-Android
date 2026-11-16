using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using TESL.Accelerator.Core.Abstractions;
using TESL.Accelerator.Core.Models;

namespace TESL.Accelerator.Core.Services
{
    public class CandidateProbeService : ICandidateProbe
    {
        private readonly IAcceleratorLogger? _logger;
        private readonly ConcurrentQueue<NodeProbeFailureRecord> _failureRecords = new();

        public IReadOnlyList<NodeProbeFailureRecord> FailureRecords => _failureRecords.ToArray();

        public CandidateProbeService(IAcceleratorLogger? logger = null)
        {
            _logger = logger;
        }

        public void RecordFailure(string ip, string targetSni, int targetPort, string channel, string result)
        {
            var record = new NodeProbeFailureRecord
            {
                Ip = ip,
                TargetSni = targetSni,
                TargetPort = targetPort,
                Channel = channel,
                Result = result,
                Timestamp = DateTime.UtcNow
            };
            _failureRecords.Enqueue(record);
            while (_failureRecords.Count > 500 && _failureRecords.TryDequeue(out _)) { }

            _logger?.Log($"[探测失败记录] {record}", LogLevel.Warning);
        }

        public async Task<bool> ProbeTlsHandshakeAsync(
            string host,
            int port,
            string sniHost,
            int timeoutMs,
            CancellationToken ct = default)
        {
            try
            {
                using var tcpClient = new TcpClient();
                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                await tcpClient.ConnectAsync(host, port, linkedCts.Token);

                using var sslStream = new SslStream(
                    tcpClient.GetStream(),
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
                string channel = port == 8443 || port == 18443 ? "8443" : "443";
                RecordFailure(host, sniHost, port, channel, $"TLS Handshake Failed: {ex.Message}");
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
                bool tls443Ok = await ProbeTlsHandshakeAsync("127.0.0.1", localPort443, sni443, 3000, ct);
                bool tls8443Ok = await ProbeTlsHandshakeAsync("127.0.0.1", localPort8443, sni8443, 3000, ct);

                if (tls443Ok && tls8443Ok)
                {
                    _logger?.Log($"[优选就绪] 双通道真实 TLS 握手均成功验证！(耗时: {sw.Elapsed.TotalSeconds:F1}s)", LogLevel.Info);
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
