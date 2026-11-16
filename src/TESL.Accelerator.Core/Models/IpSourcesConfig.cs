using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TESL.Accelerator.Core.Models
{
    public class IpSourcesConfig
    {
        [JsonPropertyName("github_mirrors")]
        public List<string> GithubMirrors { get; set; } = new();

        [JsonPropertyName("timeout_seconds")]
        public int TimeoutSeconds { get; set; } = 6;

        [JsonPropertyName("max_age_hours")]
        public int MaxAgeHours { get; set; } = 48;

        [JsonPropertyName("min_required_ips")]
        public int MinRequiredIps { get; set; } = 10;

        [JsonPropertyName("max_candidates_per_channel")]
        public int MaxCandidatesPerChannel { get; set; } = 128;

        [JsonPropertyName("cloudflare_official_cidr_url")]
        public string CloudflareOfficialCidrUrl { get; set; } = "https://www.cloudflare.com/ips-v4";

        [JsonPropertyName("sources_443")]
        public List<IpSourceItem> Sources443 { get; set; } = new();

        [JsonPropertyName("sources_8443")]
        public List<IpSourceItem> Sources8443 { get; set; } = new();

        public static IpSourcesConfig CreateDefault()
        {
            return new IpSourcesConfig
            {
                GithubMirrors = new List<string>
                {
                    "https://ghfast.top/https://raw.githubusercontent.com/",
                    "https://gh-proxy.com/https://raw.githubusercontent.com/",
                    "https://raw.githubusercontent.com/"
                },
                TimeoutSeconds = 6,
                MaxAgeHours = 48,
                MinRequiredIps = 10,
                MaxCandidatesPerChannel = 128,
                CloudflareOfficialCidrUrl = "https://www.cloudflare.com/ips-v4",
                Sources443 = new List<IpSourceItem>
                {
                    new() { Name = "Lancelot Top100", Url = "https://raw.githubusercontent.com/LancelotRar/best-cf-ips/main/best-cf-ip-scanned-top100.txt", IsGithub = true, CheckTimestamp = true, MaxAgeHours = 48, PortFilter = 0 },
                    new() { Name = "Lancelot Top20", Url = "https://raw.githubusercontent.com/LancelotRar/best-cf-ips/main/best-cf-ip-scanned-top20.txt", IsGithub = true, CheckTimestamp = true, MaxAgeHours = 48, PortFilter = 0 },
                    new() { Name = "IPDB", Url = "https://raw.githubusercontent.com/ymyuuu/IPDB/main/BestCF/bestcfv4.txt", IsGithub = true, CheckTimestamp = false, MaxAgeHours = 48, PortFilter = 0 },
                    new() { Name = "Joname", Url = "https://raw.githubusercontent.com/joname1/BestCFip/main/ipv4.txt", IsGithub = true, CheckTimestamp = true, MaxAgeHours = 48, PortFilter = 0 },
                    new() { Name = "WeTest", Url = "https://bestcf.tools12345.com/wetest/ipv4.txt", IsGithub = false, CheckTimestamp = true, MaxAgeHours = 48, PortFilter = 0 }
                },
                Sources8443 = new List<IpSourceItem>
                {
                    new() { Name = "WARP-MINI-8443", Url = "https://bestcf.tools12345.com/WARP/WARP-MINI-8443.txt", IsGithub = false, CheckTimestamp = false, MaxAgeHours = 48, PortFilter = 8443, Is8443Exclusive = true },
                    new() { Name = "Lancelot Top100 (通用)", Url = "https://raw.githubusercontent.com/LancelotRar/best-cf-ips/main/best-cf-ip-scanned-top100.txt", IsGithub = true, CheckTimestamp = true, MaxAgeHours = 48, PortFilter = 0 },
                    new() { Name = "IPDB", Url = "https://raw.githubusercontent.com/ymyuuu/IPDB/main/BestCF/bestcfv4.txt", IsGithub = true, CheckTimestamp = false, MaxAgeHours = 48, PortFilter = 0 },
                    new() { Name = "Joname", Url = "https://raw.githubusercontent.com/joname1/BestCFip/main/ipv4.txt", IsGithub = true, CheckTimestamp = true, MaxAgeHours = 48, PortFilter = 0 },
                    new() { Name = "WeTest", Url = "https://bestcf.tools12345.com/wetest/ipv4.txt", IsGithub = false, CheckTimestamp = true, MaxAgeHours = 48, PortFilter = 0 }
                }
            };
        }
    }

    public class IpSourceItem
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("url")]
        public string Url { get; set; } = string.Empty;

        [JsonPropertyName("is_github")]
        public bool IsGithub { get; set; } = false;

        [JsonPropertyName("check_timestamp")]
        public bool CheckTimestamp { get; set; } = false;

        [JsonPropertyName("max_age_hours")]
        public int MaxAgeHours { get; set; } = 48;

        [JsonPropertyName("port_filter")]
        public int PortFilter { get; set; } = 0;

        [JsonPropertyName("is_8443_exclusive")]
        public bool Is8443Exclusive { get; set; } = false;

        [JsonPropertyName("is_fallback")]
        public bool IsFallback { get; set; } = false;
    }
}
