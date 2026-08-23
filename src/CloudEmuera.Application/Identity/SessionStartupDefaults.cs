using CloudEmuera.Application.Fonts;
using CloudEmuera.Domain.Sessions;

namespace CloudEmuera.Application.Identity;

public sealed record SessionStartupDefaults(
    string FontFaceId,
    int FontSize,
    int LineHeight,
    SessionWidthMode WidthMode,
    int? CustomWidth)
{
    public static SessionStartupDefaults Default { get; } = new(
        RuntimeFontDefaults.DefaultFaceId,
        FontSize: 18,
        LineHeight: 19,
        WidthMode: SessionWidthMode.Origin,
        CustomWidth: null);
}

public sealed record SessionStartupDefaultsCommand(
    string FontFaceId,
    int FontSize,
    int LineHeight,
    SessionWidthMode WidthMode = SessionWidthMode.Origin,
    int? CustomWidth = null);
