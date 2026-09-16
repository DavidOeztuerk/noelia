using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Noelia.Application.Extensions;
using Noelia.Application.Interfaces;

namespace Noelia.Application.Probe;

public sealed class ApplicationPackageTests
{
    [Fact]
    public async Task Cache_aware_CQRS_refuses_startup_with_the_exact_provider_remedy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddCQRS(typeof(CacheQuery).Assembly);
        await using var app = builder.Build();

        var start = () => app.StartAsync();

        await start.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*AddCQRS() (CacheQuery implements ICacheableQuery)*")
            .WithMessage("*IDistributedCacheService*")
            .WithMessage("*AddRedisCache(prefix) or AddInMemoryCache(prefix)*");
    }

    private sealed record CacheQuery : IQuery<string>, ICacheableQuery
    {
        public string CacheKey => "application-probe";
        public TimeSpan CacheDuration => TimeSpan.FromMinutes(1);
    }
}
