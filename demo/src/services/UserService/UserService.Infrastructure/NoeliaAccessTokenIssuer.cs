using Noelia.Core.Identity;
using Noelia.Infrastructure.Security;
using UserService.Application;
using UserService.Domain;

namespace UserService.Infrastructure;

/// <summary>
/// Issues the short-lived access token, carrying the session it belongs to.
/// </summary>
/// <remarks>
/// The <c>sid</c> claim is what lets a later revocation name one device — and
/// it stays the same for the whole sign-in, however often the refresh token
/// rotates underneath it.
/// </remarks>
public sealed class NoeliaAccessTokenIssuer(IJwtService jwt) : IAccessTokenIssuer
{
    public async Task<IssuedToken> IssueAsync(
        User user,
        SessionId session,
        CancellationToken cancellationToken)
    {
        // Acting stays AsSelf: this demo has no companies, and a tenant claim
        // would assert a membership nobody checked.
        var issued = await jwt.GenerateTokenAsync(new UserClaims
        {
            UserId = user.Id.ToString(),
            Email = user.Email.Value,
            SessionId = session.ToString()
        });

        return new IssuedToken(issued.AccessToken, issued.ExpiresAt);
    }
}
