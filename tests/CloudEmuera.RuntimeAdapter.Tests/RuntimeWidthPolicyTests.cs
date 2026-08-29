using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests;

public sealed class RuntimeWidthPolicyTests
{
    [Theory]
    [InlineData(RuntimeWidthMode.Original, null, 640, 760)]
    [InlineData(RuntimeWidthMode.Max, null, 2_500, 2_000)]
    [InlineData(RuntimeWidthMode.Max, null, 900, 900)]
    [InlineData(RuntimeWidthMode.Adaptive, null, 2_500, 760)]
    [InlineData(RuntimeWidthMode.Adaptive, null, 640, 640)]
    [InlineData(RuntimeWidthMode.Custom, 1_200, 900, 1_200)]
    [InlineData(RuntimeWidthMode.Custom, 1_200, 1_600, 1_200)]
    [Trait("Category", "RuntimePaths")]
    public void ResolveSelectsTheFourWidthModeSemantics(RuntimeWidthMode mode, int? customWidth, int browserWidth, int expectedWidth)
    {
        // SESS-014/PLAY-015: Original and Custom are not capped by the
        // browser; Max keeps the existing 2000px cap; Adaptive is the old
        // Origin min(configured, browser) behavior.
        Assert.Equal(expectedWidth, RuntimeWidthPolicy.Resolve(760, browserWidth, mode, customWidth));
    }

    [Theory]
    [InlineData(RuntimeWidthMode.Original, null)]
    [InlineData(RuntimeWidthMode.Max, null)]
    [InlineData(RuntimeWidthMode.Adaptive, null)]
    [Trait("Category", "RuntimePaths")]
    public void NonCustomModesRejectCustomWidth(RuntimeWidthMode mode, int? customWidth)
    {
        Assert.False(RuntimeWidthPolicy.IsValid(mode, 1_200));
        Assert.True(RuntimeWidthPolicy.IsValid(mode, customWidth));
    }
}
