using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;
using Noelia.Infrastructure.Authorization;
using Noelia.Infrastructure.Security.ServiceTokens;
using static Noelia.Infrastructure.Tests.Authorization.AuthenticatedIdentityFixtures;

namespace Noelia.Infrastructure.Tests.Authorization;

/// <summary>
/// ADR-0007 (#59), decision 2 for the handler: same rule as the middleware, same answers.
/// The rights handler does not call <c>Fail()</c> (and must not: other handlers of the same
/// requirement may still succeed), so "not met" is asserted as "not succeeded".
/// </summary>
[Trait("Category", "Unit")]
public class PermissionHandlerAuthenticatedIdentityTests
{
    public static TheoryData<string, bool> Matrix()
    {
        var data = new TheoryData<string, bool>();
        foreach (var kind in Kinds)
        {
            data.Add(kind, true);
            data.Add(kind, false);
        }

        return data;
    }

    private static async Task<(AuthorizationHandlerContext Context, CapturingLog Log)> Run(ClaimsPrincipal user)
    {
        var log = new CapturingLog();
        var requirement = new PermissionRequirement(Required, Resource);
        var context = new AuthorizationHandlerContext([requirement], user, null);
        await new PermissionAuthorizationHandler(log, Catalog).HandleAsync(context);
        return (context, log);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task PersonWithoutTheRight_UnauthenticatedIdentityCarriesIt_IsNotMet(string kind, bool unauthenticatedFirst)
    {
        var (context, _) = await Run(Of(unauthenticatedFirst, Person(), Unauthenticated(Granting(kind))));

        context.HasSucceeded.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task OnlyUnauthenticatedIdentities_IsNotMet(string kind, bool unauthenticatedFirst)
    {
        var user = unauthenticatedFirst
            ? new ClaimsPrincipal([Unauthenticated(Granting(kind)), Unauthenticated()])
            : new ClaimsPrincipal([Unauthenticated(), Unauthenticated(Granting(kind))]);

        var (context, _) = await Run(user);

        context.HasSucceeded.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task RightOnTheAuthenticatedIdentity_UnauthenticatedBesideIt_DoesNotDisturb(string kind, bool unauthenticatedFirst)
    {
        var (context, _) = await Run(Of(unauthenticatedFirst, Person(Granting(kind)), Unauthenticated(new Claim("note", "x"))));

        context.HasSucceeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Log_NamesTheAuthenticatedUser(bool unauthenticatedFirst)
    {
        var (_, log) = await Run(Of(unauthenticatedFirst, Person(), Mallory("permission")));

        log.Messages.Should().NotBeEmpty();
        log.Messages.Should().OnlyContain(m => m.Contains("alice") && !m.Contains("mallory"));
    }

    [Fact]
    public async Task ServiceIdentity_PermissionInItCountsUnchanged()
    {
        // ADR-0007 decision 6: the service scheme's identity is authenticated; its permission claim counts.
        var service = new ClaimsIdentity(
            [new Claim("permission", Required)], ServiceTokenDefaults.AuthenticationScheme);

        var (context, _) = await Run(new ClaimsPrincipal([Unauthenticated(), service]));

        context.HasSucceeded.Should().BeTrue();
    }

    private sealed class CapturingLog : ILogger<PermissionAuthorizationHandler>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
