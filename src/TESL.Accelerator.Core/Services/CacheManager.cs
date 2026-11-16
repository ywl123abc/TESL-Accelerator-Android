using System;
using System.IO;
using System.Linq;
using System.Text;
using TESL.Accelerator.Core.Abstractions;
using TESL.Accelerator.Core.Models;

namespace TESL.Accelerator.Core.Services
{
    public class CacheManager
    {
        private readonly IAcceleratorLogger? _logger;

        public CacheManager(IAcceleratorLogger? logger = null)
        {
            _logger = logger;
        }

        public const string DirectCacheFileName = "direct-cache-v4.txt";

        /// <summary>
        /// 校验并清理格式损坏或内容异常的污损缓存（合法 172.65.* 属于 Cloudflare 官方 Anycast，予以正常保留）
        /// </summary>
        public void PurifyCacheFolder(string folderPath, string tag = "")
        {
            string cachePath = Path.Combine(folderPath, DirectCacheFileName);
            if (!File.Exists(cachePath)) return;

            try
            {
                var fileInfo = new FileInfo(cachePath);
                if (fileInfo.Length == 0)
                {
                    File.Delete(cachePath);
                    return;
                }

                string content = File.ReadAllText(cachePath, Encoding.UTF8).Trim();
                if (string.IsNullOrWhiteSpace(content) || !content.Contains("|"))
                {
                    _logger?.Log($"[缓存净化] 检测到 {tag} 缓存格式异常损坏，自动清除...", LogLevel.Warning);
                    File.Delete(cachePath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Log($"[缓存净化异常] {ex.Message}", LogLevel.Warning);
            }
        }

        public void ClearCache(string folderPath)
        {
            try
            {
                string cachePath = Path.Combine(folderPath, DirectCacheFileName);
                if (File.Exists(cachePath))
                {
                    File.Delete(cachePath);
                }
            }
            catch { }
        }

        /// <summary>
        /// 检查缓存文件是否过期 (默认 48 小时)
        /// </summary>
        public bool IsCacheExpired(string folderPath, TimeSpan? maxAge = null)
        {
            string cachePath = Path.Combine(folderPath, DirectCacheFileName);
            if (!File.Exists(cachePath)) return true;
            var ageLimit = maxAge ?? TimeSpan.FromHours(48);
            var fi = new FileInfo(cachePath);
            return (DateTime.UtcNow - fi.LastWriteTimeUtc) > ageLimit;
        }

        /// <summary>
        /// 从 cfnat 输出的 direct-cache-v4.txt 中解析最优节点信息
        /// </summary>
        public bool TryReadCache(string folderPath, ChannelInfo targetChannel, string defaultReadyStatus = "Anycast 直连", TimeSpan? maxAge = null)
        {
            try
            {
                string cachePath = Path.Combine(folderPath, DirectCacheFileName);
                if (!File.Exists(cachePath)) return false;

                if (maxAge.HasValue)
                {
                    var fileInfo = new FileInfo(cachePath);
                    if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > maxAge.Value)
                    {
                        _logger?.Log($"[缓存过期] {cachePath} 已超过有效期 ({maxAge.Value.TotalHours} 小时)，跳过读取。", LogLevel.Warning);
                        return false;
                    }
                }

                var lines = File.ReadAllLines(cachePath)
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("#") && !l.StartsWith("//"))
                    .ToList();

                if (lines.Count == 0) return false;

                // 格式例: 104.16.1.1|SIN|...|新加坡|85
                var parts = lines[0].Split('|');
                if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
                {
                    targetChannel.Ip = parts[0].Trim();

                    if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]))
                    {
                        string coloCode = parts.Length > 1 ? parts[1].Trim() : "";
                        targetChannel.Location = $"{parts[3].Trim()} ({coloCode})";
                    }

                    if (parts.Length > 4 && int.TryParse(parts[4].Trim(), out int lat))
                    {
                        targetChannel.LatencyMs = lat;
                    }

                    targetChannel.IsReady = true;
                    targetChannel.StatusText = defaultReadyStatus;
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// 跨平台原子写入文件 (先写入随机临时文件，再原子替换，避免进程并发读到半写入碎片)
        /// </summary>
        public static void AtomicWriteText(string destinationPath, string content)
        {
            string dir = Path.GetDirectoryName(destinationPath) ?? "";
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmpPath = destinationPath + ".tmp." + Guid.NewGuid().ToString("N");
            var utf8NoBom = new UTF8Encoding(false);
            File.WriteAllText(tmpPath, content, utf8NoBom);
            File.Move(tmpPath, destinationPath, overwrite: true);
        }
    }
}
