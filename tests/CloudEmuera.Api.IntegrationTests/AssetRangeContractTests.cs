using System.Text;
using CloudEmuera.Api;
using Xunit;

namespace CloudEmuera.Api.IntegrationTests;

[Trait("Category", "SessionAssets")]
public sealed class AssetRangeContractTests
{
    [Theory]
    [InlineData("", 10, true, 0, 10)]
    [InlineData("bytes=0-0", 10, true, 0, 1)]
    [InlineData("bytes=2-5", 10, true, 2, 4)]
    [InlineData("bytes=8-", 10, true, 8, 2)]
    [InlineData("bytes=-3", 10, true, 7, 3)]
    [InlineData("bytes=0-999", 10, true, 0, 10)]
    [InlineData("bytes=10-", 10, false, 0, 0)]
    [InlineData("bytes=5-2", 10, false, 0, 0)]
    [InlineData("bytes=abc", 10, false, 0, 0)]
    [InlineData("bytes=0-1,3-4", 10, false, 0, 0)]
    [InlineData("bytes=-0", 10, false, 0, 0)]
    [InlineData("bytes=-1", 0, false, 0, 0)]
    public void TrySingleRangeHandlesBoundariesAndRejectsAmbiguousRanges(string header, long total, bool expected, long expectedStart, long expectedLength)
    {
        bool actual = ApiIdentity.TrySingleRange(header, total, out long start, out long length);
        Assert.Equal(expected, actual);
        if (expected)
        {
            Assert.Equal(expectedStart, start);
            Assert.Equal(expectedLength, length);
        }
    }

    [Fact]
    public async Task BoundedReadStreamNeverReadsBeyondTheRequestedLength()
    {
        await using var source = new MemoryStream(Encoding.ASCII.GetBytes("0123456789"));
        source.Position = 3;
        await using var bounded = new BoundedReadStream(source, 4);

        using var result = new MemoryStream();
        await bounded.CopyToAsync(result);

        Assert.Equal("3456", Encoding.ASCII.GetString(result.ToArray()));
        Assert.Equal(4, bounded.Position);
        Assert.Equal(0, await bounded.ReadAsync(new byte[16]));
        Assert.Throws<NotSupportedException>(() => bounded.Seek(0, SeekOrigin.Begin));
    }
}
