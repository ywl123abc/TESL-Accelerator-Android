using System;
using System.IO;
using TESL.Accelerator.Core.Models;
using TESL.Accelerator.Core.Services;

namespace TESL.Accelerator.Tests
{
    public static class CacheManagerTests
    {
        public static void Run()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "tesl_test_cache_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var cacheManager = new CacheManager();

                // 1. 测试解析合法的 direct-cache-v4.txt (包含 104.16.*)
                string validCacheContent = "104.16.1.1|SIN|Cloudflare|新加坡|85\n104.16.1.2|HKG|Cloudflare|香港|92";
                string cachePath = Path.Combine(tempDir, CacheManager.DirectCacheFileName);
                File.WriteAllText(cachePath, validCacheContent);

                var ch = new ChannelInfo { Port = 443 };
                bool readOk = cacheManager.TryReadCache(tempDir, ch);

                Assert.True(readOk, "Should read valid cache file");
                Assert.Equal("104.16.1.1", ch.Ip);
                Assert.Equal("新加坡 (SIN)", ch.Location);
                Assert.Equal(85, ch.LatencyMs);
                Assert.True(ch.IsReady, "Channel should be ready");

                // 2. 测试合法 172.65.x.x 缓存不会被无条件清除，并能够正常被解析读取
                string cf17265Cache = "172.65.251.100|SIN|Cloudflare|新加坡|60";
                File.WriteAllText(cachePath, cf17265Cache);

                cacheManager.PurifyCacheFolder(tempDir, "test");
                Assert.True(File.Exists(cachePath), "Legitimate 172.65.x.x cache must NOT be unconditionally purged");

                var ch172 = new ChannelInfo { Port = 443 };
                bool read172Ok = cacheManager.TryReadCache(tempDir, ch172);
                Assert.True(read172Ok, "Should read 172.65 cache");
                Assert.Equal("172.65.251.100", ch172.Ip);
                Assert.Equal("新加坡 (SIN)", ch172.Location);
                Assert.Equal(60, ch172.LatencyMs);

                // 3. 测试损坏的空缓存或无分隔符格式能够被正确净化
                File.WriteAllText(cachePath, "");
                cacheManager.PurifyCacheFolder(tempDir, "corrupted_empty");
                Assert.False(File.Exists(cachePath), "Empty corrupted cache file should be purged");

                // 4. 测试原子安全写文件
                string testAtomicFile = Path.Combine(tempDir, "atomic_test.txt");
                CacheManager.AtomicWriteText(testAtomicFile, "hello atomic world");

                Assert.True(File.Exists(testAtomicFile), "Atomic written file must exist");
                Assert.Equal("hello atomic world", File.ReadAllText(testAtomicFile));

                // 5. 测试 48 小时缓存过期判断与读取拦截
                string expDir = Path.Combine(tempDir, "expired_test");
                Directory.CreateDirectory(expDir);
                string expCachePath = Path.Combine(expDir, CacheManager.DirectCacheFileName);
                File.WriteAllText(expCachePath, validCacheContent);
                // 模拟将写入时间设为 50 小时前
                File.SetLastWriteTimeUtc(expCachePath, DateTime.UtcNow.AddHours(-50));

                Assert.True(cacheManager.IsCacheExpired(expDir), "Cache older than 48h must be marked expired");
                var expiredCh = new ChannelInfo { Port = 443 };
                bool readExpired = cacheManager.TryReadCache(expDir, expiredCh, maxAge: TimeSpan.FromHours(48));
                Assert.False(readExpired, "TryReadCache with maxAge 48h must reject 50h old cache file");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }
}
