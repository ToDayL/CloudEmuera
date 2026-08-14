namespace CloudEmuera.Application.Sessions;

/// <summary>
/// Transport-neutral browser input source.  The API maps the closed
/// WebSocket discriminator to this value; the Worker remains the authority
/// for prompt, format, timeout and duplicate semantics.
/// </summary>
[Flags]
public enum SessionInputSource
{
    None = 0,
    Keyboard = 1,
    Button = 2,
    PointerDevice = 4,
    System = 8,
}

public sealed record SessionPointerInput(int X, int Y, int Button, bool Pressed);

public sealed record SessionKeyInput(int KeyCode, bool Control, bool Alt, bool Shift);

public sealed record SessionInputCommand(
    string SessionId,
    ulong WorkerEpoch,
    string PromptId,
    string ClientMessageId,
    string Value,
    SessionInputSource Source,
    SessionPointerInput? PointerData = null,
    SessionKeyInput? Key = null);

/// <summary>
/// Stable result codes used between the realtime adapter and the Worker
/// router.  They deliberately do not mention WebSocket or protobuf types.
/// </summary>
public static class SessionInputResultCodes
{
    public const string Accepted = "accepted";
    public const string Duplicate = "duplicate";
    public const string Conflict = "conflict";
    public const string StalePrompt = "stale_prompt";
    public const string NoActivePrompt = "no_active_prompt";
    public const string InvalidFormat = "invalid_format";
    public const string InvalidCommand = "invalid_command";
    public const string Forbidden = "forbidden";
    public const string Cancelled = "cancelled";
    public const string TimedOut = "timed_out";
    public const string SessionNotAcceptingInput = "session_not_accepting_input";
    public const string StaleEpoch = "stale_epoch";
    public const string SessionNotRunning = "session_not_running";
    public const string InputBackpressure = "input_backpressure";
    public const string WorkerUnavailable = "worker_unavailable";
}

public sealed record SessionInputResult(
    string PromptId,
    string ClientMessageId,
    string Status,
    string ReasonCode,
    string? NormalizedValue = null);

/// <summary>
/// The in-process linearization point shared by lifecycle close and realtime
/// input.  It only orders external side effects; SQLite remains authoritative
/// for the persistent Session state and Worker binding.
/// </summary>
public interface ISessionCommandGate
{
    Task<SessionCommandLease> EnterAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}

public sealed class SessionCommandLease(Action release) : IAsyncDisposable, IDisposable
{
    private Action? release = release ?? throw new ArgumentNullException(nameof(release));

    public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Optional persistence adapter used by the realtime route.</summary>
public interface ICurrentSessionRuntimeLeaseReader
{
    Task<Runtime.SessionRuntimeLease?> GetCurrentLeaseAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
