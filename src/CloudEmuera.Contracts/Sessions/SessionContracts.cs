using System.Text.Json;

namespace CloudEmuera.Contracts.Sessions;

public sealed record CreateSessionRequest(
    string GameId,
    string Name,
    string FontFaceId = "sarasa-fixed-sc-1.0.40-regular",
    int FontSize = 18,
    int LineHeight = 19);

public sealed record UpdateSessionConfigurationRequest(
    string Name,
    string FontFaceId = "sarasa-fixed-sc-1.0.40-regular",
    int FontSize = 18,
    int LineHeight = 19);

/// <summary>
/// The lifecycle endpoints intentionally accept only an empty JSON object.
/// The API adapter validates this shape before invoking the application port,
/// so force/deadline/autosave controls cannot be smuggled into the user API.
/// </summary>
public sealed record EmptySessionCommandRequest(JsonElement Body);

public sealed record SessionGameResponse(string Id, string Name);

public sealed record SessionResponse(
    int SchemaVersion,
    string Id,
    string Name,
    SessionGameResponse Game,
    string SourceContentDigest,
    long SourceContentRevision,
    string RuntimeVersion,
    string FontFaceId,
    int FontSize,
    int LineHeight,
    string State,
    int StateVersion,
    long WorkerEpoch,
    bool WaitingForInput,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset LastActivityAt,
    DateTimeOffset? ClosedAt,
    string? CloseReason);

public sealed record SessionListResponse(IReadOnlyList<SessionResponse> Items, string? NextCursor);
