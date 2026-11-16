using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TESL.Accelerator.Core.Abstractions;
using TESL.Accelerator.Core.Models;

namespace TESL.Accelerator.Core.Services
{
    public class IPSourceManager
    {
        private readonly IAcceleratorLogger _logger;
        private readonly HttpClient _httpClient;

        public IPSourceManager(IAcceleratorLogger logger, HttpClient? httpClient = null)
        {
            _logger = logger;
            _httpClient = httpClient ?? new HttpClient();
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TESL-Accelerator/2.0");
            }
        }

        public async Task<string?> FetchSourceContentAsync(
            IpSourceItem source,
            IReadOnlyList<string> githubMirrors,
            int timeoutSeconds,
            CancellationToken ct = default)
        {
            var urls = new List<string>();
            if (source.IsGithub && source.Url.StartsWith("https://raw.githubusercontent.com/"))
            {
                string path = source.Url.Substring("https://raw.githubusercontent.com/".Length);
                foreach (var m in githubMirrors)
                {
                    if (m.EndsWith("/")) urls.Add(m + path);
                    else urls.Add(m + "/" + path);
                }
            }
            urls.Add(source.Url);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            foreach (var url in urls)
            {
                try
                {
                    using var resp = await _httpClient.GetAsync(url, linkedCts.Token);
                    if (!resp.IsSuccessStatusCode) continue;

                    string body = await resp.Content.ReadAsStringAsync(linkedCts.Token);
                    if (string.IsNullOrWhiteSpace(body) ||
                        body.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                        body.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                        body.Contains("404: Not Found", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return body;
                }
                catch
                {
                    // 尝试下一个 mirror 节点
                }
            }

            return null;
        }

        public bool CheckFreshness(string rawContent, IpSourceItem source, int maxAgeHours, out double detectedAgeHours)
        {
            detectedAgeHours = 0;
            if (!source.CheckTimestamp) return true;

            var ts = IPParser.ParseTimestamp(rawContent);
            if (ts.HasValue)
            {
                detectedAgeHours = (DateTime.UtcNow - ts.Value).TotalHours;
                if (detectedAgeHours > maxAgeHours || detectedAgeHours < -24)
                {
                    _logger.Log($"  [{source.Name}] 数据已过期 ({detectedAgeHours:F1} 小时前更新)，跳过。", LogLevel.Warning);
                    return false;
                }
            }

            return true;
        }
    }
}
