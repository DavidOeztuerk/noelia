using Noelia.Data.EntityFrameworkCore.Sessions;
using Microsoft.EntityFrameworkCore;

namespace UserService.Infrastructure;

/// <summary>
/// The application's database. Noelia's refresh token table is configured into
/// it — the same database the service already runs, no server of its own.
/// </summary>
public sealed class DemoDbContext(DbContextOptions<DemoDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ConfigureNoeliaRefreshTokens();
}
