namespace CloudEmuera.Domain.Sessions;

/// <summary>Controls whether a Session supplies or preserves the game's text metrics.</summary>
public enum SessionFontSizeLineHeightMode
{
    /// <summary>Use the persisted Session FontSize and LineHeight values.</summary>
    Override = 0,

    /// <summary>Leave the values loaded from the game's emuera.config unchanged.</summary>
    Config = 1,
}
