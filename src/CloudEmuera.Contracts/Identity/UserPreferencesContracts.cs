namespace CloudEmuera.Contracts.Identity;

public sealed record SessionStartupDefaultsResponse(
    string FontFaceId,
    int FontSize,
    int LineHeight,
    string WidthMode,
    int? CustomWidth,
    bool ConvertBackslashToYen);

public sealed record UpdateSessionStartupDefaultsRequest(
    string FontFaceId,
    int FontSize,
    int LineHeight,
    string WidthMode = "ADAPTIVE",
    int? CustomWidth = null,
    bool ConvertBackslashToYen = true);
