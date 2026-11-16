using System;
using System.Collections.Generic;
using TESL.Accelerator.Core.Models;
using TESL.Accelerator.Core.Services;

namespace TESL.Accelerator.Tests
{
    public static class IPParserTests
    {
        public static void Run()
        {
            var cfRanges = new List<CidrRange>
            {
                new CidrRange("104.16.0.0/12"),
                new CidrRange("162.159.0.0/16"),
                new CidrRange("172.64.0.0/13")
            };

            // 1. 测试从脏文本、注释、附加端口信息中提取 IP
            string rawContent = @"
# 节点列表更新
104.16.1.1 # 香港优选
104.16.1.2:443
104.16.1.3:8443;专用长连接
162.159.192.1 | 日本东京 | 80ms
172.65.34.1:8443 # 合法 CF 官方 Anycast (172.64.0.0/13)，允许入池
8.8.8.8 # 谷歌公网 (非 CF，应被过滤)
127.0.0.1 (回环应被过滤)
";

            // 测试通用 443 候选池解析 (不需要 8443 过滤)
            var seen443 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ips443 = IPParser.ParseAndValidateIps(rawContent, cfRanges, require8443: false, seen443);

            Assert.Equal(5, ips443.Count, "Should parse exactly 5 valid CF IPs including 172.65.*");
            Assert.Contains("104.16.1.1", string.Join(",", ips443));
            Assert.Contains("104.16.1.2", string.Join(",", ips443));
            Assert.Contains("104.16.1.3", string.Join(",", ips443));
            Assert.Contains("162.159.192.1", string.Join(",", ips443));
            Assert.Contains("172.65.34.1", string.Join(",", ips443));
            Assert.DoesNotContain("8.8.8.8", string.Join(",", ips443));
            Assert.DoesNotContain("127.0.0.1", string.Join(",", ips443));

            // 测试 8443 专用候选池解析 (只保留明确指定 8443 端口的节点)
            var seen8443 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ips8443 = IPParser.ParseAndValidateIps(rawContent, cfRanges, require8443: true, seen8443);

            Assert.Equal(2, ips8443.Count, "Should parse exactly 2 valid 8443 CF IPs");
            Assert.Contains("104.16.1.3", string.Join(",", ips8443));
            Assert.Contains("172.65.34.1", string.Join(",", ips8443));

            // 2. 测试时间戳提取
            string textWithTs1 = "更新于: 2026-11-15 14:30:00\n104.16.1.1";
            var ts1 = IPParser.ParseTimestamp(textWithTs1);
            Assert.True(ts1.HasValue, "Should parse standard timestamp");
            Assert.Equal(2026, ts1!.Value.Year);
            Assert.Equal(11, ts1.Value.Month);
            Assert.Equal(15, ts1.Value.Day);
            Assert.Equal(14, ts1.Value.Hour);
            Assert.Equal(30, ts1.Value.Minute);

            string textWithTs2 = "# Upd20261114_08:15\n104.16.1.1";
            var ts2 = IPParser.ParseTimestamp(textWithTs2);
            Assert.True(ts2.HasValue, "Should parse Upd timestamp");
            Assert.Equal(2026, ts2!.Value.Year);
            Assert.Equal(11, ts2.Value.Month);
            Assert.Equal(14, ts2.Value.Day);
            Assert.Equal(8, ts2.Value.Hour);
            Assert.Equal(15, ts2.Value.Minute);
        }
    }
}
