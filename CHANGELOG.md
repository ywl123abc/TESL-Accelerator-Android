# Changelog

All notable changes to the TESL-Accelerator-Android project will be documented in this file.

---

## [2.0.0] - 2026-11-16

### Added
- **ARM64 Native Engine**: Integrated `libcfnat.so` with 16KB ELF page size alignment for Android 15 compatibility.
- **Dual Channel Forwarding**:
  - Independent 443 business channel (`127.0.0.1:10443`).
  - Independent 8443 match signaling channel (`127.0.0.1:18443`).
- **Native Socket Protection**: Implemented thread-safe `VpnService.protect(fd)` callback prior to TCP connect.
- **Low-Power Routine Patrol**: Implemented 4-node routine patrol (Primary + Top 3 candidates) every 60 seconds (1.3s duration), drastically reducing radio wakeups and battery drain.
- **Android 15 Edge-to-Edge Support**: Integrated `View.IOnApplyWindowInsetsListener` to dynamically offset status bar cutouts and bottom gesture bars.
- **Mainland DNS Resolvers**: Configured primary DNS to Tencent DNSPod (`119.29.29.29`) and secondary to Alibaba Public DNS (`223.5.5.5`).
- **Update System**:
  - Built-in GitHub Releases API check with 6-hour background caching.
  - Built-in fixed mainland GitHub mirrors (`ghfast.top`, `gh-proxy.com`, `ghproxy.net`) with sequential failover.
  - Built-in jsDelivr CDN fallback metadata reading (`updates/latest.json`).
  - Strict APK SHA256 integrity check and package validation prior to user-confirmed installation.
