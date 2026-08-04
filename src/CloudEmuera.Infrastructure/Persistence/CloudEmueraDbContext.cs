using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CloudEmuera.Infrastructure.Persistence;

public sealed class CloudEmueraDbContext(DbContextOptions<CloudEmueraDbContext> options)
    : IdentityDbContext<CloudEmueraUser, Microsoft.AspNetCore.Identity.IdentityRole<string>, string>(options);

