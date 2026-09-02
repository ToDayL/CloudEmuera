namespace CloudEmuera.RuntimeAdapter;

/// <summary>Controls whether the headless runtime overrides the game's text metrics.</summary>
public enum RuntimeFontSizeLineHeightMode
{
    /// <summary>Use the values supplied by the Session.</summary>
    Override = 0,

    /// <summary>Keep the values loaded from the game's emuera.config.</summary>
    Config = 1,
}
