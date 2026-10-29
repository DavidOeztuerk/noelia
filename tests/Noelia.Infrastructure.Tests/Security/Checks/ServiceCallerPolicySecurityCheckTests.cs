using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Checks;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Security.ServiceTokens;
using Noelia.Infrastructure.Tests.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.Checks;

/// <summary>
/// <c>noelia.m2m.caller-policy</c>: the policy a door promises, proven on the
/// endpoints and the container that actually run.
/// </summary>
[Trait("Category", "Unit")]
[Collection(ServiceCallerHttpCollection.Name)]
public class ServiceCallerPolicySecurityCheckTests
{
    private const string CheckId = "noelia.m2m.caller-policy";

    [Fact]
    public async Task Without_the_modules_and_without_doors_the_check_is_not_applicable()
    {
        await using var host = await ServiceCallerHost.StartAsync(s =>
        {
            s.Compose = noelia => { };
            s.Services = sv => sv.AddAuthentication();
            s.Map = endpoints => endpoints.MapGet("/plain", () => "x");
        });

        var result = await Run(host);

        // NotApplicable, like every other Composition check with nothing to look at: a
        // Pass would claim a property was verified when nothing was examined.
        result.Status.Should().Be(SecurityCheckStatus.NotApplicable);
        result.Summary.Should().Contain("not in use");
    }

    [Fact]
    public async Task The_check_runs_whether_or_not_the_module_is_composed()
    {
        await using var host = await ServiceCallerHost.StartAsync(s =>
        {
            s.Compose = noelia => { };
            s.Services = sv => sv.AddAuthentication();
            s.Map = endpoints => endpoints.MapGet("/plain", () => "x");
        });

        var results = await host.Services.GetRequiredService<ISecurityCheckRunner>().RunAsync();

        results.Should().Contain(result => result.Id == CheckId);
        host.Services.GetServices<ISecurityCheck>().Single(c => c.Id == CheckId).Category
            .Should().Be(SecurityCheckCategory.Composition);
    }

    [Fact]
    public async Task A_well_formed_receiver_passes()
    {
        await using var host = await ServiceCallerHost.StartAsync();

        var result = await Run(host);

        result.Status.Should().Be(SecurityCheckStatus.Pass, result.Summary);
    }

    [Fact]
    public async Task A_door_in_a_group_that_allows_anonymous_fails()
    {
        await using var host = await ServiceCallerHost.StartAsync(s => s.Map = endpoints =>
        {
            var group = endpoints.MapGroup("/internal").AllowAnonymous();
            group.MapGet("/account", () => "x").RequireServiceCaller("advisor");
            group.MapServiceFallback();
        });

        var result = await Run(host);

        result.Status.Should().Be(SecurityCheckStatus.Fail);
        result.Summary.Should().Contain("/internal/account");
    }

    [Fact]
    public async Task A_fallback_that_allows_anonymous_fails()
    {
        await using var host = await ServiceCallerHost.StartAsync(s => s.Map = endpoints =>
        {
            var group = endpoints.MapGroup("/internal");
            group.MapGet("/account", () => "x").RequireServiceCaller("advisor");
            group.MapServiceFallback().AllowAnonymous();
        });

        (await Run(host)).Status.Should().Be(SecurityCheckStatus.Fail);
    }

    [Fact]
    public async Task A_result_handler_registered_after_the_decorator_fails()
    {
        await using var host = await ServiceCallerHost.StartAsync(s => s.Services = services =>
            services.AddSingleton<IAuthorizationMiddlewareResultHandler, ForeignHandler>());

        var result = await Run(host);

        result.Status.Should().Be(SecurityCheckStatus.Fail);
        result.Summary.Should().Contain("IAuthorizationMiddlewareResultHandler");
    }

    [Fact]
    public async Task A_door_that_carries_a_rate_limit_its_fallback_lacks_fails()
    {
        await using var host = await ServiceCallerHost.StartAsync(s => s.Map = endpoints =>
        {
            var group = endpoints.MapGroup("/internal");
            group.MapGet("/account", () => "x").RequireServiceCaller("advisor").RequireRateLimiting("per-door");
            group.MapServiceFallback();
        });

        var result = await Run(host);

        result.Status.Should().Be(SecurityCheckStatus.Fail);
        result.Summary.Should().Contain("/internal/account");
    }

    [Fact]
    public async Task A_rate_limit_with_another_name_on_the_fallback_is_not_the_same_limit()
    {
        await using var host = await ServiceCallerHost.StartAsync(s => s.Map = endpoints =>
        {
            var group = endpoints.MapGroup("/internal");
            group.MapGet("/account", () => "x").RequireServiceCaller("advisor").RequireRateLimiting("a");
            group.MapServiceFallback().RequireRateLimiting("b");
        });

        (await Run(host)).Status.Should().Be(SecurityCheckStatus.Fail);
    }

    [Fact]
    public async Task A_rate_limit_on_the_group_reaches_door_and_fallback_alike_and_passes()
    {
        await using var host = await ServiceCallerHost.StartAsync(s => s.Map = endpoints =>
        {
            var group = endpoints.MapGroup("/internal").RequireRateLimiting("per-group");
            group.MapGet("/account", () => "x").RequireServiceCaller("advisor");
            group.MapServiceFallback();
        });

        (await Run(host)).Status.Should().Be(SecurityCheckStatus.Pass);
    }

    [Fact]
    public async Task A_door_that_carries_a_cors_policy_its_fallback_lacks_fails()
    {
        await using var host = await ServiceCallerHost.StartAsync(s =>
        {
            s.Services = services => services.AddCors();
            s.Map = endpoints =>
            {
                var group = endpoints.MapGroup("/internal");
                group.MapGet("/account", () => "x").RequireServiceCaller("advisor").RequireCors("one");
                group.MapServiceFallback();
            };
        });

        (await Run(host)).Status.Should().Be(SecurityCheckStatus.Fail);
    }

    [Fact]
    public async Task A_door_that_carries_an_output_cache_policy_its_fallback_lacks_fails()
    {
        await using var host = await ServiceCallerHost.StartAsync(s =>
        {
            s.Services = services => services.AddOutputCache();
            s.Map = endpoints =>
            {
                var group = endpoints.MapGroup("/internal");
                group.MapGet("/account", () => "x").RequireServiceCaller("advisor").CacheOutput();
                group.MapServiceFallback();
            };
        });

        (await Run(host)).Status.Should().Be(SecurityCheckStatus.Fail);
    }

    [Fact]
    public async Task A_door_with_no_fallback_over_it_is_a_warning()
    {
        await using var host = await ServiceCallerHost.StartAsync(s => s.Map = endpoints =>
            endpoints.MapGroup("/internal").MapGet("/account", () => "x").RequireServiceCaller("advisor"));

        var result = await Run(host);

        result.Status.Should().Be(SecurityCheckStatus.Warning);
        result.Summary.Should().Contain("/internal/account");
    }

    [Fact]
    public async Task A_fallback_under_another_prefix_does_not_cover_the_door()
    {
        await using var host = await ServiceCallerHost.StartAsync(s => s.Map = endpoints =>
        {
            endpoints.MapGroup("/internal").MapGet("/account", () => "x").RequireServiceCaller("advisor");
            endpoints.MapGroup("/internal-other").MapServiceFallback();
        });

        (await Run(host)).Status.Should().Be(SecurityCheckStatus.Warning);
    }

    [Fact]
    public async Task A_door_is_matched_to_the_longest_fallback_prefix()
    {
        await using var host = await ServiceCallerHost.StartAsync(s => s.Map = endpoints =>
        {
            endpoints.MapGroup("/internal").MapServiceFallback();
            var inner = endpoints.MapGroup("/internal/deep");
            inner.MapGet("/account", () => "x").RequireServiceCaller("advisor").RequireRateLimiting("deep");
            inner.MapServiceFallback().RequireRateLimiting("deep");
        });

        (await Run(host)).Status.Should().Be(SecurityCheckStatus.Pass);
    }

    [Fact]
    public async Task A_controller_door_is_covered_by_a_fallback_with_an_explicit_prefix()
    {
        await using var host = await ServiceCallerHost.StartAsync(s => s.Controllers = true);

        (await Run(host)).Status.Should().Be(SecurityCheckStatus.Pass);
    }

    [Fact]
    public async Task A_summary_names_shapes_and_never_values()
    {
        await using var host = await ServiceCallerHost.StartAsync(s => s.Map = endpoints =>
            endpoints.MapGroup("/internal").MapGet("/account/{id}", () => "x").RequireServiceCaller("secret-caller-name"));

        var result = await Run(host);

        (result.Summary + result.Remediation).Should().NotContain("secret-caller-name");
    }

    // ---- the issuer side ------------------------------------------------------------

    [Fact]
    public async Task An_issuer_with_a_rate_limited_token_endpoint_passes()
    {
        await using var host = await Issuer(endpoints =>
            endpoints.MapServiceTokenEndpoint().RequireRateLimiting("token"));

        (await Run(host)).Status.Should().Be(SecurityCheckStatus.Pass);
    }

    [Fact]
    public async Task An_issuer_whose_token_endpoint_has_no_rate_limit_gets_a_warning()
    {
        await using var host = await Issuer(endpoints => endpoints.MapServiceTokenEndpoint());

        var result = await Run(host);

        result.Status.Should().Be(SecurityCheckStatus.Warning);
        result.Summary.Should().Contain("/m2m/token");
    }

    [Fact]
    public async Task An_issuer_with_two_token_endpoints_fails()
    {
        await using var host = await Issuer(endpoints =>
        {
            endpoints.MapServiceTokenEndpoint("/m2m/token").RequireRateLimiting("token");
            endpoints.MapServiceTokenEndpoint("/other/token").RequireRateLimiting("token");
        });

        (await Run(host)).Status.Should().Be(SecurityCheckStatus.Fail);
    }

    [Fact]
    public async Task An_issuer_with_no_token_endpoint_fails()
    {
        await using var host = await Issuer(endpoints => endpoints.MapGet("/plain", () => "x"));

        (await Run(host)).Status.Should().Be(SecurityCheckStatus.Fail);
    }

    [Fact]
    public async Task A_token_endpoint_without_the_issuer_module_is_not_counted_against_a_plain_service()
    {
        await using var host = await ServiceCallerHost.StartAsync(s =>
        {
            s.Compose = noelia => { };
            s.Services = sv => sv.AddAuthentication();
            s.Map = endpoints => endpoints.MapGet("/plain", () => "x");
        });

        (await Run(host)).Status.Should().Be(SecurityCheckStatus.NotApplicable);
    }

    // ---- helpers ------------------------------------------------------------------

    private static async Task<SecurityCheckResult> Run(ServiceCallerHost host)
    {
        var check = host.Services.GetServices<ISecurityCheck>().Single(c => c.Id == CheckId);
        return await check.RunAsync();
    }

    private static Task<ServiceCallerHost> Issuer(Action<IEndpointRouteBuilder> map)
    {
        var config = StandardConfig();
        return ServiceCallerHost.StartAsync(s =>
        {
            s.Configuration = config;
            s.Compose = noelia => Standard(noelia, config);
            s.Map = map;
        });
    }

    private sealed class ForeignHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(
            RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult) =>
            next(context);
    }
}
