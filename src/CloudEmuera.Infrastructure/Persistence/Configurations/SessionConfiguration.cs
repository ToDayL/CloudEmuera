using CloudEmuera.Domain.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class SessionConfiguration : IEntityTypeConfiguration<SessionRow>
{
    public void Configure(EntityTypeBuilder<SessionRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.SessionsTable, table =>
        {
            table.HasCheckConstraint("ck_sessions_id", SqliteCheckExpressions.IdentifierPrefix("id", "sess_"));
            table.HasCheckConstraint("ck_sessions_owner_id", SqliteCheckExpressions.IdentifierPrefix("owner_user_id", "usr_"));
            table.HasCheckConstraint("ck_sessions_game_id", SqliteCheckExpressions.IdentifierPrefix("game_id", "game_"));
            table.HasCheckConstraint("ck_sessions_source_digest", "length(source_content_digest) = 71 AND substr(source_content_digest, 1, 7) = 'sha256:' AND lower(source_content_digest) = source_content_digest AND substr(source_content_digest, 8) NOT GLOB '*[^0-9a-f]*' AND length(substr(source_content_digest, 8)) = 64");
            table.HasCheckConstraint("ck_sessions_source_revision", "source_content_revision > 0");
            table.HasCheckConstraint("ck_sessions_manifest_digest", "length(session_root_manifest_digest) BETWEEN 1 AND 128 AND instr(session_root_manifest_digest, char(0)) = 0");
            table.HasCheckConstraint("ck_sessions_save_layout", "save_layout IN (0, 1)");
            table.HasCheckConstraint("ck_sessions_runtime_version", "length(runtime_version) BETWEEN 1 AND 128 AND instr(runtime_version, char(0)) = 0");
            table.HasCheckConstraint("ck_sessions_root_path", SqliteCheckExpressions.RelativePath("session_root_path"));
            table.HasCheckConstraint("ck_sessions_name", "length(name) BETWEEN 1 AND 200 AND instr(name, char(0)) = 0");
            table.HasCheckConstraint("ck_sessions_state", "state IN ('CREATING', 'STARTING', 'RUNNING', 'STOPPING', 'CLOSED', 'CRASHED')");
            table.HasCheckConstraint("ck_sessions_counters", "state_version >= 0 AND worker_epoch >= 0 AND last_output_sequence >= 0");
            table.HasCheckConstraint("ck_sessions_waiting_prompt", "waiting_for_input IN (0, 1) AND ((waiting_for_input = 1 AND current_prompt_id IS NOT NULL AND length(current_prompt_id) BETWEEN 1 AND 256) OR (waiting_for_input = 0 AND current_prompt_id IS NULL))");
            table.HasCheckConstraint("ck_sessions_close_reason", "close_reason IS NULL OR (length(close_reason) BETWEEN 1 AND 256 AND instr(close_reason, char(0)) = 0)");
            table.HasCheckConstraint("ck_sessions_time_order", "created_at >= 0 AND last_activity_at >= created_at AND (started_at IS NULL OR started_at >= created_at) AND (closed_at IS NULL OR closed_at >= created_at)");
            table.HasCheckConstraint("ck_sessions_closed_fields", "((state IN ('CLOSED', 'CRASHED') AND closed_at IS NOT NULL) OR (state NOT IN ('CLOSED', 'CRASHED') AND closed_at IS NULL)) AND ((state IN ('CREATING', 'CLOSED', 'CRASHED') AND waiting_for_input = 0 AND current_prompt_id IS NULL) OR state NOT IN ('CREATING', 'CLOSED', 'CRASHED'))");
        });

        builder.HasKey(row => row.Id).HasName("pk_sessions");
        builder.HasAlternateKey(row => new { row.Id, row.WorkerEpoch }).HasName("ak_sessions_id_worker_epoch");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.OwnerUserId).HasColumnName("owner_user_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.GameId).HasColumnName("game_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.SourceContentDigest).HasColumnName("source_content_digest").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.DigestLength).IsRequired();
        builder.Property(row => row.SourceContentRevision).HasColumnName("source_content_revision").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.SessionRootManifestDigest).HasColumnName("session_root_manifest_digest").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.SessionRootManifestDigestMaxLength).IsRequired();
        builder.Property(row => row.SaveLayout).HasColumnName("save_layout").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.RuntimeVersion).HasColumnName("runtime_version").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.RuntimeVersionMaxLength).IsRequired();
        builder.Property(row => row.SessionRootPath).HasColumnName("session_root_path").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength).IsRequired();
        builder.Property(row => row.Name).HasColumnName("name").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.NameMaxLength).IsRequired();
        builder.Property(row => row.State).HasColumnName("state").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<SessionState>(), SqliteValueConverters.CreateEnumComparer<SessionState>()).IsRequired();
        builder.Property(row => row.StateVersion).HasColumnName("state_version").HasColumnType("INTEGER").HasDefaultValue(0).IsRequired();
        builder.Property(row => row.WorkerEpoch).HasColumnName("worker_epoch").HasColumnType("INTEGER").HasDefaultValue(0).IsRequired();
        builder.Property(row => row.WaitingForInput).HasColumnName("waiting_for_input").HasColumnType("INTEGER").HasDefaultValue(false).IsRequired();
        builder.Property(row => row.CurrentPromptId).HasColumnName("current_prompt_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PromptIdMaxLength);
        builder.Property(row => row.LastOutputSequence).HasColumnName("last_output_sequence").HasColumnType("INTEGER").HasDefaultValue(0).IsRequired();
        builder.Property(row => row.CloseReason).HasColumnName("close_reason").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.CloseReasonMaxLength);
        ConfigureTime(builder.Property(row => row.CreatedAt), "created_at");
        builder.Property(row => row.StartedAt).HasColumnName("started_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);
        ConfigureTime(builder.Property(row => row.LastActivityAt), "last_activity_at");
        builder.Property(row => row.ClosedAt).HasColumnName("closed_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);

        builder.HasIndex(row => new { row.OwnerUserId, row.CreatedAt, row.Id }).HasDatabaseName("ix_sessions_owner_created").IsDescending(false, true, true);
        builder.HasIndex(row => new { row.OwnerUserId, row.State }).HasDatabaseName("ix_sessions_owner_state");
        builder.HasIndex(row => new { row.State, row.LastActivityAt }).HasDatabaseName("ix_sessions_state_activity");
        builder.HasIndex(row => row.GameId).HasDatabaseName("ix_sessions_game");
        builder.HasIndex(row => new { row.GameId, row.SourceContentDigest }).HasDatabaseName("ix_sessions_game_content_digest");
        builder.HasOne(row => row.OwnerUser).WithMany().HasForeignKey(row => row.OwnerUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sessions_owner_user");
        builder.HasOne(row => row.Game).WithMany(game => game.Sessions).HasForeignKey(row => row.GameId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_sessions_game");
    }

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}
