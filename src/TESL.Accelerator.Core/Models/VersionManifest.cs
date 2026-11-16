using System;
using System.Collections.Generic;

namespace TESL.Accelerator.Core.Models
{
    public class BinaryComponentInfo
    {
        public string ComponentName { get; set; } = string.Empty;
        public string LockedVersion { get; set; } = string.Empty;
        public string UpstreamTagUrl { get; set; } = string.Empty;
        public string PatchDescription { get; set; } = string.Empty;
        public string BuildTarget { get; set; } = string.Empty;
    }

    public static class VersionManifest
    {
        public const string CoreVersion = "2.0.0";
        public const string SingBoxLockedVersion = "1.11.10";
        public const string CfnatLockedVersion = "0.0.14";

        public static readonly BinaryComponentInfo SingBoxWindows = new()
        {
            ComponentName = "sing-box (Windows x64)",
            LockedVersion = SingBoxLockedVersion,
            UpstreamTagUrl = "https://github.com/SagerNet/sing-box/releases/tag/v1.11.10",
            PatchDescription = "Official Release sing-box-1.11.10-windows-amd64 with WinTun support",
            BuildTarget = "win-x64"
        };

        public static readonly BinaryComponentInfo SingBoxAndroid = new()
        {
            ComponentName = "libbox (Android AAR)",
            LockedVersion = SingBoxLockedVersion,
            UpstreamTagUrl = "https://github.com/SagerNet/sing-box/releases/tag/v1.11.10",
            PatchDescription = "Official Go Mobile gomobile libbox.aar with VpnService PlatformInterface",
            BuildTarget = "android-aar"
        };

        public static readonly BinaryComponentInfo CfnatWindows = new()
        {
            ComponentName = "cfnat (Windows x64)",
            LockedVersion = CfnatLockedVersion,
            UpstreamTagUrl = "https://github.com/fscarmen/cfnat/releases/tag/v0.0.14",
            PatchDescription = "MinGW-w64 static build with 8443 TLS Probe & ServerHello Verification Patch",
            BuildTarget = "win-x64 (MinGW GCC)"
        };

        public static readonly BinaryComponentInfo CfnatAndroid = new()
        {
            ComponentName = "cfnat (Android Native NDK)",
            LockedVersion = CfnatLockedVersion,
            UpstreamTagUrl = "https://github.com/fscarmen/cfnat/releases/tag/v0.0.14",
            PatchDescription = "Android NDK Clang POSIX build with VpnService.protect() bypass hook and 8443 TLS Probe",
            BuildTarget = "arm64-v8a, armeabi-v7a, x86_64"
        };

        public static IReadOnlyList<BinaryComponentInfo> GetAllComponents() => new[]
        {
            SingBoxWindows,
            SingBoxAndroid,
            CfnatWindows,
            CfnatAndroid
        };
    }
}
