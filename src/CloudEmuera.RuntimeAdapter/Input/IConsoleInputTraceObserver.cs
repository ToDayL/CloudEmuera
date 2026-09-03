namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Optional Worker-owned observation port. It sees only prompts actually
/// opened by the Runtime and their single terminal resolution; rejected and
/// inactive input attempts never cross this boundary.
/// </summary>
public interface IConsoleInputTraceObserver
{
    void PromptOpened(ConsolePrompt prompt);

    void PromptResolved(ConsolePrompt prompt, ConsoleInputResult result, ConsoleInputAttempt? attempt);
}
