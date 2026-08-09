using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class CompatibilityDiagnosticConfiguration : IEntityTypeConfiguration<CompatibilityDiagnosticRow>
{
    public void Configure(EntityTypeBuilder<CompatibilityDiagnosticRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.CompatibilityDiagnosticsTable, table =>
        {
            table.HasCheckConstraint("ck_compatibility_diagnostics_id", SqliteCheckExpressions.IdentifierPrefix("id", "diag_"));
            table.HasCheckConstraint("ck_compatibility_diagnostics_game_id", SqliteCheckExpressions.IdentifierPrefix("game_id", "game_"));
            table.HasCheckConstraint("ck_compatibility_diagnostics_revision", "workspace_revision >= 0");
            table.HasCheckConstraint("ck_compatibility_diagnostics_stage", "length(stage) BETWEEN 1 AND 32 AND instr(stage, char(0)) = 0");
            table.HasCheckConstraint("ck_compatibility_diagnostics_severity", "severity IN ('INFO', 'WARNING', 'ERROR')");
            table.HasCheckConstraint("ck_compatibility_diagnostics_code", "length(code) BETWEEN 1 AND 128 AND instr(code, char(0)) = 0");
            table.HasCheckConstraint("ck_compatibility_diagnostics_path", $"logical_path IS NULL OR ({SqliteCheckExpressions.RelativePath("logical_path")})");
            table.HasCheckConstraint("ck_compatibility_diagnostics_line", "line_number IS NULL OR line_number > 0");
            table.HasCheckConstraint("ck_compatibility_diagnostics_message", "length(message_key) BETWEEN 1 AND 256 AND instr(message_key, char(0)) = 0");
            table.HasCheckConstraint("ck_compatibility_diagnostics_arguments", SqliteCheckExpressions.ValidJson("arguments_json"));
            table.HasCheckConstraint("ck_compatibility_diagnostics_blocking", "activation_blocking IN (0, 1)");
            table.HasCheckConstraint("ck_compatibility_diagnostics_override", "override_policy IN ('NEVER', 'ADMIN') AND ((overridden_by IS NULL AND overridden_at IS NULL) OR (override_policy = 'ADMIN' AND overridden_by IS NOT NULL AND overridden_at IS NOT NULL))");
            table.HasCheckConstraint("ck_compatibility_diagnostics_created", "created_at >= 0");
        });
        builder.HasKey(row => row.Id).HasName("pk_compatibility_diagnostics");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.GameId).HasColumnName("game_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        builder.Property(row => row.WorkspaceRevision).HasColumnName("workspace_revision").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.Stage).HasColumnName("stage").HasColumnType("TEXT").HasMaxLength(32).IsRequired();
        builder.Property(row => row.Severity).HasColumnName("severity").HasColumnType("TEXT").HasMaxLength(16).IsRequired();
        builder.Property(row => row.Code).HasColumnName("code").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ReasonCodeMaxLength).IsRequired();
        builder.Property(row => row.LogicalPath).HasColumnName("logical_path").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.PathMaxLength);
        builder.Property(row => row.LineNumber).HasColumnName("line_number").HasColumnType("INTEGER");
        builder.Property(row => row.MessageKey).HasColumnName("message_key").HasColumnType("TEXT").HasMaxLength(256).IsRequired();
        builder.Property(row => row.ArgumentsJson).HasColumnName("arguments_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength).HasDefaultValue("{}").IsRequired();
        builder.Property(row => row.ActivationBlocking).HasColumnName("activation_blocking").HasColumnType("INTEGER").IsRequired();
        builder.Property(row => row.OverridePolicy).HasColumnName("override_policy").HasColumnType("TEXT").HasMaxLength(16).IsRequired();
        builder.Property(row => row.OverriddenBy).HasColumnName("overridden_by").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength);
        builder.Property(row => row.OverriddenAt).HasColumnName("overridden_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);
        builder.Property(row => row.CreatedAt).HasColumnName("created_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
        builder.HasIndex(row => new { row.GameId, row.WorkspaceRevision, row.ActivationBlocking }).HasDatabaseName("ix_compatibility_diagnostics_game_revision");
        builder.HasOne(row => row.Game).WithMany().HasForeignKey(row => row.GameId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_compatibility_diagnostics_game");
        builder.HasOne<CloudEmueraUser>().WithMany().HasForeignKey(row => row.OverriddenBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_compatibility_diagnostics_overridden_by");
    }
}
