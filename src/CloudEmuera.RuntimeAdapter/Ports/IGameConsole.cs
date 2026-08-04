namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Minimal platform-neutral console operation envelope. The final structured
/// event and snapshot semantics belong to P0-03.
/// </summary>
public sealed record GameConsoleOperation
{
    public GameConsoleOperation(string kind, string? text = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        Kind = kind;
        Text = text;
    }

    public string Kind { get; }

    public string? Text { get; }
}

/// <summary>
/// Minimal platform-neutral input prompt envelope. It is not the final P0-03
/// promptId/sequence contract.
/// </summary>
public sealed record GameConsolePrompt
{
    public GameConsolePrompt(string kind, string? text = null, string? promptId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        Kind = kind;
        Text = text;
        PromptId = promptId;
    }

    public string Kind { get; }

    public string? Text { get; }

    public string? PromptId { get; }
}

public sealed record GameConsoleInput
{
    public GameConsoleInput(string value, string? kind = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
        Kind = kind;
    }

    public string Value { get; }

    public string? Kind { get; }
}

/// <summary>
/// Runtime console boundary. It has no dependency on a window, control, font,
/// color or desktop event loop.
/// </summary>
public interface IGameConsole
{
    void Emit(GameConsoleOperation operation);

    GameConsoleInput Read(GameConsolePrompt prompt, CancellationToken cancellationToken = default);
}
