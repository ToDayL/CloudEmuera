namespace CloudEmuera.Infrastructure.Persistence;

public sealed class GameFileRow
{
    public string GameId { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string LogicalPath { get; set; } = string.Empty;
    public string EntryKind { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public string? ContentDigest { get; set; }
    public string? FileKind { get; set; }
    public string? TextEncoding { get; set; }
    public bool? HasBom { get; set; }
    public GameRow? Game { get; set; }
}
