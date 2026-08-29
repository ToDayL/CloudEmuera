using CloudEmuera.Application.Fonts;
using CloudEmuera.Domain.Sessions;

namespace CloudEmuera.Application.Identity;

public sealed record SessionStartupDefaults(
    string FontFaceId,
    int FontSize,
    int LineHeight,
    SessionWidthMode WidthMode,
    int? CustomWidth,
    bool ConvertBackslashToYen = true)
{
    public static SessionStartupDefaults Default { get; } = new(
        RuntimeFontDefaults.DefaultFaceId,
        FontSize: 18,
        LineHeight: 19,
        WidthMode: SessionWidthMode.Adaptive,
        CustomWidth: null,
        ConvertBackslashToYen: true);
}

public sealed record SessionStartupDefaultsCommand(
    string FontFaceId,
    int FontSize,
    int LineHeight,
    SessionWidthMode WidthMode = SessionWidthMode.Adaptive,
    int? CustomWidth = null,
    bool ConvertBackslashToYen = true);
