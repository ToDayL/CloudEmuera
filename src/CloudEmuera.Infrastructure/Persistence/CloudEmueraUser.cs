using Microsoft.AspNetCore.Identity;

namespace CloudEmuera.Infrastructure.Persistence;

public sealed class CloudEmueraUser : IdentityUser<string>
{
    public CloudEmueraUser()
    {
        Id = $"usr_{Guid.CreateVersion7():N}";
        SecurityStamp = Guid.NewGuid().ToString("N");
    }
}

