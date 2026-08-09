namespace CloudEmuera.Infrastructure.Persistence;

public sealed class InstanceStateRow
{
    public const string Required = "BOOTSTRAP_REQUIRED";
    public const string Completed = "COMPLETED";
    public int Id { get; set; } = 1;
    public string BootstrapStatus { get; set; } = Required;
    public DateTimeOffset? InitializedAt { get; set; }
    public string? InitialAdminUserId { get; set; }
    public int StateVersion { get; set; }
}
