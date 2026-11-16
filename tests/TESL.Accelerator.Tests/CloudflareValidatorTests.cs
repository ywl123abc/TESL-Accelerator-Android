using System.Collections.Generic;
using System.Net;
using TESL.Accelerator.Core.Models;
using TESL.Accelerator.Core.Services;

namespace TESL.Accelerator.Tests
{
    public static class CloudflareValidatorTests
    {
        public static void Run()
        {
            // 1. 私网/回环/组播/保留/CGNAT 地址必须被拒绝
            Assert.False(CloudflareValidator.IsPublicIpv4(IPAddress.Parse("127.0.0.1")), "127.0.0.1 must be rejected");
            Assert.False(CloudflareValidator.IsPublicIpv4(IPAddress.Parse("10.0.0.1")), "10.0.0.1 must be rejected");
            Assert.False(CloudflareValidator.IsPublicIpv4(IPAddress.Parse("192.168.1.1")), "192.168.1.1 must be rejected");
            Assert.False(CloudflareValidator.IsPublicIpv4(IPAddress.Parse("172.16.0.1")), "172.16.0.1 must be rejected");
            Assert.False(CloudflareValidator.IsPublicIpv4(IPAddress.Parse("172.31.255.254")), "172.31.255.254 must be rejected");
            Assert.False(CloudflareValidator.IsPublicIpv4(IPAddress.Parse("169.254.1.1")), "169.254.1.1 must be rejected");
            Assert.False(CloudflareValidator.IsPublicIpv4(IPAddress.Parse("100.64.0.1")), "100.64.0.1 CGNAT must be rejected");
            Assert.False(CloudflareValidator.IsPublicIpv4(IPAddress.Parse("224.0.0.1")), "224.0.0.1 multicast must be rejected");
            Assert.False(CloudflareValidator.IsPublicIpv4(IPAddress.Parse("240.0.0.1")), "240.0.0.1 reserved must be rejected");

            // 2. 172.65.0.0/16 属于合法公网 IPv4，必须允许通过公网校验
            Assert.True(CloudflareValidator.IsPublicIpv4(IPAddress.Parse("172.65.251.100")), "172.65.251.100 is public IPv4");
            Assert.True(CloudflareValidator.IsPublicIpv4(IPAddress.Parse("172.65.1.1")), "172.65.1.1 is public IPv4");
            Assert.True(CloudflareValidator.IsPublicIpv4(IPAddress.Parse("104.16.1.1")), "104.16.1.1 is public IPv4");
            Assert.True(CloudflareValidator.IsPublicIpv4(IPAddress.Parse("1.1.1.1")), "1.1.1.1 is public IPv4");

            // 3. Cloudflare 官方网段归属校验
            var cfRanges = new List<CidrRange>
            {
                new CidrRange("104.16.0.0/12"),
                new CidrRange("1.1.1.0/24"),
                new CidrRange("172.64.0.0/13") // 覆盖 172.64.0.0 - 172.71.255.255
            };

            // 3.1 172.65.x.x 归属于 Cloudflare 官方 CIDR (172.64.0.0/13)，必须成功通过归属校验
            Assert.True(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("172.65.1.1"), cfRanges), "172.65.1.1 must pass CF CIDR check");
            Assert.True(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("172.65.251.100"), cfRanges), "172.65.251.100 must pass CF CIDR check");

            // 3.2 其他官方网段地址必须正常通过
            Assert.True(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("104.16.123.45"), cfRanges), "104.16.123.45 in CF range");
            Assert.True(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("1.1.1.1"), cfRanges), "1.1.1.1 in CF range");
            Assert.True(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("172.64.32.1"), cfRanges), "172.64.32.1 in CF range");
            Assert.True(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("172.70.1.1"), cfRanges), "172.70.1.1 in CF range");

            // 3.3 非 Cloudflare 公网 IP 必须被拒绝
            Assert.False(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("8.8.8.8"), cfRanges), "8.8.8.8 not in CF range");
            Assert.False(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("114.114.114.114"), cfRanges), "114.114.114.114 not in CF range");
            Assert.False(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("172.72.1.1"), cfRanges), "172.72.1.1 outside 172.64.0.0/13 must be rejected");

            // 3.4 私网与非法地址即使假定 CIDR 命中也必须被拒绝 (IsPublicIpv4 前置守卫)
            Assert.False(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("127.0.0.1"), cfRanges), "127.0.0.1 loopback must be rejected");
            Assert.False(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("10.0.0.1"), cfRanges), "10.0.0.1 private must be rejected");

            // 3.5 IPv4 CIDR 首尾边界精确校验 (172.64.0.0/13: 覆盖 172.64.0.0 至 172.71.255.255)
            // 首边界测试: 172.64.0.0 (网段首地址命中), 172.63.255.255 (首地址前一位，必须拒绝)
            Assert.True(cfRanges[2].Contains(IPAddress.Parse("172.64.0.0")), "172.64.0.0 is exact head of 172.64.0.0/13");
            Assert.False(cfRanges[2].Contains(IPAddress.Parse("172.63.255.255")), "172.63.255.255 is just before head, must be false");
            // 尾边界测试: 172.71.255.255 (网段尾广播地址命中), 172.72.0.0 (尾地址后一位，必须拒绝)
            Assert.True(cfRanges[2].Contains(IPAddress.Parse("172.71.255.255")), "172.71.255.255 is exact tail of 172.64.0.0/13");
            Assert.False(cfRanges[2].Contains(IPAddress.Parse("172.72.0.0")), "172.72.0.0 is just after tail, must be false");

            // 4. 默认内置 15 个官方网段与无参数归属校验
            var defaultRanges = CloudflareValidator.GetDefaultCidrRanges();
            Assert.Equal(15, defaultRanges.Count, "Cloudflare official IPv4 CIDR count must be 15");
            Assert.True(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("104.16.1.1")), "104.16.1.1 must pass default ranges");
            Assert.True(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("172.65.32.1")), "172.65.32.1 must pass default ranges");
            Assert.False(CloudflareValidator.BelongsToCloudflare(IPAddress.Parse("8.8.8.8")), "8.8.8.8 must fail default ranges");

            // 5. 48 小时时效性校验 (47.9h 合法 vs 48.1h 过期 vs 未来的异常时间戳)
            Assert.True(CloudflareValidator.IsTimestampFresh(System.DateTime.UtcNow.AddHours(-1)), "1 hour ago is fresh");
            Assert.True(CloudflareValidator.IsTimestampFresh(System.DateTime.UtcNow.AddHours(-47.9)), "47.9 hours ago is fresh");
            Assert.False(CloudflareValidator.IsTimestampFresh(System.DateTime.UtcNow.AddHours(-48.1)), "48.1 hours ago must be expired");
            Assert.False(CloudflareValidator.IsTimestampFresh(System.DateTime.UtcNow.AddHours(25)), "Future > 24h must be rejected");
        }
    }
}
