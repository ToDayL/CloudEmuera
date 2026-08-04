namespace CloudEmuera.RuntimeAdapter;

/// <summary>
/// Source of prompt ids. An implementation must return a fresh id for every
/// call during its lifetime; the console deliberately does not retain the
/// complete prompt-id history. The default interface aliases make the small
/// contract convenient for deterministic test generators using either common
/// naming convention while the console calls <see cref="NextId"/>.
/// </summary>
public interface IPromptIdGenerator
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1716", Justification = "Next is retained as a deterministic test-generator alias.")]
    string Next()
    {
        throw new InvalidOperationException("The prompt id generator must implement Next or CreatePromptId.");
    }

    string CreatePromptId() => Next();

    string NextId() => CreatePromptId();
}

public sealed class GuidPromptIdGenerator : IPromptIdGenerator
{
    private readonly object sync = new();
    private readonly string sessionPrefix = Guid.NewGuid().ToString("N")[..16];
    private long counter;

    /// <summary>
    /// Generates ids without retaining prior ids: one random per-generator
    /// prefix plus a monotonic counter is unique for the generator lifetime.
    /// </summary>
    public string Next()
    {
        lock (sync)
        {
            if (counter == long.MaxValue)
            {
                throw new InvalidOperationException("The prompt id counter is exhausted.");
            }

            counter++;
            return $"{sessionPrefix}-{counter}";
        }
    }
}
