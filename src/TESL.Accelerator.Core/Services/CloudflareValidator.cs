using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using TESL.Accelerator.Core.Models;

namespace TESL.Accelerator.Core.Services
{
    public static class CloudflareValidator
    {
        public const int DefaultMaxAgeHours = 48;

        /// <summary>
        /// Cloudflare 官方公布的 15 个 IPv4 CIDR 网段
        /// </summary>
        public static readonly IReadOnlyList<string> DefaultCidrStrings = new[]
        {
            "173.245.48.0/20",
            "103.21.244.0/22",
            "103.22.200.0/22",
            "103.31.4.0/22",
            "141.101.64.0/18",
            "108.162.192.0/18",
            "190.93.240.0/20",
            "188.114.96.0/20",
            "197.234.240.0/22",
            "198.41.128.0/17",
            "162.158.0.0/15",
            "104.16.0.0/13",
            "104.24.0.0/14",
            "172.64.0.0/13",
            "131.0.72.0/22"
        };

        private static readonly Lazy<IReadOnlyList<CidrRange>> _defaultRanges = new(() =>
            DefaultCidrStrings.Select(c => new CidrRange(c)).ToList());

        public static IReadOnlyList<CidrRange> GetDefaultCidrRanges() => _defaultRanges.Value;

        /// <summary>
        /// 严格验证是否为公网合法的 IPv4 地址 (过滤私网、回环、链路本地、组播及保留地址)
        /// 172.65.0.0/16 归属于 Cloudflare 官方 172.64.0.0/13 Anycast 范围，属于合法公网地址
        /// </summary>
        public static bool IsPublicIpv4(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 4) return false;

            // 0.0.0.0/8, 10.0.0.0/8, 127.0.0.0/8
            if (bytes[0] == 0 || bytes[0] == 10 || bytes[0] == 127) return false;

            // 172.16.0.0/12 (172.16.0.0 - 172.31.255.255 RFC1918 私网)
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return false;

            // 192.168.0.0/16 (RFC1918 私网)
            if (bytes[0] == 192 && bytes[1] == 168) return false;

            // 169.254.0.0/16 (APIPA 链路本地)
            if (bytes[0] == 169 && bytes[1] == 254) return false;

            // 100.64.0.0/10 (RFC6598 CGNAT 运营商级 NAT)
            if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return false;

            // 224.0.0.0/4 (224.0.0.0 - 255.255.255.255 组播与保留地址)
            if (bytes[0] >= 224) return false;

            return true;
        }

        public static bool IsPublicIpv4(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            return IsPublicIpv4(ip.GetAddressBytes());
        }

        public static uint IpToUint(IPAddress ip)
        {
            byte[] b = ip.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | (uint)b[3];
        }

        /// <summary>
        /// 校验 IPv4 是否归属于 Cloudflare 官方 CIDR 授权网段
        /// </summary>
        public static bool BelongsToCloudflare(IPAddress ip, IEnumerable<CidrRange>? cfRanges = null)
        {
            if (!IsPublicIpv4(ip)) return false;
            uint ipUint = IpToUint(ip);
            var ranges = cfRanges ?? GetDefaultCidrRanges();
            foreach (var range in ranges)
            {
                if (range.Contains(ipUint)) return true;
            }
            return false;
        }

        /// <summary>
        /// 48 小时时效性校验
        /// </summary>
        public static bool IsTimestampFresh(DateTime timestampUtc, int maxAgeHours = DefaultMaxAgeHours)
        {
            var age = (DateTime.UtcNow - timestampUtc).TotalHours;
            return age >= -24 && age <= maxAgeHours;
        }
    }
}
