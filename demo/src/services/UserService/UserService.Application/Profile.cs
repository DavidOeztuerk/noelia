using Noelia.Application.Extensions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UserService.Contracts;

namespace UserService.Application;

/// <summary>Reads the caller's own record. There is no query for anyone else's.</summary>
public sealed record GetOwnProfileQuery(Guid UserId) : IRequest<UserResponse?>;

public sealed class GetOwnProfileQueryHandler(IUserRepository users)
    : IRequestHandler<GetOwnProfileQuery, UserResponse?>
{
    public async Task<UserResponse?> Handle(GetOwnProfileQuery query, CancellationToken cancellationToken)
    {
        var user = await users.FindAsync(query.UserId, cancellationToken);
        return user is null
            ? null
            : new UserResponse(user.Id.Value, user.DisplayName, user.Email.Value, user.CreatedAt);
    }
}
