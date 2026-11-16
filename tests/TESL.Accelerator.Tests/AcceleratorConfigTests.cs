using TESL.Accelerator.Core.Models;

namespace TESL.Accelerator.Tests
{
    public static class AcceleratorConfigTests
    {
        public static void Run()
        {
            var config = AcceleratorConfig.CreateDefault();

            // 1. 测试 Windows sing-box 配置生成
            string winJson = config.GenerateWindowsSingBoxJson();

            // Windows 必须包含 process_name 规则且精准匹配 "The Elder Scrolls Legends.exe"
            Assert.Contains("\"process_name\"", winJson, "Windows config must contain process_name");
            Assert.Contains("The Elder Scrolls Legends.exe", winJson, "Windows config must match game process name");
            Assert.Contains("tesl-tun", winJson, "Windows config must define tesl-tun interface");
            Assert.Contains("10443", winJson, "Windows config must map 80/443 to local 10443");
            Assert.Contains("18443", winJson, "Windows config must map 8443 to local 18443");
            Assert.Contains("hijack-dns", winJson, "Windows config must hijack dns");

            // 2. 测试 Android sing-box 配置生成
            string androidJson = config.GenerateAndroidSingBoxJson();

            // Android 严禁照搬 Windows process_name 规则
            Assert.DoesNotContain("\"process_name\"", androidJson, "Android config must NOT contain process_name");
            // Android 必须包含 include_package 规则且精准匹配 "com.bethsoft.theelderscrollslegends"
            Assert.Contains("include_package", androidJson, "Android config must specify include_package for VpnService TUN");
            Assert.Contains("com.bethsoft.theelderscrollslegends", androidJson, "Android config must match TESL Android package");
            Assert.Contains("10443", androidJson, "Android config must map 80/443 to local 10443");
            Assert.Contains("18443", androidJson, "Android config must map 8443 to local 18443");
            Assert.Contains("hijack-dns", androidJson, "Android config must hijack dns");
        }
    }
}
