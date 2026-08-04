namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Controlled logical file areas exposed to the interpreter.
/// </summary>
public enum RuntimeFileArea
{
    GameContent = 0,
    Configuration = 1,
    Save = 2,
    Temporary = 3
}
