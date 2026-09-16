using Noelia.Data.EntityFrameworkCore.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UserService.Application;

namespace UserService.Infrastructure;

public static class UserInfrastructureServiceCollectionExtensions
{
    /// <param name="services">The container.</param>
    /// <param name="databasePath">
    /// Where the SQLite file lives. One file, no server — which is the point:
    /// ending a session costs a row, not a deployment.
    /// </param>
    public static IServiceCollection AddUserInfrastructure(
        this IServiceCollection services,
        string databasePath)
    {
        services.AddSingleton<IUserRepository, InMemoryUserRepository>();
        services.AddScoped<IAccessTokenIssuer, NoeliaAccessTokenIssuer>();

        services.AddDbContext<DemoDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddEntityFrameworkRefreshTokens<DemoDbContext>();

        // Readiness means "this service can do its job", and it cannot do its
        // job without the store its sessions live in. Without this the endpoint
        // answered 200 with an empty check list, which an orchestrator reads as
        // "route traffic here" — and Noelia now reports that emptiness as
        // noelia.health.readiness-coverage: Fail rather than letting it pass.
        services.AddHealthChecks()
            .AddDbContextCheck<DemoDbContext>("user-store", tags: ["ready", "db"]);

        return services;
    }
}
