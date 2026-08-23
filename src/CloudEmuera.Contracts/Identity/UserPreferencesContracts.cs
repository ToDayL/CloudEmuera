namespace CloudEmuera.Contracts.Identity;

public sealed record SessionStartupDefaultsResponse(
    string FontFaceId,
    int FontSize,
    int LineHeight);

public sealed record UpdateSessionStartupDefaultsRequest(
    string FontFaceId,
    int FontSize,
    int LineHeight);
