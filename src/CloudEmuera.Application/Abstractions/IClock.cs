namespace CloudEmuera.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

