# TESL-Accelerator-Android (TESL 加速器 Android 版)

专为《上古卷轴：传奇》（*The Elder Scrolls: Legends*，TESL）量身定制的 Android 移动端极速直连加速工具。

---

## 一、核心架构与特性

- **双通道分离架构**：
  - **443 业务通道**：负责卡牌数据加载、牌库检索、账号认证与商城资源请求，转发至 127.0.0.1:10443。
  - **8443 对战信令通道**：负责实时 PubSub 长连接通信与真人/人机对战，转发至 127.0.0.1:18443。
- **底层原生引擎驱动**：
  - **内核**：基于 sing-box v1.11.10 兼容内核。
  - **优选引擎**：ARM64 原生 `libcfnat.so` C 转发与 Anycast 延迟探测引擎。
  - **系统级 Protect 回调**：Worker 线程在 socket connect 前安全回调 Android VpnService.protect(fd)，防止网络死锁与 TUN 回环。
- **低功耗常规巡检**：
  - 启动阶段执行快速优选（Fast-Path，首个健康节点即刻就绪）。
  - 常规巡检模式采用主节点 + Top 3 备用节点轻量探针（单轮约 1.3 秒，休眠 60 秒），整机发热极低、电池温度稳定。
- **Android 15 全面屏适配**：
  - 接入 WindowInsets 动态避让系统状态栏与底部手势导航条，支持极黑暗黑主题。
- **中国大陆 GitHub 访问加速与更新系统**：
  - GitHub Releases 为唯一官方正式发布渠道。
  - 内置多条固定 GitHub 大陆镜像加速线路（`ghfast.top`、`gh-proxy.com`、`ghproxy.net`）与 jsDelivr CDN 备用元数据读取。
  - 自动顺序故障切换，用户点击确认后调起系统安装界面。

---

## 二、下载与安装

前往 [GitHub Releases 页面](https://github.com/ywl123abc/TESL-Accelerator-Android/releases/latest) 下载最新发布的 APK 安装包。

### 运行环境要求
- **操作系统**：Android 8.0 (API 26) 及以上（完整适配至 Android 15 / API 35）。
- **处理器架构**：ARM64-v8a。
- **系统权限**：前台网络服务、VPN 建立权限（由系统自动弹窗授权）。

---

## 三、网络加速线路说明

应用内更新系统内置了固定的 GitHub 加速镜像列表，用于改善中国大陆地区的 Release 元数据获取与 APK 下载体验。

> [!NOTE]
> 固定加速镜像线路属于第三方公共加速基础设施，其可用性会随网络环境和时间推移发生动态变化。应用内已内置自动故障切换机制（官方地址 -> 镜像 1 -> 镜像 2 -> 镜像 3 -> 官方回退）。
> 本项目无法保证所有地区、所有网络运营商均能在任何时刻保持 100% 连通。

---

## 四、许可证与致谢

- sing-box: GNU General Public License v3.0 or later (GPL-3.0-or-later)
- cfnat: MIT License
- Microsoft .NET Runtime: MIT License

详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
