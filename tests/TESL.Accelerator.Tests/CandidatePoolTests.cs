using System.Collections.Generic;
using TESL.Accelerator.Core.Services;

namespace TESL.Accelerator.Tests
{
    public static class CandidatePoolTests
    {
        public static void Run()
        {
            var pool = new CandidatePool();

            // 1. 测试最大容量限制与去重
            var testIps = new List<string>();
            for (int i = 0; i < 200; i++)
            {
                testIps.Add($"104.16.1.{i % 150}"); // 包含重复元素
            }

            int added = pool.AddCandidates443(testIps, maxLimit: 128);
            Assert.Equal(128, pool.Candidates443.Count, "443 candidates must not exceed 128");
            Assert.Equal(128, added, "Added unique count must cap at 128");

            // 重复添加相同 IP 应被完全忽略
            int addedAgain = pool.AddCandidates443(new[] { "104.16.1.1", "104.16.1.2" }, maxLimit: 128);
            Assert.Equal(0, addedAgain, "Duplicate IPs must not be added");

            // 2. 8443 通道独立性验证
            pool.AddCandidates8443(new[] { "162.159.192.1", "162.159.192.2" });
            Assert.Equal(2, pool.Candidates8443.Count, "8443 pool must be independent");
            Assert.Equal(128, pool.Candidates443.Count, "443 pool count must remain unaffected");

            // 3. 清理验证
            pool.Clear();
            Assert.Equal(0, pool.Candidates443.Count, "Pool 443 must be cleared");
            Assert.Equal(0, pool.Candidates8443.Count, "Pool 8443 must be cleared");
        }
    }
}
