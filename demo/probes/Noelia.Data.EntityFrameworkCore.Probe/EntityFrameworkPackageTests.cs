using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Data.EntityFrameworkCore;
using Noelia.Data.EntityFrameworkCore.Sessions;

namespace Noelia.Data.EntityFrameworkCore.Probe;

public sealed class EntityFrameworkPackageTests
{
    [Fact]
    public void Refresh_token_provider_is_one_module_and_maps_its_owned_table()
    {
        var host = Host.CreateApplicationBuilder();
        host.Services.AddDbContext<ProbeDbContext>(options => options.UseSqlite("Data Source=:memory:"));
        var noelia = new NoeliaBuilder(
            host.Services, host.Configuration, host.Environment, "ef-probe", [], []);

        noelia.UseEntityFrameworkRefreshTokens<ProbeDbContext>();
        var composition = noelia.Build();
        using var provider = host.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ProbeDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<IRefreshTokenStore>();

        composition.Included.Should().Equal(EntityFrameworkNoeliaModule.RefreshTokens);
        composition.Contracts[EntityFrameworkNoeliaModule.RefreshTokens].Requirements
            .Should().ContainSingle(requirement => requirement.ServiceType == typeof(ProbeDbContext));
        context.Model.FindEntityType(typeof(NoeliaRefreshToken)).Should().NotBeNull();
        store.GetType().Assembly.GetName().Name.Should().Be("Noelia.Data.EntityFrameworkCore");
    }

    private sealed class ProbeDbContext(DbContextOptions<ProbeDbContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ConfigureNoeliaRefreshTokens();
    }
}
