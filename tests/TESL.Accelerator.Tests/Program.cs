using System;

namespace TESL.Accelerator.Tests
{
    internal class Program
    {
        private static int Main(string[] args)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==========================================================================");
            Console.WriteLine("   TESL-Accelerator.Core 业务单元测试套件");
            Console.WriteLine("   覆盖: IP清洗 / CIDR网段 / 候选池治理 / 缓存控制 / 双端配置生成 / 版本锁");
            Console.WriteLine("==========================================================================");
            Console.ResetColor();

            int passed = 0;
            int failed = 0;

            void RunTest(string testName, Action testAction)
            {
                try
                {
                    testAction();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  [PASS] {testName}");
                    Console.ResetColor();
                    passed++;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"  [FAIL] {testName}: {ex.Message}");
                    Console.ResetColor();
                    failed++;
                }
            }

            RunTest("CloudflareValidator: 公网IP清洗与 Cloudflare 官方 Anycast (含 172.65.*) 校验", CloudflareValidatorTests.Run);
            RunTest("IPSourceManager: 默认多源优先级、48h时效性检查与多镜像容灾回退", IPSourceManagerTests.Run);
            RunTest("IPParser: 候选IP提取、端口8443分离与多格式时间戳解析", IPParserTests.Run);
            RunTest("CandidatePool: 双通道独立候选池上限 (128)、去重与隔离维护", CandidatePoolTests.Run);
            RunTest("CacheManager: direct-cache-v4 解析、172.65 合法缓存保留与损坏缓存净化", CacheManagerTests.Run);
            RunTest("AcceleratorCoordinator: 生命周期状态机、双端启动幂等性与事件通知", AcceleratorCoordinatorTests.Run);
            RunTest("AcceleratorConfig: Windows 与 Android sing-box 双端差异化配置生成", AcceleratorConfigTests.Run);
            RunTest("VersionManifest: 核心版本与 sing-box / cfnat 双平台版本锁定校验", VersionManifestTests.Run);
            RunTest("AndroidEngine: AndroidPlatform 装配、VpnService 生命周期、TUN 所有权与 Protect 校验", AndroidEngineTests.Run);

            Console.WriteLine();
            Console.WriteLine("--------------------------------------------------------------------------");
            if (failed == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  测试全部通过！通过: {passed}, 失败: {failed}");
                Console.ResetColor();
                return 0;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  测试存在失败项！通过: {passed}, 失败: {failed}");
                Console.ResetColor();
                return 1;
            }
        }
    }
}
