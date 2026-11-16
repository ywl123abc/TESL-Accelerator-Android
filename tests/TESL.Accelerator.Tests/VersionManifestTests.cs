using TESL.Accelerator.Core.Models;

namespace TESL.Accelerator.Tests
{
    public static class VersionManifestTests
    {
        public static void Run()
        {
            Assert.Equal("2.0.0", VersionManifest.CoreVersion, "Core version must be 2.0.0");
            Assert.Equal("1.11.10", VersionManifest.SingBoxLockedVersion, "sing-box locked version must be 1.11.10");
            Assert.Equal("0.0.14", VersionManifest.CfnatLockedVersion, "cfnat locked version must be 0.0.14");

            var components = VersionManifest.GetAllComponents();
            Assert.Equal(4, components.Count, "Must have 4 binary component lock definitions (Win singbox, Android libbox, Win cfnat, Android cfnat)");

            foreach (var comp in components)
            {
                Assert.False(string.IsNullOrWhiteSpace(comp.ComponentName), "Component name must not be empty");
                Assert.False(string.IsNullOrWhiteSpace(comp.LockedVersion), "Component locked version must not be empty");
                Assert.False(string.IsNullOrWhiteSpace(comp.UpstreamTagUrl), "Component upstream tag url must not be empty");
                Assert.False(string.IsNullOrWhiteSpace(comp.PatchDescription), "Component patch description must not be empty");
                Assert.False(string.IsNullOrWhiteSpace(comp.BuildTarget), "Component build target must not be empty");
            }
        }
    }
}
