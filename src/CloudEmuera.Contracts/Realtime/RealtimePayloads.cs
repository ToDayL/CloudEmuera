using System.Text.Json.Serialization;

namespace CloudEmuera.Contracts.Realtime;

public sealed record RealtimeSnapshot(
    ulong WorkerEpoch,
    long SnapshotSequence,
    RealtimeConsoleState ConsoleState);

public sealed record RealtimeTransactionBatch(
    ulong WorkerEpoch,
    long FirstSequence,
    long LastSequence,
    IReadOnlyList<RealtimeTransaction> Transactions);

public sealed record RealtimeResyncRequired(
    ulong WorkerEpoch,
    long ObservedSequence,
    string Reason);

public sealed record RealtimeConsoleState(
    IReadOnlyList<RealtimeLine> Scrollback,
    IReadOnlyList<RealtimeBackgroundLayer> BackgroundLayers,
    RealtimeCanvasScene CanvasScene,
    RealtimeMediaState MediaState,
    RealtimePrompt? CurrentPrompt,
    RealtimeWindowMetadata WindowMetadata,
    RealtimeTruncation Truncation);

public sealed record RealtimeLine(
    string LineId,
    IReadOnlyList<RealtimeNode> Nodes,
    string Alignment,
    bool Temporary);

/// <summary>
/// Closed display union. Type is a protocol discriminator, not CLR type
/// metadata; only fields applicable to that discriminator are populated.
/// </summary>
public sealed record RealtimeNode(
    string Type,
    string? Text = null,
    RealtimeTextStyle? Style = null,
    IReadOnlyList<RealtimeNode>? Children = null,
    string? Value = null,
    string? Tooltip = null,
    bool? Enabled = null,
    long? Generation = null,
    string? AssetId = null,
    RealtimeRect? SourceRect = null,
    RealtimeRect? Destination = null,
    string? AltText = null,
    bool? Decorative = null,
    int? ZIndex = null,
    int? Frame = null,
    float? Opacity = null,
    string? HoverAssetId = null,
    RealtimeRect? HoverSourceRect = null,
    string? MappingAssetId = null,
    RealtimeRect? MappingSourceRect = null,
    IReadOnlyList<RealtimeSpriteAnimationFrame>? AnimationFrames = null,
    string? Shape = null,
    RealtimeColor? Fill = null,
    RealtimeColor? Stroke = null,
    IReadOnlyList<RealtimePoint>? Points = null,
    RealtimeHtmlNode? Root = null,
    RealtimeRect? Layout = null,
    RealtimeRect? Bounds = null);

public sealed record RealtimeSpriteAnimationFrame(
    string AssetId,
    RealtimeRect SourceRect,
    RealtimePoint Offset,
    int DurationMilliseconds);

public sealed record RealtimeHtmlNode(
    string Type,
    string? Text = null,
    string? Tag = null,
    IReadOnlyList<RealtimeHtmlNode>? Children = null,
    RealtimeTextStyle? Style = null,
    string? AssetId = null,
    string? AltText = null);

public sealed record RealtimeTextStyle(
    IReadOnlyList<string> Decorations,
    string FontFamily,
    int FontSize,
    int LineHeight,
    RealtimeColor? Foreground = null,
    RealtimeColor? Background = null);

public sealed record RealtimeColor(byte Red, byte Green, byte Blue, byte Alpha);

public sealed record RealtimePoint(int X, int Y);

public sealed record RealtimeRect(int X, int Y, int Width, int Height);

public sealed record RealtimeBackgroundLayer(
    string LayerId,
    string AssetId,
    string Mode,
    float Opacity,
    long Depth);

public sealed record RealtimeCanvasScene(
    IReadOnlyList<RealtimeDrawable> Drawables,
    IReadOnlyList<RealtimeHitRegion> HitRegions);

public sealed record RealtimeDrawable(
    string Type,
    string DrawableId,
    RealtimeRect Bounds,
    int ZIndex,
    float Opacity,
    string? AssetId = null,
    RealtimeRect? SourceRect = null,
    int? Frame = null,
    IReadOnlyList<RealtimeSpriteAnimationFrame>? AnimationFrames = null,
    string? Shape = null,
    RealtimeColor? Fill = null,
    RealtimeColor? Stroke = null,
    IReadOnlyList<RealtimePoint>? Points = null,
    RealtimeHtmlNode? Root = null,
    byte[]? PngData = null,
    byte[]? HoverPngData = null,
    bool? HitTestMap = null);

public sealed record RealtimeHitRegion(
    string RegionId,
    RealtimeRect Bounds,
    string InputValue,
    bool Enabled,
    string? Tooltip = null);

public sealed record RealtimeMediaState(IReadOnlyList<RealtimeMediaChannel> Channels);

public sealed record RealtimeMediaChannel(
    string Channel,
    string? AssetId,
    string PlaybackState,
    bool Loop,
    float Volume,
    long Revision,
    string StartPolicy);

public sealed record RealtimePrompt(
    string PromptId,
    string InputType,
    string? PromptText,
    string? DefaultValue,
    RealtimeInputConstraints Constraints,
    string TimeoutBehavior,
    string TimeoutAction,
    string[] AllowedSources,
    bool OneInput,
    bool SystemInput,
    bool StopMessageSkip,
    bool DisplayTime,
    string? TimeoutMessage,
    long OpenedAtUnixMilliseconds,
    long DeadlineUnixMilliseconds,
    long? TimeoutMilliseconds);

public sealed record RealtimeInputConstraints(
    string Type,
    int? MaxLength = null,
    long? Minimum = null,
    long? Maximum = null,
    bool? AllowSign = null,
    bool? AllowControlCharacters = null);

public sealed record RealtimeWindowMetadata(
    string Title,
    int ViewportWidth,
    int ViewportHeight,
    RealtimeColor? DefaultForeground,
    RealtimeColor? DefaultBackground,
    RealtimeFontSpec DefaultFont);

public sealed record RealtimeFontSpec(string Family, int Size, int LineHeight);

public sealed record RealtimeTruncation(
    bool WasTruncated,
    long DroppedNodeCount,
    long DroppedLineCount,
    long DroppedTextLength);

public sealed record RealtimeTransaction(long Sequence, IReadOnlyList<RealtimeOperation> Operations);

/// <summary>Closed operation union used by the browser payload.</summary>
public sealed record RealtimeOperation(
    string Type,
    IReadOnlyList<RealtimeNode>? Nodes = null,
    RealtimePrompt? Prompt = null,
    string? PromptId = null,
    string? Reason = null,
    RealtimeLine? Line = null,
    string? LineId = null,
    IReadOnlyList<string>? LineIds = null,
    RealtimeWindowMetadata? WindowMetadata = null,
    RealtimeBackgroundLayer? BackgroundLayer = null,
    string? LayerId = null,
    RealtimeDrawable? Drawable = null,
    string? DrawableId = null,
    int? MinimumZIndex = null,
    int? MaximumZIndex = null,
    RealtimeHitRegion? HitRegion = null,
    string? RegionId = null,
    RealtimeMediaChannel? MediaChannel = null,
    string? Channel = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(RealtimeSnapshot))]
[JsonSerializable(typeof(RealtimeTransactionBatch))]
[JsonSerializable(typeof(RealtimeResyncRequired))]
[JsonSerializable(typeof(RealtimeTransaction))]
public partial class RealtimeJsonContext : JsonSerializerContext;
