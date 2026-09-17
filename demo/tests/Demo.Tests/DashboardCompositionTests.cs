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
        var userHtml = await WholeDashboard(_users.CreateClient());
        var todoHtml = await WholeDashboard(_todos.CreateClient());

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

    /// <summary>
    /// Every section of the dashboard, fetched and joined.
    /// </summary>
    /// <remarks>
    /// Since 6.4.0 each section is its own page, so "the dashboard shows X" is a
    /// statement about a set of pages. Fetching all of them keeps that assertion
    /// saying what it always said, and adds one it could not make before: every
    /// section the navigation offers answers, rather than 404ing in somebody's
    /// browser.
    /// </remarks>
    internal static async Task<string> WholeDashboard(HttpClient client, string root = "/noelia")
    {
        string[] sections =
        [
            "", "/composition", "/configuration", "/security", "/sovereignty", "/ai",
            "/obligations", "/audit", "/sessions", "/rate-limits", "/health"
        ];

        var joined = new System.Text.StringBuilder();

        foreach (var section in sections)
        {
            var response = await client.GetAsync(root + section);

            if (response.StatusCode != System.Net.HttpStatusCode.OK)
            {
                throw new InvalidOperationException(
                    $"The navigation links to {root}{section}, which answered {(int)response.StatusCode}.");
            }

            joined.Append(await response.Content.ReadAsStringAsync());
        }

        return joined.ToString();
    }

}
