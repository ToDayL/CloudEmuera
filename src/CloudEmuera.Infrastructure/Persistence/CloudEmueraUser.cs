using Microsoft.AspNetCore.Identity;

namespace CloudEmuera.Infrastructure.Persistence;

public sealed class CloudEmueraUser : IdentityUser<string>
{
    public CloudEmueraUser()
    {
        Id = $"usr_{Guid.CreateVersion7():N}";
        SecurityStamp = Guid.NewGuid().ToString("N");
        Role = UserRole.Player;
        Status = UserStatus.Active;
        PreferencesJson = "{}";
    }

    public string LoginName { get; set; } = string.Empty;

    public string NormalizedLoginName { get; set; } = string.Empty;

    public UserRole Role { get; set; }

    public UserStatus Status { get; set; }

    public string QuotaProfileId { get; set; } = string.Empty;

    public string PreferencesJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public int StateVersion { get; set; }

    public QuotaProfileRow? QuotaProfile { get; set; }
}
