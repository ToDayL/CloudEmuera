using CloudEmuera.Application.Fonts;

namespace CloudEmuera.Application.Identity;

public sealed record SessionStartupDefaults(
    string FontFaceId,
    int FontSize,
    int LineHeight)
{
    public static SessionStartupDefaults Default { get; } = new(
        RuntimeFontDefaults.DefaultFaceId,
        FontSize: 18,
        LineHeight: 19);
}

public sealed record SessionStartupDefaultsCommand(
    string FontFaceId,
    int FontSize,
    int LineHeight);
