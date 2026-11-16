using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TESL.Accelerator.Core.Abstractions;
using TESL.Accelerator.Core.Models;
using TESL.Accelerator.Core.Services;

namespace TESL.Accelerator.Tests
{
    public static class IPSourceManagerTests
    {
        private class TestLogger : IAcceleratorLogger
        {
            public List<string> Logs { get; } = new();
            public event Action<string>? OnLog;
            public void Log(string message, LogLevel level = LogLevel.Info)
            {
                Logs.Add(message);
                OnLog?.Invoke(message);
            }
        }

        private class MockHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

            public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return Task.FromResult(_handler(request));
            }
        }

        public static void Run()
        {
            // 1. 验证默认配置源定义、优先级与回退源
            var config = IpSourcesConfig.CreateDefault();
            Assert.Equal(5, config.Sources443.Count, "Sources443 must have exactly 5 verified default sources");
            Assert.Equal("Lancelot Top100", config.Sources443[0].Name);
            Assert.Equal("Lancelot Top20", config.Sources443[1].Name);
            Assert.Equal("IPDB", config.Sources443[2].Name);
            Assert.Equal("Joname", config.Sources443[3].Name);
            Assert.Equal("WeTest", config.Sources443[4].Name);

            Assert.Equal(5, config.Sources8443.Count, "Sources8443 must have exactly 5 verified default sources");
            Assert.Equal("WARP-MINI-8443", config.Sources8443[0].Name);
            Assert.True(config.Sources8443[0].Is8443Exclusive, "WARP-MINI-8443 must be exclusive to 8443");
            Assert.Equal(8443, config.Sources8443[0].PortFilter);
            Assert.Equal("Lancelot Top100 (通用)", config.Sources8443[1].Name);
            Assert.Equal("IPDB", config.Sources8443[2].Name);
            Assert.Equal("Joname", config.Sources8443[3].Name);
            Assert.Equal("WeTest", config.Sources8443[4].Name);

            // 2. 验证时效性检查 (48小时门限)
            var logger = new TestLogger();
            var manager = new IPSourceManager(logger);

            var srcWithCheck = new IpSourceItem { Name = "TestCheck", CheckTimestamp = true, MaxAgeHours = 48 };
            var srcNoCheck = new IpSourceItem { Name = "TestNoCheck", CheckTimestamp = false, MaxAgeHours = 48 };

            string freshContent = $"更新时间: {DateTime.UtcNow.AddHours(-5):yyyy-MM-dd HH:mm:ss}\n104.16.1.1";
            string expiredContent = $"更新时间: {DateTime.UtcNow.AddHours(-55):yyyy-MM-dd HH:mm:ss}\n104.16.1.1";

            bool isFresh = manager.CheckFreshness(freshContent, srcWithCheck, 48, out double age1);
            Assert.True(isFresh, "Fresh content within 48h must pass check");
            Assert.True(age1 >= 4 && age1 <= 6, "Age must be around 5 hours");

            bool isExpired = manager.CheckFreshness(expiredContent, srcWithCheck, 48, out double age2);
            Assert.False(isExpired, "Expired content >48h must fail check");
            Assert.True(age2 >= 54 && age2 <= 56, "Age must be around 55 hours");

            bool noCheckPassed = manager.CheckFreshness(expiredContent, srcNoCheck, 48, out _);
            Assert.True(noCheckPassed, "CheckTimestamp=false must skip freshness check and return true");

            // 3. 验证多镜像重试机制与脏响应过滤 (HTML/404/Empty 过滤)
            var requestedUrls = new List<string>();
            var mockHandler = new MockHttpMessageHandler(req =>
            {
                string url = req.RequestUri!.ToString();
                requestedUrls.Add(url);

                if (url.Contains("ghfast.top"))
                {
                    // 模拟第一个 mirror 返回 404
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }
                if (url.Contains("gh-proxy.com"))
                {
                    // 模拟第二个 mirror 返回 HTML 报错页面
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("<!DOCTYPE html><html><body>Error</body></html>")
                    };
                }
                // 原始 GitHub 返回有效数据
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("104.16.1.1\n104.16.1.2")
                };
            });

            var httpClient = new HttpClient(mockHandler);
            var mockManager = new IPSourceManager(logger, httpClient);

            var testSource = new IpSourceItem
            {
                Name = "TestSource",
                Url = "https://raw.githubusercontent.com/test/repo/main/ips.txt",
                IsGithub = true
            };

            var mirrors = new[] { "https://ghfast.top/https://raw.githubusercontent.com/", "https://gh-proxy.com/https://raw.githubusercontent.com/" };
            string? result = mockManager.FetchSourceContentAsync(testSource, mirrors, 5).GetAwaiter().GetResult();

            Assert.Equal("104.16.1.1\n104.16.1.2", result);
            Assert.Equal(3, requestedUrls.Count, "Should failover across 2 mirrors and hit upstream");
            Assert.Contains("ghfast.top", requestedUrls[0]);
            Assert.Contains("gh-proxy.com", requestedUrls[1]);
            Assert.Equal("https://raw.githubusercontent.com/test/repo/main/ips.txt", requestedUrls[2]);
        }
    }
}
