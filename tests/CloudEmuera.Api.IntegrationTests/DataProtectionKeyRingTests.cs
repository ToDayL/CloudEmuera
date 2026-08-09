using CloudEmuera.Api.Security;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

public sealed class DataProtectionKeyRingTests
{
    [Fact]
    [Trait("Category", "Authentication")]
    public void PrepareAndHardenUsePrivateUnixPermissions()
    {
        if (!OperatingSystem.IsLinux()) return;
        string root = Directory.CreateTempSubdirectory("cloudemuera-keyring-").FullName;
        try
        {
            DirectoryInfo keys = DataProtectionKeyRing.Prepare(root);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(root) & (UnixFileMode)0x1FF);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(keys.FullName) & (UnixFileMode)0x1FF);
            string key = Path.Combine(keys.FullName, "key-test.xml");
            File.WriteAllText(key, "<key />");
            File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            DataProtectionKeyRing.HardenExistingKeyFiles(root);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key) & (UnixFileMode)0x1FF);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "Authentication")]
    public void KeyRingRejectsSymbolicLinkAncestorsAndKeyFiles()
    {
        if (!OperatingSystem.IsLinux()) return;
        string parent = Directory.CreateTempSubdirectory("cloudemuera-keyring-links-").FullName;
        try
        {
            string actual = Directory.CreateDirectory(Path.Combine(parent, "actual")).FullName;
            string linked = Path.Combine(parent, "linked");
            Directory.CreateSymbolicLink(linked, actual);
            Assert.Throws<InvalidOperationException>(() => DataProtectionKeyRing.Prepare(linked));

            DirectoryInfo keys = DataProtectionKeyRing.Prepare(actual);
            string target = Path.Combine(parent, "target.xml");
            File.WriteAllText(target, "<key />");
            File.CreateSymbolicLink(Path.Combine(keys.FullName, "key-linked.xml"), target);
            Assert.Throws<InvalidOperationException>(() => DataProtectionKeyRing.HardenExistingKeyFiles(actual));
        }
        finally { Directory.Delete(parent, recursive: true); }
    }
}
