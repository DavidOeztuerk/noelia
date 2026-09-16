using Noelia.Infrastructure.Security.Keys;

namespace Demo.Tests;

public sealed class DashboardCompositionTests : IDisposable
{
    private static readonly GeneratedKeyPair Keys = SigningKey.GenerateKeyPair("dashboard-composition");
    private readonly TemporaryDatabase _database = new();
    private readonly ServiceFactory<UserService.Api.ServiceEntryPoint> _users;
    private readonly ServiceFactory<TodoService.Api.ServiceEntryPoint> _todos;

    public DashboardCompositionTests()
    {
        _users = new ServiceFactory<UserService.Api.ServiceEntryPoint>(Keys, true, _database.Path);
        _todos = new ServiceFactory<TodoService.Api.ServiceEntryPoint>(Keys, false);
    }

    [Fact]
    public async Task Service_dashboards_show_their_different_real_compositions()
    {
        var userHtml = await _users.CreateClient().GetStringAsync("/noelia");
        var todoHtml = await _todos.CreateClient().GetStringAsync("/noelia");

        userHtml.Should().Contain("PasswordHashing");
        userHtml.Should().Contain("TokenSessions");
        todoHtml.Should().NotContain("PasswordHashing");
        todoHtml.Should().NotContain("TokenSessions");
        userHtml.Should().Contain("Dashboard");
        todoHtml.Should().Contain("Dashboard");
        userHtml.Should().NotContain(GateCanary.Value);
        todoHtml.Should().NotContain(GateCanary.Value);
    }

    public void Dispose()
    {
        _users.Dispose();
        _todos.Dispose();
        _database.Dispose();
    }
}
