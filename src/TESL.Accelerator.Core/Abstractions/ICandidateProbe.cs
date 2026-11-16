using System;
using System.Threading;
using System.Threading.Tasks;

namespace TESL.Accelerator.Core.Abstractions
{
    public interface ICandidateProbe
    {
        /// <summary>
        /// 对指定的主机端口进行 TLS ClientHello / ServerHello 握手连通性与证书探测
        /// </summary>
        Task<bool> ProbeTlsHandshakeAsync(string host, int port, string sniHost, int timeoutMs, CancellationToken ct = default);

        /// <summary>
        /// 等待 443 与 8443 本地中继与出口节点全部就绪
        /// </summary>
        Task<bool> CheckProxyEndpointsReadyAsync(int localPort443, int localPort8443, string sni443, string sni8443, TimeSpan timeout, CancellationToken ct = default);
    }
}
