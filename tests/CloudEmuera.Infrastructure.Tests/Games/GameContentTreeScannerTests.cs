using CloudEmuera.Infrastructure.Games;

namespace CloudEmuera.Infrastructure.Tests.Games;

public sealed class GameContentTreeScannerTests
{
    [Fact]
    [Trait("Category", "GameLibrary")]
    public async Task LargeContentTreeIsRejectedAtTheBoundedEntryLimit()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cloudemuera-scan-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(root);
        try
        {
            const int testLimit = 8;
            for (int index = 0; index <= testLimit; index++)
                await File.WriteAllTextAsync(Path.Combine(root, $"{index:D5}.TXT"), string.Empty);

            GameContentLimitException exception = Assert.Throws<GameContentLimitException>(() =>
                GameContentTreeScanner.Scan(root, maxEntryCount: testLimit));
            Assert.Equal("GAME_CONTENT_ENTRY_LIMIT", exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
