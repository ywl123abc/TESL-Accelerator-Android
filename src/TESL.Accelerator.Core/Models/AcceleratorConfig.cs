using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TESL.Accelerator.Core.Models
{
    public class ChannelEndpointConfig
    {
        public int RemotePort { get; set; }
        public int LocalPort { get; set; }
        public string SniHost { get; set; } = string.Empty;
        public string ColoPreference { get; set; } = "SIN,HKG,NRT,HND,KIX,ICN,TPE,LAX,SJC,SEA";
        public int TaskCount { get; set; } = 100;
        public int MaxDelayMs { get; set; } = 300;
        public int ExpectedStatusCode { get; set; } = 200;
    }

    public class AcceleratorConfig
    {
        public List<string> TargetDomains { get; set; } = new()
        {
            "tesl-reborn.com",
            "dpdns.org",
            "090227.xyz",
            "182682.xyz",
            "icu.icu"
        };

        public List<int> HttpPorts { get; set; } = new() { 80, 443 };
        public List<int> PubSubPorts { get; set; } = new() { 8443 };

        public ChannelEndpointConfig Channel443 { get; set; } = new()
        {
            RemotePort = 443,
            LocalPort = 10443,
            SniHost = "tesl-reborn.com"
        };

        public ChannelEndpointConfig Channel8443 { get; set; } = new()
        {
            RemotePort = 8443,
            LocalPort = 18443,
            SniHost = "pubsub.tesl-reborn.com"
        };

        public string DnsServer { get; set; } = "223.5.5.5";
        public int DnsPort { get; set; } = 53;

        public string TunAddress { get; set; } = "172.19.0.1/30";
        public int TunMtu { get; set; } = 1400;
        public string TunInterfaceName { get; set; } = "tesl-tun";

        public string WindowsProcessName { get; set; } = "The Elder Scrolls Legends.exe";
        public string AndroidPackageName { get; set; } = "com.bethsoft.theelderscrollslegends";

        public static AcceleratorConfig CreateDefault() => new AcceleratorConfig();

        /// <summary>
        /// 生成 Windows sing-box 配置文件结构 (基于 process_name 规则)
        /// </summary>
        public string GenerateWindowsSingBoxJson(bool indented = true)
        {
            var configObj = new
            {
                log = new
                {
                    level = "info",
                    timestamp = true
                },
                dns = new
                {
                    servers = new object[]
                    {
                        new
                        {
                            type = "udp",
                            tag = "cf-dns",
                            server = DnsServer,
                            server_port = DnsPort
                        }
                    },
                    final = "cf-dns",
                    reverse_mapping = true
                },
                inbounds = new object[]
                {
                    new
                    {
                        type = "tun",
                        tag = "tun-in",
                        interface_name = TunInterfaceName,
                        address = new[] { TunAddress },
                        mtu = TunMtu,
                        auto_route = true,
                        strict_route = true,
                        stack = "system"
                    }
                },
                outbounds = new object[]
                {
                    new
                    {
                        type = "direct",
                        tag = "direct"
                    }
                },
                route = new
                {
                    auto_detect_interface = true,
                    rules = new object[]
                    {
                        new
                        {
                            process_name = new[] { WindowsProcessName },
                            network = new[] { "tcp" },
                            action = "sniff",
                            sniffer = new[] { "tls", "http" }
                        },
                        new
                        {
                            protocol = "dns",
                            action = "hijack-dns"
                        },
                        new
                        {
                            process_name = new[] { WindowsProcessName },
                            domain_suffix = TargetDomains,
                            network = new[] { "tcp" },
                            port = PubSubPorts,
                            action = "route",
                            outbound = "direct",
                            override_address = "127.0.0.1",
                            override_port = Channel8443.LocalPort
                        },
                        new
                        {
                            process_name = new[] { WindowsProcessName },
                            domain_suffix = TargetDomains,
                            network = new[] { "tcp" },
                            port = HttpPorts,
                            action = "route",
                            outbound = "direct",
                            override_address = "127.0.0.1",
                            override_port = Channel443.LocalPort
                        },
                        new
                        {
                            process_name = new[] { WindowsProcessName },
                            action = "route",
                            outbound = "direct"
                        }
                    },
                    final = "direct"
                },
                experimental = new
                {
                    cache_file = new
                    {
                        enabled = true,
                        path = "cache.db"
                    }
                }
            };

            return JsonSerializer.Serialize(configObj, new JsonSerializerOptions
            {
                WriteIndented = indented,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        /// <summary>
        /// 生成 Android sing-box 配置文件结构 (基于 package_name 规则，严禁直接照搬 Windows process_name)
        /// </summary>
        public string GenerateAndroidSingBoxJson(bool indented = true)
        {
            var configObj = new
            {
                log = new
                {
                    level = "info",
                    timestamp = true
                },
                dns = new
                {
                    servers = new object[]
                    {
                        new
                        {
                            tag = "dnspod-dns",
                            address = "udp://119.29.29.29:53",
                            detour = "direct"
                        },
                        new
                        {
                            tag = "ali-dns",
                            address = "udp://223.5.5.5:53",
                            detour = "direct"
                        }
                    },
                    final = "dnspod-dns",
                    reverse_mapping = true
                },
                inbounds = new object[]
                {
                    new
                    {
                        type = "tun",
                        tag = "tun-in",
                        address = new[] { TunAddress },
                        mtu = TunMtu,
                        auto_route = true,
                        strict_route = false,
                        stack = "gvisor",
                        include_package = new[] { AndroidPackageName }
                    }
                },
                outbounds = new object[]
                {
                    new
                    {
                        type = "direct",
                        tag = "direct"
                    }
                },
                route = new
                {
                    auto_detect_interface = true,
                    rules = new object[]
                    {
                        new
                        {
                            network = new[] { "tcp" },
                            action = "sniff",
                            sniffer = new[] { "tls", "http" }
                        },
                        new
                        {
                            protocol = "dns",
                            action = "hijack-dns"
                        },
                        new
                        {
                            domain_suffix = TargetDomains,
                            network = new[] { "tcp" },
                            port = PubSubPorts,
                            action = "route",
                            outbound = "direct",
                            override_address = "127.0.0.1",
                            override_port = Channel8443.LocalPort
                        },
                        new
                        {
                            domain_suffix = TargetDomains,
                            network = new[] { "tcp" },
                            port = HttpPorts,
                            action = "route",
                            outbound = "direct",
                            override_address = "127.0.0.1",
                            override_port = Channel443.LocalPort
                        },
                        new
                        {
                            action = "route",
                            outbound = "direct"
                        }
                    },
                    final = "direct"
                },
                experimental = new
                {
                    cache_file = new
                    {
                        enabled = true,
                        path = "cache.db"
                    }
                }
            };

            return JsonSerializer.Serialize(configObj, new JsonSerializerOptions
            {
                WriteIndented = indented,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }
    }
}
