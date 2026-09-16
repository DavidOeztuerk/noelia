using Noelia.Core.Identity;
using UserService.Domain;

namespace UserService.Application;

public interface IUserRepository
{
    ValueTask AddAsync(User user, CancellationToken cancellationToken);
    ValueTask<User?> FindByEmailAsync(EmailAddress email, CancellationToken cancellationToken);
    ValueTask<User?> FindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Writes a changed user back.</summary>
    ValueTask SaveAsync(User user, CancellationToken cancellationToken);
}

/// <summary>Issues the access token that carries a request.</summary>
/// <remarks>
/// Short-lived and verified by every service from its signature alone. The
/// refresh token that outlives it is Noelia's <c>ITokenSessionService</c>, and
/// lives in this service's own database.
/// </remarks>
public interface IAccessTokenIssuer
{
    Task<IssuedToken> IssueAsync(User user, SessionId session, CancellationToken cancellationToken);
}

public sealed record IssuedToken(string Value, DateTimeOffset ExpiresAt);
