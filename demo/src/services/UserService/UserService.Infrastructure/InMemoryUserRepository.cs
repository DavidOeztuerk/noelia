using System.Collections.Concurrent;
using UserService.Application;
using UserService.Domain;

namespace UserService.Infrastructure;

/// <summary>
/// Keeps users in this process. Everything is lost on restart, which is what
/// makes this a demo and not a deployment.
/// </summary>
public sealed class InMemoryUserRepository : IUserRepository
{
    private readonly ConcurrentDictionary<Guid, User> _users = new();

    // A second index, because sign-in looks up by address and scanning every
    // user per attempt is the shape that stops working first.
    private readonly ConcurrentDictionary<string, Guid> _byEmail = new(StringComparer.Ordinal);

    public ValueTask AddAsync(User user, CancellationToken cancellationToken)
    {
        _users[user.Id.Value] = user;
        _byEmail[user.Email.Value] = user.Id.Value;
        return ValueTask.CompletedTask;
    }

    public ValueTask<User?> FindByEmailAsync(EmailAddress email, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_byEmail.TryGetValue(email.Value, out var id) ? _users.GetValueOrDefault(id) : null);

    public ValueTask<User?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        ValueTask.FromResult(_users.GetValueOrDefault(id));

    public ValueTask SaveAsync(User user, CancellationToken cancellationToken)
    {
        _users[user.Id.Value] = user;
        return ValueTask.CompletedTask;
    }
}
