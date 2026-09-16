using Noelia.Infrastructure.Security.Keys;
using Microsoft.Extensions.DependencyInjection;

namespace Demo.Tests;

/// <summary>
/// todo-service holds nothing it could sign with.
/// </summary>
/// <remarks>
/// The property the key pair exists for. With a shared secret both services
/// held the same material, so taking over the least important one gave the
/// ability to mint a token for any person at the most important one — while the
/// README claimed each service verifies for itself.
/// </remarks>
public class KeySeparationTests : IDisposable
{
    private static readonly GeneratedKeyPair Keys = SigningKey.GenerateKeyPair("tests");

    private readonly TemporaryDatabase _database = new();
    private readonly ServiceFactory<UserService.Api.ServiceEntryPoint> _users;
    private readonly ServiceFactory<TodoService.Api.ServiceEntryPoint> _todos;

    public KeySeparationTests()
    {
        _users = new ServiceFactory<UserService.Api.ServiceEntryPoint>(Keys, mayIssue: true, _database.Path);
        _todos = new ServiceFactory<TodoService.Api.ServiceEntryPoint>(Keys, mayIssue: false);
    }

    [Fact]
    public void The_consuming_service_cannot_issue_tokens()
    {
        using var scope = _todos.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<KeyRing>().CanIssue.Should().BeFalse();
    }

    [Fact]
    public void The_issuing_service_can()
    {
        using var scope = _users.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<KeyRing>().CanIssue.Should().BeTrue();
    }

    public void Dispose()
    {
        _users.Dispose();
        _todos.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }
}
