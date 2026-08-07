using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEventRow>
{
    public void Configure(EntityTypeBuilder<AuditEventRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.AuditEventsTable, table =>
        {
            table.HasCheckConstraint("ck_audit_events_id", SqliteCheckExpressions.IdentifierPrefix("id", "audit_"));
            table.HasCheckConstraint("ck_audit_events_actor_user_id", "actor_user_id IS NULL OR (length(actor_user_id) BETWEEN 5 AND 64 AND instr(actor_user_id, char(0)) = 0)");
            table.HasCheckConstraint("ck_audit_events_actor_type", "actor_type IN ('USER', 'ADMIN', 'SYSTEM')");
            table.HasCheckConstraint("ck_audit_events_action", "length(action) BETWEEN 1 AND 128 AND instr(action, char(0)) = 0");
            table.HasCheckConstraint("ck_audit_events_resource_type", "length(resource_type) BETWEEN 1 AND 64 AND instr(resource_type, char(0)) = 0");
            table.HasCheckConstraint("ck_audit_events_resource_id", "length(resource_id) BETWEEN 1 AND 128 AND instr(resource_id, char(0)) = 0");
            table.HasCheckConstraint("ck_audit_events_request_id", "request_id IS NULL OR (length(request_id) BETWEEN 1 AND 128 AND instr(request_id, char(0)) = 0)");
            table.HasCheckConstraint("ck_audit_events_result", "result IN ('SUCCEEDED', 'FAILED')");
            table.HasCheckConstraint("ck_audit_events_reason_code", "reason_code IS NULL OR (length(reason_code) BETWEEN 1 AND 128 AND instr(reason_code, char(0)) = 0)");
            table.HasCheckConstraint("ck_audit_events_metadata_json", SqliteCheckExpressions.Json.Replace("{0}", "metadata_json", StringComparison.Ordinal));
            table.HasCheckConstraint("ck_audit_events_occurred_at", "occurred_at >= 0");
        });

        builder.HasKey(row => row.Id).HasName("pk_audit_events");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength).IsRequired();
        ConfigureTime(builder.Property(row => row.OccurredAt), "occurred_at");
        builder.Property(row => row.ActorUserId).HasColumnName("actor_user_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength);
        builder.Property(row => row.ActorType).HasColumnName("actor_type").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<AuditActorType>(), SqliteValueConverters.CreateEnumComparer<AuditActorType>()).IsRequired();
        builder.Property(row => row.Action).HasColumnName("action").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ActionMaxLength).IsRequired();
        builder.Property(row => row.ResourceType).HasColumnName("resource_type").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ResourceTypeMaxLength).IsRequired();
        builder.Property(row => row.ResourceId).HasColumnName("resource_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ResourceIdMaxLength).IsRequired();
        builder.Property(row => row.RequestId).HasColumnName("request_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.RequestIdMaxLength);
        builder.Property(row => row.Result).HasColumnName("result").HasColumnType("TEXT").HasConversion(SqliteValueConverters.CreateEnumConverter<AuditResult>(), SqliteValueConverters.CreateEnumComparer<AuditResult>()).IsRequired();
        builder.Property(row => row.ReasonCode).HasColumnName("reason_code").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.ReasonCodeMaxLength);
        builder.Property(row => row.MetadataJson).HasColumnName("metadata_json").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.JsonMaxLength).HasDefaultValue("{}").IsRequired();

        builder.HasIndex(row => row.OccurredAt).HasDatabaseName("ix_audit_events_occurred_at");
        builder.HasIndex(row => new { row.ResourceType, row.ResourceId, row.OccurredAt }).HasDatabaseName("ix_audit_events_resource_time");
        builder.HasIndex(row => new { row.ActorUserId, row.OccurredAt }).HasDatabaseName("ix_audit_events_actor_time");
    }

    private static void ConfigureTime(PropertyBuilder<DateTimeOffset> property, string name) =>
        property.HasColumnName(name).HasColumnType("INTEGER").HasConversion(SqliteValueConverters.DateTimeOffsetToUnixMilliseconds, SqliteValueConverters.DateTimeOffsetComparer).IsRequired();
}
