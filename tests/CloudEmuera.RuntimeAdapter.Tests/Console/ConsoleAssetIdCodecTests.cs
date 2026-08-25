using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.ConsoleContract;

[Trait("Category", "ConsoleContract")]
public sealed class ConsoleAssetIdCodecTests
{
    [Fact]
    public void PathAssetIdsRoundTripNormalizedLogicalPaths()
    {
        string id = ConsoleAssetIdCodec.EncodePath("images/背景/e\u0301.png");

        Assert.StartsWith(ConsoleAssetIdCodec.PathPrefix, id, StringComparison.Ordinal);
        Assert.True(ConsoleAssetIdCodec.TryDecodePath(id, out string decoded));
        Assert.Equal("images/背景/é.png", decoded);
        _ = new ConsoleAssetId(id);
    }

    [Fact]
    public void LegacyDigestAliasesRemainReadable()
    {
        Assert.True(ConsoleAssetIdCodec.TryGetLegacyDigest(
            "sha256-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            out string digest));
        Assert.Equal("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", digest);
        Assert.True(ConsoleAssetIdCodec.IsLegacyDigestId(
            "sha256-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
    }

    [Theory]
    [InlineData("path-", false)]
    [InlineData("path-Li4vc2VjcmV0", false)]
    [InlineData("path-YQ", true)]
    [InlineData("path-!", false)]
    [InlineData("sha256-not-a-digest", false)]
    public void AssetAliasesAreValidated(string value, bool expected)
    {
        Assert.Equal(expected, ConsoleAssetIdCodec.TryDecodePath(value, out _));
    }

    [Fact]
    public void OversizedPathAliasesAreRejectedBeforeDecoding()
    {
        Assert.False(ConsoleAssetIdCodec.TryDecodePath("path-" + new string('A', 100_000), out _));
    }
}
