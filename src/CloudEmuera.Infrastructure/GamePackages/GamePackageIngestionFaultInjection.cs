namespace CloudEmuera.Infrastructure.GamePackages;

public enum GamePackageIngestionFaultPoint
{
    BeforeArchiveWrite,
    BeforePublishRename,
    BeforeReadyCas,
    BeforeAuditCommit,
    BeforeAnalyze,
    BeforeAbandonCas,
}

/// <summary>Deterministic fault seam for filesystem and transaction recovery verification.</summary>
public interface IGamePackageIngestionFaultInjector
{
    ValueTask InjectAsync(GamePackageIngestionFaultPoint point, CancellationToken cancellationToken);
}
