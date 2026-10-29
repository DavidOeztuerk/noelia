using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Messaging;
using Noelia.Infrastructure.Communication;
using Noelia.Infrastructure.Communication.Caching;
using Noelia.Infrastructure.Communication.Configuration;
using Noelia.Infrastructure.Communication.Deduplication;
using Noelia.Infrastructure.Communication.Telemetry;
using Noelia.Infrastructure.Resilience;
using Noelia.Infrastructure.Security.M2M;
using Xunit;

namespace Noelia.Infrastructure.Tests.Security.M2M;

/// <summary>
/// 7.1 is a minor release, so an assembly compiled against 7.0 must still find the
/// constructors it was compiled against. Optional parameters do not help with that:
/// a default value is filled in at the call site, and the compiled call names the
/// exact parameter list.
/// </summary>
[Trait("Category", "Unit")]
public class SevenZeroConstructorsTests
{
    [Fact]
    public void ServiceTokenProvider_keeps_its_7_0_constructor()
    {
        var constructor = typeof(ServiceTokenProvider).GetConstructor(
        [
            typeof(HttpClient),
            typeof(ILogger<ServiceTokenProvider>),
            typeof(IOptions<ServiceCommunicationOptions>),
            typeof(IDistributedCache)
        ]);

        constructor.Should().NotBeNull();
    }

    [Fact]
    public void ServiceCommunicationManager_keeps_its_7_0_constructor()
    {
        var constructor = typeof(ServiceCommunicationManager).GetConstructor(
        [
            typeof(HttpClient),
            typeof(IEventBus),
            typeof(ILogger<ServiceCommunicationManager>),
            typeof(IConfiguration),
            typeof(ICircuitBreakerFactory),
            typeof(IRetryPolicyFactory),
            typeof(IHttpContextAccessor),
            typeof(IOptions<ServiceCommunicationOptions>),
            typeof(IServiceResponseCache),
            typeof(IServiceTokenProvider),
            typeof(IServiceCommunicationMetrics),
            typeof(IRequestDeduplicator)
        ]);

        constructor.Should().NotBeNull();
    }

    [Fact]
    public void The_container_picks_the_constructor_that_takes_the_shared_token_cache()
    {
        // Two public constructors: without the marker the activator could pick the
        // 7.0 one, and every typed client would then build its own token cache.
        typeof(ServiceTokenProvider).GetConstructors()
            .Count(c => c.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), inherit: false))
            .Should().Be(1);
        typeof(ServiceCommunicationManager).GetConstructors()
            .Count(c => c.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), inherit: false))
            .Should().Be(1);
    }
}
