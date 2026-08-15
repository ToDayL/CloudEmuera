using System.Text;
using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Paths;

[Trait("Category", "SavePathSecurity")]
public sealed class EmueraSavePathPolicyTests
{
    [Theory]
    [InlineData("global.sav", EmueraSaveFileKind.Global)]
    [InlineData("save1.sav", EmueraSaveFileKind.Normal)]
    public void RootLayoutAcceptsOnlyNativeSaveFiles(string candidate, EmueraSaveFileKind kind)
    {
        EmueraSavePath path = EmueraSavePathPolicy.Parse(RuntimeSaveLayout.Root, candidate);

        Assert.Equal(candidate, path.Value);
        Assert.Equal(kind, path.Kind);
    }

    [Theory]
    [InlineData("sav/global.sav")]
    [InlineData("folder/save1.sav")]
    [InlineData("txt1.txt")]
    [InlineData("img1.png")]
    public void RootLayoutRejectsPhysicalPrefixNestedPathsAndAuxiliaryFiles(string candidate)
    {
        Assert.False(EmueraSavePathPolicy.TryParse(RuntimeSaveLayout.Root, candidate, out _));
    }

    [Fact]
    public void SavLayoutAllowsSafeDirectoriesButNeverASecondPhysicalSavPrefix()
    {
        EmueraSavePath nested = EmueraSavePathPolicy.Parse(RuntimeSaveLayout.SavDirectory, "profiles/slot_1/save12.sav");

        Assert.Equal("profiles/slot_1", nested.ParentPath);
        Assert.False(EmueraSavePathPolicy.TryParse(RuntimeSaveLayout.SavDirectory, "sav/save1.sav", out _));
        Assert.False(EmueraSavePathPolicy.TryParse(RuntimeSaveLayout.SavDirectory, "unsafe.dir/save1.sav", out _));
    }

    [Theory]
    [InlineData("../save1.sav")]
    [InlineData("/save1.sav")]
    [InlineData("C:/save1.sav")]
    [InlineData("save1.sav/")]
    [InlineData("save1.sav\\backup")]
    [InlineData("save1.sav\u0001")]
    [InlineData("save1.sav\u0000")]
    [InlineData("save.sav")]
    [InlineData("save01.SAV")]
    public void MaliciousOrNonNativeNamesAreRejected(string candidate)
    {
        Assert.False(EmueraSavePathPolicy.TryParse(RuntimeSaveLayout.Root, candidate, out _));
    }

    [Fact]
    public void CanonicalizationAndCollisionChecksAreUnicodeAndCaseInsensitive()
    {
        EmueraSavePath canonical = EmueraSavePathPolicy.Parse(RuntimeSaveLayout.SavDirectory, "profile/save1.sav");
        string decomposed = "profile/sa" + "ve1.sav".Normalize(NormalizationForm.FormD);

        Assert.Equal("profile/save1.sav", canonical.Value);
        Assert.True(EmueraSavePathPolicy.AreCollisionFree(["profile/save1.sav", "profile/save2.sav"]));
        Assert.False(EmueraSavePathPolicy.AreCollisionFree(["save1.sav", "SAVE1.SAV"]));
        Assert.True(EmueraSavePathPolicy.AreCollisionFree([decomposed]));
    }

    [Fact]
    public void RuntimeOnlyCompatibilityFlagsCanReadPhysicalAndRootAuxiliaryLocations()
    {
        Assert.True(EmueraSavePathPolicy.TryParse(RuntimeSaveLayout.Root, "txt1.txt", out EmueraSavePath rootText, allowAuxiliaryInRoot: true));
        Assert.Equal(EmueraSaveFileKind.AuxiliaryText, rootText.Kind);
        Assert.True(EmueraSavePathPolicy.TryParse(RuntimeSaveLayout.SavDirectory, "sav/save1.sav", out _, allowPhysicalSavPrefix: true));
    }
}
