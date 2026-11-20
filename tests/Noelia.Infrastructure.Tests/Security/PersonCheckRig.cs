using System.Security.Claims;
using Noelia.Abstractions.Security.Authorization;
using ResourceDecision = Noelia.Abstractions.Security.Authorization.AuthorizationResult;

namespace Noelia.Infrastructure.Tests.Security;

/// <summary>
/// A provider of the check per record that records whom it was asked about and refuses
/// the ids it is told to (ADR-0007, #59). Ownership belongs to "alice" alone.
/// </summary>
internal sealed class PersonCheckRig
{
    public const string Owner = "alice";

    public readonly List<ClaimsPrincipal> Asked = [];
    public readonly HashSet<string> Refused = [];
    public readonly IResourceAuthorizationService Resources = Substitute.For<IResourceAuthorizationService>();

    public PersonCheckRig()
    {
        Resources.AuthorizeAsync(
                Arg.Any<ClaimsPrincipal>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var user = call.Arg<ClaimsPrincipal>();
                Asked.Add(user);
                var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                return id is not null && Refused.Contains(id)
                    ? ResourceDecision.Fail($"{id} may not")
                    : ResourceDecision.Success();
            });
        Resources.IsResourceOwnerAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => string.Equals(call.ArgAt<string>(0), Owner, StringComparison.Ordinal));
    }
}
