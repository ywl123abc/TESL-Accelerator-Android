using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using TESL.Accelerator.Core.Models;

namespace TESL.Accelerator.Core.Services
{
    public static class IPParser
    {
        /// <summary>
        /// 解析第三方源文本中可能附带的更新时间戳
        /// </summary>
        public static DateTime? ParseTimestamp(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            try
            {
                using var reader = new StringReader(text);
                for (int i = 0; i < 10; i++)
                {
                    string? l = reader.ReadLine();
                    if (l == null) break;

                    // 1. 标准格式: 2026-11-15 14:30(:00) 或 2026/11/15 14:30
                    var m1 = Regex.Match(l, @"\b(20\d{2})[-/](\d{1,2})[-/](\d{1,2})[ T](\d{1,2}):(\d{2})(?::(\d{2}))?\b");
                    if (m1.Success)
                    {
                        int yr = int.Parse(m1.Groups[1].Value);
                        int mo = int.Parse(m1.Groups[2].Value);
                        int dy = int.Parse(m1.Groups[3].Value);
                        int hr = int.Parse(m1.Groups[4].Value);
                        int mi = int.Parse(m1.Groups[5].Value);
                        int sc = m1.Groups[6].Success ? int.Parse(m1.Groups[6].Value) : 0;
                        return new DateTime(yr, mo, dy, hr, mi, sc, DateTimeKind.Utc);
                    }

                    // 2. Upd 格式: Upd20261115_14:30
                    var m2 = Regex.Match(l, @"Upd(20\d{2})(\d{2})(\d{2})_(\d{1,2}):(\d{2})");
                    if (m2.Success)
                    {
                        int yr = int.Parse(m2.Groups[1].Value);
                        int mo = int.Parse(m2.Groups[2].Value);
                        int dy = int.Parse(m2.Groups[3].Value);
                        int hr = int.Parse(m2.Groups[4].Value);
                        int mi = int.Parse(m2.Groups[5].Value);
                        return new DateTime(yr, mo, dy, hr, mi, 0, DateTimeKind.Utc);
                    }

                    // 3. 短格式: 11-15 14:30
                    var m3 = Regex.Match(l, @"\b(\d{1,2})-(\d{1,2})\s+(\d{1,2}):(\d{2})\b");
                    if (m3.Success)
                    {
                        int yr = DateTime.UtcNow.Year;
                        int mo = int.Parse(m3.Groups[1].Value);
                        int dy = int.Parse(m3.Groups[2].Value);
                        int hr = int.Parse(m3.Groups[3].Value);
                        int mi = int.Parse(m3.Groups[4].Value);
                        return new DateTime(yr, mo, dy, hr, mi, 0, DateTimeKind.Utc);
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// 从原始文本提取并过滤出合法的 IPv4 候选节点列表
        /// </summary>
        public static List<string> ParseAndValidateIps(
            string content,
            IReadOnlyList<CidrRange>? cfRanges = null,
            bool require8443 = false,
            ISet<string>? seen = null)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(content)) return result;

            var ranges = cfRanges ?? CloudflareValidator.GetDefaultCidrRanges();
            seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim().Trim('\uFEFF');
                if (string.IsNullOrWhiteSpace(line) ||
                    line.StartsWith("#") ||
                    line.StartsWith("//") ||
                    line.StartsWith(";"))
                {
                    continue;
                }

                // 支持以空格、制表符、竖线或分号分隔的附加信息（如 IP#地区 或 IP 8443）
                int sepIdx = line.IndexOfAny(new[] { '#', ' ', '\t', '|', ';' });
                string core = sepIdx >= 0 ? line.Substring(0, sepIdx).Trim() : line;
                if (string.IsNullOrWhiteSpace(core)) continue;

                string ipCandidate = core;
                int port = -1;

                int colonIdx = core.LastIndexOf(':');
                if (colonIdx >= 0)
                {
                    string portStr = core.Substring(colonIdx + 1);
                    if (int.TryParse(portStr, out int p)) port = p;
                    ipCandidate = core.Substring(0, colonIdx);
                }

                if (require8443 && port != 8443) continue;

                if (IPAddress.TryParse(ipCandidate, out var ipAddr) &&
                    ipAddr.AddressFamily == AddressFamily.InterNetwork)
                {
                    if (CloudflareValidator.BelongsToCloudflare(ipAddr, ranges))
                    {
                        if (seen.Add(ipCandidate))
                        {
                            result.Add(ipCandidate);
                        }
                    }
                }
            }

            return result;
        }
    }
}
