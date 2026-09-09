namespace CloudEmuera.Contracts.Identity;

public sealed record UpdateUiLocaleRequest(string Locale);

public sealed record UiLocalePreferenceResponse(string Locale, int StateVersion);

public sealed record SessionStartupDefaultsResponse(
    string FontFaceId,
    int FontSize,
    int LineHeight,
    string WidthMode,
    int? CustomWidth,
    bool ConvertBackslashToYen,
    string FontSizeLineHeightMode);

public sealed record UpdateSessionStartupDefaultsRequest(
    string FontFaceId,
    int FontSize,
    int LineHeight,
    string WidthMode = "ADAPTIVE",
    int? CustomWidth = null,
    bool ConvertBackslashToYen = true,
    string FontSizeLineHeightMode = "OVERRIDE");
