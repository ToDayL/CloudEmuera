using System.Globalization;
using CloudEmuera.RuntimeAdapter;
using Xunit;

namespace CloudEmuera.RuntimeAdapter.Tests.Paths;

[Trait("Category", "RuntimePaths")]
public sealed class RuntimeRelativePathTests
{
    [Theory]
    [InlineData("CSV/GAMEBASE.CSV")]
    [InlineData("ERB/START.ERB")]
    [InlineData("sav/save00.sav")]
    [InlineData("emoji/猫😀.txt")]
    [InlineData("ReadMe/eraSQN\u0083p\u0083b\u0083`.txt")]
    [InlineData("file:game")]
    [InlineData("foo ")]
    [InlineData("foo.")]
    [InlineData("CON.txt")]
    public void ValidPathsKeepStableOrdinalSlashForm(string value)
    {
        RuntimeRelativePath path = RuntimeRelativePath.Parse(value);

        Assert.Equal(value, path.Value);
        Assert.Equal(value, path.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("a//b")]
    [InlineData("a/")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\game\\file")]
    [InlineData("\\\\server\\share")]
    [InlineData("a\\b")]
    [InlineData("a\0b")]
    public void UnsafePathsAreRejected(string value)
    {
        Assert.False(RuntimeRelativePath.TryParse(value, out _));
        RuntimePathException exception = Assert.Throws<RuntimePathException>(() => RuntimeRelativePath.Parse(value));
        Assert.Equal(RuntimePathReasonCodes.InvalidRelativePath, exception.ReasonCode);
    }

    [Fact]
    public void LengthSegmentAndCountBoundariesAreFixed()
    {
        string segment = new('a', RuntimeRelativePath.MaxSegmentLength);
        Assert.True(RuntimeRelativePath.TryParse(segment, out _));
        Assert.False(RuntimeRelativePath.TryParse(segment + "a", out _));

        string segments = string.Join('/', Enumerable.Repeat("a", RuntimeRelativePath.MaxSegmentCount));
        Assert.True(RuntimeRelativePath.TryParse(segments, out _));
        Assert.False(RuntimeRelativePath.TryParse(segments + "/a", out _));

        string lengthBoundary = string.Join(
            '/',
            Enumerable.Repeat(new string('a', RuntimeRelativePath.MaxSegmentLength), RuntimeRelativePath.MaxSegmentCount));
        lengthBoundary = lengthBoundary[..RuntimeRelativePath.MaxLength];
        while (lengthBoundary.EndsWith('/'))
        {
            lengthBoundary = lengthBoundary[..^1];
        }
        Assert.True(RuntimeRelativePath.TryParse(lengthBoundary, out _));
        Assert.False(RuntimeRelativePath.TryParse(lengthBoundary + "a", out _));
    }

    [Fact]
    public void ComparisonDoesNotUseCurrentCulture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            RuntimeRelativePath upper = RuntimeRelativePath.Parse("I/file");
            RuntimeRelativePath lower = RuntimeRelativePath.Parse("i/file");

            Assert.NotEqual(upper, lower);
            Assert.True(upper.CompareTo(lower) != 0);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
