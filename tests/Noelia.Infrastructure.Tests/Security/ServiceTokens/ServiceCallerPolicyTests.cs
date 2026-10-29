using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Noelia.Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Noelia.Abstractions.Hosting;
using Noelia.Infrastructure.Builder;
using Noelia.Infrastructure.Security.ServiceTokens;
using NSubstitute;
using static Noelia.Infrastructure.Tests.Security.ServiceTokens.ServiceTokenFixture;

namespace Noelia.Infrastructure.Tests.Security.ServiceTokens;

/// <summary>
/// The pieces of the receiving side one at a time: the attribute and the
/// requirement, the handler that reads only the service identity, the decorator
/// that turns a refusal into a 404, and what <c>UseServiceCallers</c> registers.
/// </summary>
[Trait("Category", "Unit")]
public class ServiceCallerPolicyTests
{
    // ---- attribute and requirement ---------------------------------------------

    [Fact]
    public void The_attribute_refuses_an_empty_list()
    {
        var act = () => new ServiceCallerAttribute();

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("Advisor")]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("a b")]
    [InlineData("1abc")]
    public void The_attribute_refuses_anything_that_is_not_a_client_id_so_there_is_no_wildcard(string id)
    {
        var act = () => new ServiceCallerAttribute("advisor", id);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_attribute_names_the_service_scheme_and_carries_the_callers_as_a_requirement()
    {
        var attribute = new ServiceCallerAttribute("advisor", "transfer", "advisor");

        attribute.AuthenticationSchemes.Should().Be(ServiceTokenDefaults.AuthenticationScheme);
        attribute.Policy.Should().BeNull();
        attribute.Roles.Should().BeNull();
        attribute.GetRequirements().Should().ContainSingle()
            .Which.Should().BeOfType<ServiceCallerRequirement>()
            .Which.Callers.Should().BeEquivalentTo("advisor", "transfer");
    }

    [Fact]
    public void RequireServiceCaller_puts_the_attribute_on_the_endpoint()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        app.MapGet("/x", () => "x").RequireServiceCaller("advisor");

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.Single().Endpoints.Single();
        endpoint.Metadata.GetMetadata<ServiceCallerAttribute>().Should().NotBeNull();
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Should().NotBeEmpty();
    }

    // ---- the handler ---------------------------------------------------------------

    [Fact]
    public async Task The_handler_lets_in_a_caller_from_the_service_identity()
    {
        var context = Context(["advisor"], ServiceIdentity("advisor"));

        await new ServiceCallerHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task The_handler_refuses_a_caller_who_is_not_listed()
    {
        var context = Context(["advisor"], ServiceIdentity("notification"));

        await new ServiceCallerHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task The_handler_ignores_a_client_id_on_any_identity_but_the_service_one()
    {
        // A person's token may carry any claim the issuer was told to put there.
        var person = new ClaimsIdentity([new Claim("client_id", "advisor")], "AuthenticationTypes.Federation");
        var context = Context(["advisor"], person);

        await new ServiceCallerHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task The_handler_reads_the_service_identity_even_when_the_principal_also_holds_a_person()
    {
        // What the policy evaluation produces when an endpoint asks for both schemes.
        var person = new ClaimsIdentity([new Claim("client_id", "advisor")], "AuthenticationTypes.Federation");
        var context = Context(["advisor"], person, ServiceIdentity("notification"));

        await new ServiceCallerHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse("the person's claim is not the caller");
    }

    [Fact]
    public async Task The_handler_refuses_a_service_identity_that_is_not_authenticated()
    {
        var unauthenticated = new ClaimsIdentity([new Claim("client_id", "advisor")]);
        var context = Context(["advisor"], unauthenticated);

        await new ServiceCallerHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task The_handler_never_satisfies_the_requirement_of_the_fallback()
    {
        var context = Context([], ServiceIdentity("advisor"), nobody: true);

        await new ServiceCallerHandler().HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    // ---- the decorator ---------------------------------------------------------

    [Fact]
    public async Task The_decorator_answers_404_without_body_challenge_or_forbid_and_does_not_continue()
    {
        var inner = Substitute.For<IAuthorizationMiddlewareResultHandler>();
        var decorator = new ServiceCallerAuthorizationResultHandler(inner, NullLoggerFactory.Instance);
        var http = new DefaultHttpContext();
        var next = Substitute.For<RequestDelegate>();

        await decorator.HandleAsync(next, http, DoorPolicy(), PolicyAuthorizationResult.Challenge());

        http.Response.StatusCode.Should().Be(404);
        http.Response.Headers.Should().BeEmpty();
        http.Response.ContentLength.Should().BeNull();
        await next.DidNotReceive().Invoke(Arg.Any<HttpContext>());
        await inner.DidNotReceiveWithAnyArgs().HandleAsync(default!, default!, default!, default!);
    }

    [Fact]
    public async Task The_decorator_turns_a_forbid_into_the_same_404()
    {
        var decorator = new ServiceCallerAuthorizationResultHandler(
            Substitute.For<IAuthorizationMiddlewareResultHandler>(), NullLoggerFactory.Instance);
        var http = new DefaultHttpContext();

        await decorator.HandleAsync(_ => Task.CompletedTask, http, DoorPolicy(), PolicyAuthorizationResult.Forbid());

        http.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task The_decorator_continues_when_the_caller_is_let_in()
    {
        var inner = Substitute.For<IAuthorizationMiddlewareResultHandler>();
        var decorator = new ServiceCallerAuthorizationResultHandler(inner, NullLoggerFactory.Instance);
        var http = new DefaultHttpContext();
        var reached = false;

        await decorator.HandleAsync(
            _ =>
            {
                reached = true;
                return Task.CompletedTask;
            },
            http, DoorPolicy(), PolicyAuthorizationResult.Success());

        reached.Should().BeTrue();
        http.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task The_decorator_leaves_every_policy_without_the_requirement_to_the_handler_it_wraps()
    {
        var inner = Substitute.For<IAuthorizationMiddlewareResultHandler>();
        var decorator = new ServiceCallerAuthorizationResultHandler(inner, NullLoggerFactory.Instance);
        var http = new DefaultHttpContext();
        var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        var result = PolicyAuthorizationResult.Challenge();
        RequestDelegate next = _ => Task.CompletedTask;

        await decorator.HandleAsync(next, http, policy, result);

        await inner.Received(1).HandleAsync(next, http, policy, result);
        http.Response.StatusCode.Should().Be(200, "the decorator wrote nothing for a policy that is not a door");
    }

    // ---- what UseServiceCallers registers ------------------------------------------

    [Fact]
    public void UseServiceCallers_puts_the_module_in_the_composition_with_its_key_requirement()
    {
        using var provider = Build(n => n
            .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
            .UseServiceCallers("identity"));

        var composition = provider.GetRequiredService<NoeliaComposition>();
        composition.Included.Should().Contain(NoeliaModule.ServiceCallers);
        composition.Contracts[NoeliaModule.ServiceCallers].Requirements
            .Should().ContainSingle(r => r.ServiceType.Name == "KeyRing")
            .Which.Providers.Should().NotBeEmpty();
    }

    [Fact]
    public void UseServiceCallers_replaces_the_result_handler_with_the_decorator()
    {
        using var provider = Build(n => n
            .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
            .UseServiceCallers("identity"));

        provider.GetRequiredService<IAuthorizationMiddlewareResultHandler>()
            .Should().BeOfType<ServiceCallerAuthorizationResultHandler>();
    }

    [Fact]
    public async Task UseServiceCallers_wraps_a_result_handler_that_was_registered_before_it()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var mine = Substitute.For<IAuthorizationMiddlewareResultHandler>();
        services.AddSingleton(mine);
        services.AddNoelia(Config(), new Env(), "billing", n => n
            .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
            .UseServiceCallers("identity"));
        using var provider = services.BuildServiceProvider();
        var decorator = provider.GetRequiredService<IAuthorizationMiddlewareResultHandler>();
        var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

        await decorator.HandleAsync(_ => Task.CompletedTask, new DefaultHttpContext(), policy,
            PolicyAuthorizationResult.Challenge());

        decorator.Should().BeOfType<ServiceCallerAuthorizationResultHandler>();
        await mine.ReceivedWithAnyArgs(1).HandleAsync(default!, default!, default!, default!);
    }

    [Fact]
    public async Task UseServiceCallers_adds_a_scheme_and_leaves_the_default_one_alone()
    {
        using var provider = Build(n => n
            .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
            .UseServiceCallers("identity"));
        var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();

        (await schemes.GetSchemeAsync(ServiceTokenDefaults.AuthenticationScheme)).Should().NotBeNull();
        (await schemes.GetDefaultAuthenticateSchemeAsync())!.Name.Should().Be("Bearer");
        (await schemes.GetDefaultChallengeSchemeAsync())!.Name.Should().Be("Bearer");
    }

    [Fact]
    public void The_scheme_validates_exactly_what_a_service_token_is()
    {
        using var provider = Build(n => n
            .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
            .UseServiceCallers("identity"));

        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(ServiceTokenDefaults.AuthenticationScheme);
        var parameters = options.TokenValidationParameters;

        parameters.ValidAudience.Should().Be("identity");
        parameters.ValidIssuer.Should().Be(Issuer);
        parameters.ValidTypes.Should().BeEquivalentTo(
            ServiceTokenDefaults.TokenType, "application/" + ServiceTokenDefaults.TokenType);
        parameters.NameClaimType.Should().Be("client_id");
        parameters.AuthenticationType.Should().Be(ServiceTokenDefaults.AuthenticationScheme);
        parameters.ValidateLifetime.Should().BeTrue();
        options.MapInboundClaims.Should().BeFalse();
        options.SaveToken.Should().BeFalse();
        parameters.IssuerSigningKeys.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_scheme_takes_the_token_from_the_header_and_nowhere_else()
    {
        using var provider = Build(n => n
            .UseJwt(jwt => jwt.VerifyOnly(KeyPair.PublicKey, KeyPair.Kid))
            .UseServiceCallers("identity"));
        var events = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(ServiceTokenDefaults.AuthenticationScheme).Events;
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?access_token=" + Wire.Service());
        var scheme = new AuthenticationScheme("NoeliaService", null, typeof(JwtBearerHandler));
        var context = new MessageReceivedContext(http, scheme, new JwtBearerOptions());

        await events.MessageReceived(context);

        context.Token.Should().BeNull("a service token in a query string ends up in access logs");
    }

    // ---- helpers -----------------------------------------------------------------

    private static ServiceProvider Build(Action<NoeliaBuilder> compose) =>
        Compose(Config(), compose).BuildServiceProvider();

    private static ClaimsIdentity ServiceIdentity(string client) =>
        new([new Claim("client_id", client)], ServiceTokenDefaults.AuthenticationScheme);

    private static AuthorizationHandlerContext Context(
        string[] callers, ClaimsIdentity identity, ClaimsIdentity? second = null, bool nobody = false)
    {
        var requirement = nobody ? ServiceCallerRequirement.Nobody : new ServiceCallerRequirement(callers);
        var principal = new ClaimsPrincipal(identity);
        if (second is not null)
        {
            principal.AddIdentity(second);
        }

        return new AuthorizationHandlerContext([requirement], principal, null);
    }

    private static AuthorizationPolicy DoorPolicy() =>
        new AuthorizationPolicyBuilder(ServiceTokenDefaults.AuthenticationScheme)
            .AddRequirements(new ServiceCallerRequirement(["advisor"]))
            .Build();
}
