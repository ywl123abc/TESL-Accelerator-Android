using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Widget;
using TESL.Accelerator.Core.Services;

namespace TESL.Accelerator.AndroidHost.Services
{
    public enum UpdateCheckState
    {
        Idle,
        Checking,
        HasUpdate,
        UpToDate,
        Error,
        Downloading
    }

    public class ReleaseInfo
    {
        public string VersionName { get; set; } = string.Empty;
        public long VersionCode { get; set; }
        public string TagName { get; set; } = string.Empty;
        public string PublishedAt { get; set; } = string.Empty;
        public string Changelog { get; set; } = string.Empty;
        public string ApkName { get; set; } = string.Empty;
        public long ApkSize { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string Source { get; set; } = "GitHub"; // "GitHub" or "jsDelivr"
    }

    public class AndroidUpdateService
    {
        private const string RepoOwner = "ywl123abc";
        private const string RepoName = "TESL-Accelerator-Android";

        // 官方 API 与固定大陆加速前缀 (严格按顺序故障切换，不可混淆)
        private static readonly string[] ApiEndpoints = new[]
        {
            $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest",
            $"https://ghfast.top/https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest",
            $"https://gh-proxy.com/https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest",
            $"https://ghproxy.net/https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest"
        };

        // jsDelivr CDN 仅作为静态清单备用读取渠道，严禁用于下载 APK
        private static readonly string JsDelivrManifestUrl =
            $"https://cdn.jsdelivr.net/gh/{RepoOwner}/{RepoName}@main/updates/latest.json";

        // 固定下载加速前缀列表 (依次尝试)
        private static readonly string[] DownloadMirrorPrefixes = new[]
        {
            "", // 原始 GitHub 下载地址
            "https://ghfast.top/",
            "https://gh-proxy.com/",
            "https://ghproxy.net/"
        };

        private static readonly HttpClient HttpClient = new(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CheckCertificateRevocationList = false
        })
        {
            Timeout = TimeSpan.FromSeconds(25)
        };

        static AndroidUpdateService()
        {
            HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TESL-Accelerator-Android-Updater/2.0");
        }

        public static UpdateCheckState CurrentState { get; private set; } = UpdateCheckState.Idle;
        public static ReleaseInfo? LatestRelease { get; private set; }
        public static string LastErrorMessage { get; private set; } = string.Empty;

        private static DateTime _lastAutoCheckTime = DateTime.MinValue;
        private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);

        public static event Action<UpdateCheckState, ReleaseInfo?>? OnStateChanged;

        public static void CheckUpdateBackground(Context context, bool isManual = false)
        {
            if (!isManual && DateTime.UtcNow - _lastAutoCheckTime < AutoCheckInterval && CurrentState == UpdateCheckState.HasUpdate)
            {
                return;
            }

            Task.Run(async () =>
            {
                await PerformCheckUpdateAsync(context, isManual);
            });
        }

        public static async Task<ReleaseInfo?> PerformCheckUpdateAsync(Context context, bool isManual)
        {
            if (CurrentState == UpdateCheckState.Checking || CurrentState == UpdateCheckState.Downloading)
            {
                return LatestRelease;
            }

            SetState(UpdateCheckState.Checking, LatestRelease);
            _lastAutoCheckTime = DateTime.UtcNow;

            long currentVersionCode = GetCurrentVersionCode(context);
            ReleaseInfo? release = null;
            string lastErr = string.Empty;

            // 1. 优先尝试 GitHub 官方 API 及其固定大陆加速前缀
            foreach (var apiUri in ApiEndpoints)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var response = await HttpClient.GetAsync(apiUri, cts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        lastErr = $"HTTP {(int)response.StatusCode}";
                        continue;
                    }

                    string? contentType = response.Content.Headers.ContentType?.MediaType;
                    if (contentType != null && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                    {
                        lastErr = "Non-JSON response";
                        continue;
                    }

                    string json = await response.Content.ReadAsStringAsync(cts.Token);
                    release = ParseGitHubRelease(json);
                    if (release != null)
                    {
                        release.Source = apiUri.Contains("api.github.com") && !apiUri.StartsWith("https://gh") ? "GitHub Official" : "GitHub Mirror";
                        break;
                    }
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                }
            }

            // 2. 若所有 GitHub API 渠道均失败，使用 jsDelivr CDN 读取静态清单作为备用元数据
            if (release == null)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var response = await HttpClient.GetAsync(JsDelivrManifestUrl, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync(cts.Token);
                        release = ParseJsDelivrManifest(json);
                        if (release != null)
                        {
                            release.Source = "jsDelivr CDN (备用元数据)";
                        }
                    }
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                }
            }

            if (release == null)
            {
                LastErrorMessage = $"更新检查失败: {lastErr}";
                SetState(UpdateCheckState.Error, null);
                return null;
            }

            LatestRelease = release;

            // 严格基于整数 versionCode 比较版本大小，禁止使用字符串 versionName 比较
            if (release.VersionCode > currentVersionCode)
            {
                SetState(UpdateCheckState.HasUpdate, release);
            }
            else
            {
                SetState(UpdateCheckState.UpToDate, release);
            }

            return release;
        }

        private static void SetState(UpdateCheckState state, ReleaseInfo? release)
        {
            CurrentState = state;
            OnStateChanged?.Invoke(state, release);
        }

        public static long GetCurrentVersionCode(Context context)
        {
            try
            {
                var packageInfo = context.PackageManager?.GetPackageInfo(context.PackageName ?? "com.tesl.accelerator", 0);
                if (packageInfo != null)
                {
                    if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                    {
                        return packageInfo.LongVersionCode;
                    }
#pragma warning disable CA1422
                    return packageInfo.VersionCode;
#pragma warning restore CA1422
                }
            }
            catch { }
            return 1;
        }

        public static string GetCurrentVersionName(Context context)
        {
            try
            {
                var packageInfo = context.PackageManager?.GetPackageInfo(context.PackageName ?? "com.tesl.accelerator", 0);
                return packageInfo?.VersionName ?? "2.0.0";
            }
            catch { }
            return "2.0.0";
        }

        public static async Task<bool> DownloadAndInstallApkAsync(Activity activity, ReleaseInfo release, Action<int> onProgress)
        {
            if (CurrentState == UpdateCheckState.Downloading) return false;

            SetState(UpdateCheckState.Downloading, release);

            string targetFileName = string.IsNullOrWhiteSpace(release.ApkName) ? "TESL-Accelerator-Update.apk" : release.ApkName;
            string destDir = activity.CacheDir?.AbsolutePath ?? activity.FilesDir?.AbsolutePath ?? "/data/data/com.tesl.accelerator/cache";
            string destPath = Path.Combine(destDir, targetFileName);

            // 1. 生成并依次尝试下载链接 (官方原址 -> 3个固定加速前缀)
            bool downloadSuccess = false;
            string lastErr = string.Empty;

            foreach (var prefix in DownloadMirrorPrefixes)
            {
                string rawUrl = release.DownloadUrl;
                string downloadUrl = string.IsNullOrEmpty(prefix) ? rawUrl : $"{prefix}{rawUrl}";

                try
                {
                    if (File.Exists(destPath)) File.Delete(destPath);

                    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    using var response = await HttpClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                    if (!response.IsSuccessStatusCode)
                    {
                        lastErr = $"HTTP {(int)response.StatusCode}";
                        continue;
                    }

                    // 校验 Content-Type，坚决防止加速站返回的 HTML 报错页面被保存为 APK
                    var contentType = response.Content.Headers.ContentType?.MediaType;
                    if (contentType != null && (contentType.Contains("html") || contentType.Contains("text")))
                    {
                        lastErr = "返回了 HTML/文本错误页面，非有效 APK";
                        continue;
                    }

                    long totalBytes = response.Content.Headers.ContentLength ?? release.ApkSize;
                    long receivedBytes = 0;

                    using var sourceStream = await response.Content.ReadAsStreamAsync(cts.Token);
                    using var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);

                    byte[] buffer = new byte[64 * 1024];
                    int bytesRead;

                    while ((bytesRead = await sourceStream.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                    {
                        await destStream.WriteAsync(buffer, 0, bytesRead, cts.Token);
                        receivedBytes += bytesRead;

                        if (totalBytes > 0)
                        {
                            int progress = (int)((receivedBytes * 100) / totalBytes);
                            onProgress(Math.Min(progress, 99));
                        }
                    }

                    // 验证下载文件大小 (不小于 5MB，避免空文件或截断包)
                    if (new FileInfo(destPath).Length < 1024 * 1024)
                    {
                        lastErr = "文件大小异常，低于预期 APK 尺寸";
                        File.Delete(destPath);
                        continue;
                    }

                    downloadSuccess = true;
                    onProgress(100);
                    break;
                }
                catch (Exception ex)
                {
                    lastErr = ex.Message;
                    if (File.Exists(destPath)) File.Delete(destPath);
                }
            }

            if (!downloadSuccess)
            {
                activity.RunOnUiThread(() =>
                {
                    Toast.MakeText(activity, $"下载失败: {lastErr}", ToastLength.Long)?.Show();
                });
                SetState(UpdateCheckState.HasUpdate, release);
                return false;
            }

            // 2. 完整性与安全校验 (SHA256、包名一致性、版本号严格递增、APK 结构有效性)
            string verifyErr = VerifyApkIntegrity(activity, destPath, release);
            if (!string.IsNullOrEmpty(verifyErr))
            {
                if (File.Exists(destPath)) File.Delete(destPath);
                activity.RunOnUiThread(() =>
                {
                    Toast.MakeText(activity, $"安全校验阻断: {verifyErr}", ToastLength.Long)?.Show();
                });
                SetState(UpdateCheckState.HasUpdate, release);
                return false;
            }

            // 3. 调起 Android 系统原生安装确认界面
            activity.RunOnUiThread(() =>
            {
                PromptSystemInstall(activity, destPath);
            });

            SetState(UpdateCheckState.HasUpdate, release);
            return true;
        }

        private static string VerifyApkIntegrity(Context context, string apkPath, ReleaseInfo release)
        {
            try
            {
                // A. SHA256 校验 (若发布元数据提供了可信 SHA256)
                if (!string.IsNullOrWhiteSpace(release.Sha256))
                {
                    using var stream = File.OpenRead(apkPath);
                    byte[] hash = SHA256.HashData(stream);
                    string calculated = Convert.ToHexString(hash).ToLowerInvariant();
                    string expected = release.Sha256.Trim().ToLowerInvariant();

                    if (!string.Equals(calculated, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return $"SHA256 校验不匹配 (预期: {expected[..8]}..., 实际: {calculated[..8]}...)";
                    }
                }

                // B. 解析 APK 包名与 VersionCode
                var pm = context.PackageManager;
                if (pm == null) return "无法访问系统 PackageManager";

#pragma warning disable CA1422
                var archiveInfo = pm.GetPackageArchiveInfo(apkPath, PackageInfoFlags.Signatures);
#pragma warning restore CA1422
                if (archiveInfo == null)
                {
                    return "APK 文件结构损坏，无法解析包信息";
                }

                if (!string.Equals(archiveInfo.PackageName, context.PackageName, StringComparison.OrdinalIgnoreCase))
                {
                    return $"APK 包名 ({archiveInfo.PackageName}) 与当前应用 ({context.PackageName}) 不匹配";
                }

                long apkVersionCode = 0;
                if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
                {
                    apkVersionCode = archiveInfo.LongVersionCode;
                }
                else
                {
#pragma warning disable CA1422
                    apkVersionCode = archiveInfo.VersionCode;
#pragma warning restore CA1422
                }

                long currentVersionCode = GetCurrentVersionCode(context);
                if (apkVersionCode <= currentVersionCode)
                {
                    return $"安装包版本号 ({apkVersionCode}) 不高于当前运行版本 ({currentVersionCode})";
                }

                return string.Empty; // 校验 100% 通过
            }
            catch (Exception ex)
            {
                return $"校验异常: {ex.Message}";
            }
        }

        private static void PromptSystemInstall(Activity activity, string apkPath)
        {
            try
            {
                // Android 8.0+ 检查未知来源安装权限
                if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
                {
                    if (activity.PackageManager?.CanRequestPackageInstalls() == false)
                    {
                        var permissionIntent = new Intent(Android.Provider.Settings.ActionManageUnknownAppSources);
                        permissionIntent.SetData(Android.Net.Uri.Parse($"package:{activity.PackageName}"));
                        activity.StartActivity(permissionIntent);
                        Toast.MakeText(activity, "请允许 TESL 加速器安装应用后重试", ToastLength.Long)?.Show();
                        return;
                    }
                }

                var file = new Java.IO.File(apkPath);
                var contentUri = Android.Net.Uri.Parse($"content://com.tesl.accelerator.fileprovider/{file.Name}");

                var installIntent = new Intent(Intent.ActionView);
                installIntent.SetDataAndType(contentUri, "application/vnd.android.package-archive");
                installIntent.SetFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);

                activity.StartActivity(installIntent);
            }
            catch (Exception ex)
            {
                Toast.MakeText(activity, $"调起系统安装失败: {ex.Message}", ToastLength.Long)?.Show();
            }
        }

        private static ReleaseInfo? ParseGitHubRelease(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? "" : "";
                string body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? "" : "";
                string publishedAt = root.TryGetProperty("published_at", out var pubElem) ? pubElem.GetString() ?? "" : "";

                // 从 assets 中寻找 .apk
                string apkName = string.Empty;
                string downloadUrl = string.Empty;
                long apkSize = 0;

                if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assetsElem.EnumerateArray())
                    {
                        string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        if (name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))
                        {
                            apkName = name;
                            downloadUrl = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                            apkSize = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(downloadUrl)) return null;

                // 提取版本号与 SHA256 (优先从 body 中的 VersionCode: xxx / SHA256: xxx 提取)
                long versionCode = ExtractVersionCodeFromBody(body);
                if (versionCode <= 0)
                {
                    versionCode = ExtractVersionCodeFromTag(tagName);
                }
                string sha256 = ExtractSha256FromBody(body);

                return new ReleaseInfo
                {
                    VersionName = tagName.TrimStart('v'),
                    VersionCode = versionCode,
                    TagName = tagName,
                    PublishedAt = publishedAt,
                    Changelog = body,
                    ApkName = apkName,
                    ApkSize = apkSize,
                    Sha256 = sha256,
                    DownloadUrl = downloadUrl
                };
            }
            catch
            {
                return null;
            }
        }

        private static ReleaseInfo? ParseJsDelivrManifest(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string versionName = root.TryGetProperty("versionName", out var vn) ? vn.GetString() ?? "" : "";
                long versionCode = root.TryGetProperty("versionCode", out var vc) ? vc.GetInt64() : 0;
                string tag = root.TryGetProperty("tag", out var tg) ? tg.GetString() ?? "" : "";
                string publishedAt = root.TryGetProperty("publishedAt", out var pa) ? pa.GetString() ?? "" : "";
                string changelog = root.TryGetProperty("changelog", out var cl) ? cl.GetString() ?? "" : "";

                string fileName = string.Empty;
                long size = 0;
                string sha256 = string.Empty;
                string downloadUrl = string.Empty;

                if (root.TryGetProperty("apk", out var apkElem))
                {
                    fileName = apkElem.TryGetProperty("fileName", out var fn) ? fn.GetString() ?? "" : "";
                    size = apkElem.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                    sha256 = apkElem.TryGetProperty("sha256", out var sh) ? sh.GetString() ?? "" : "";
                    downloadUrl = apkElem.TryGetProperty("downloadUrl", out var dl) ? dl.GetString() ?? "" : "";
                }

                if (versionCode <= 0 || string.IsNullOrEmpty(downloadUrl)) return null;

                return new ReleaseInfo
                {
                    VersionName = versionName,
                    VersionCode = versionCode,
                    TagName = tag,
                    PublishedAt = publishedAt,
                    Changelog = changelog,
                    ApkName = fileName,
                    ApkSize = size,
                    Sha256 = sha256,
                    DownloadUrl = downloadUrl
                };
            }
            catch
            {
                return null;
            }
        }

        private static long ExtractVersionCodeFromBody(string body)
        {
            var lines = body.Split('\n');
            foreach (var line in lines)
            {
                if (line.Contains("VersionCode", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = line.IndexOf(':');
                    if (colon >= 0 && colon + 1 < line.Length)
                    {
                        string candidate = line[(colon + 1)..].Trim().Trim('`', '*', ' ');
                        if (long.TryParse(candidate, out long code) && code > 0)
                        {
                            return code;
                        }
                    }
                }
            }
            return 0;
        }

        private static long ExtractVersionCodeFromTag(string tag)
        {
            // 例如 v2.0.1 -> 20001, v2.1.0 -> 20100
            string clean = tag.TrimStart('v', 'V');
            var parts = clean.Split('.');
            if (parts.Length >= 2 && long.TryParse(parts[0], out long major) && long.TryParse(parts[1], out long minor))
            {
                long patch = parts.Length >= 3 && long.TryParse(parts[2], out long p) ? p : 0;
                return major * 10000 + minor * 100 + patch;
            }
            return 1;
        }

        private static string ExtractSha256FromBody(string body)
        {
            // 从 release notes 中正则或关键词查找 SHA256
            var lines = body.Split('\n');
            foreach (var line in lines)
            {
                if (line.Contains("SHA256", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = line.IndexOf(':');
                    if (colon >= 0 && colon + 1 < line.Length)
                    {
                        string candidate = line[(colon + 1)..].Trim().Trim('`', '*', ' ');
                        if (candidate.Length == 64)
                        {
                            return candidate;
                        }
                    }
                }
            }
            return string.Empty;
        }
    }
}
