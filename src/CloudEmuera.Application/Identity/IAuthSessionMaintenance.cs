namespace CloudEmuera.Application.Identity;

public interface IAuthSessionMaintenance
{
    /// <summary>Deletes at most <paramref name="batchSize"/> expired or revoked sessions.</summary>
    Task<int> CleanupAsync(int batchSize, CancellationToken cancellationToken = default);
}
