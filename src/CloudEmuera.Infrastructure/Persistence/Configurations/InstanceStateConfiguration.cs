using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CloudEmuera.Infrastructure.Persistence;

internal sealed class InstanceStateConfiguration : IEntityTypeConfiguration<InstanceStateRow>
{
    public void Configure(EntityTypeBuilder<InstanceStateRow> builder)
    {
        builder.ToTable(SqliteStorageConventions.InstanceStateTable, table =>
        {
            table.HasCheckConstraint("ck_instance_state_id", "id = 1");
            table.HasCheckConstraint("ck_instance_state_status", "bootstrap_status IN ('BOOTSTRAP_REQUIRED', 'COMPLETED')");
            table.HasCheckConstraint("ck_instance_state_shape", "(bootstrap_status = 'BOOTSTRAP_REQUIRED' AND initialized_at IS NULL AND initial_admin_user_id IS NULL) OR (bootstrap_status = 'COMPLETED' AND initialized_at IS NOT NULL AND initial_admin_user_id IS NOT NULL)");
            table.HasCheckConstraint("ck_instance_state_version", "state_version >= 0");
        });
        builder.HasKey(row => row.Id).HasName("pk_instance_state");
        builder.Property(row => row.Id).HasColumnName("id").HasColumnType("INTEGER").IsRequired().ValueGeneratedNever();
        builder.Property(row => row.BootstrapStatus).HasColumnName("bootstrap_status").HasColumnType("TEXT").HasMaxLength(32).IsRequired();
        builder.Property(row => row.InitializedAt).HasColumnName("initialized_at").HasColumnType("INTEGER").HasConversion(SqliteValueConverters.NullableDateTimeOffsetToUnixMilliseconds, SqliteValueConverters.NullableDateTimeOffsetComparer);
        builder.Property(row => row.InitialAdminUserId).HasColumnName("initial_admin_user_id").HasColumnType("TEXT").HasMaxLength(PersistenceLimits.IdMaxLength);
        builder.Property(row => row.StateVersion).HasColumnName("state_version").HasColumnType("INTEGER").HasDefaultValue(0);
        builder.HasOne<CloudEmueraUser>().WithMany().HasForeignKey(row => row.InitialAdminUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_instance_state_initial_admin");
        builder.HasIndex(row => row.InitialAdminUserId).HasDatabaseName("ix_instance_state_initial_admin");
    }
}
