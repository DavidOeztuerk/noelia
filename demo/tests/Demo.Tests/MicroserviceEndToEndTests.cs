using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Noelia.Infrastructure.Security.Keys;
using TodoService.Contracts;
using UserService.Contracts;

namespace Demo.Tests;

public sealed class MicroserviceEndToEndTests : IDisposable
{
    private static readonly GeneratedKeyPair Keys = SigningKey.GenerateKeyPair("microservice-e2e");
    private readonly TemporaryDatabase _database = new();
    private readonly ServiceFactory<UserService.Api.ServiceEntryPoint> _users;
    private readonly ServiceFactory<TodoService.Api.ServiceEntryPoint> _todos;

    public MicroserviceEndToEndTests()
    {
        _users = new ServiceFactory<UserService.Api.ServiceEntryPoint>(Keys, true, _database.Path);
        _todos = new ServiceFactory<TodoService.Api.ServiceEntryPoint>(Keys, false);
    }

    [Fact]
    public async Task A_token_crosses_the_service_boundary_without_crossing_ownership()
    {
        using var userClient = _users.CreateClient();
        var owner = await Register(userClient, "owner@example.com", "Owner");
        var stranger = await Register(userClient, "stranger@example.com", "Stranger");

        using var ownerClient = _todos.CreateClient();
        ownerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        using var strangerClient = _todos.CreateClient();
        strangerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", stranger.AccessToken);

        var created = await ownerClient.PostAsJsonAsync(
            "/api/todos", new CreateTodoRequest("owned by the first service subject"));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var todo = await created.Content.ReadFromJsonAsync<TodoResponse>();

        (await strangerClient.GetFromJsonAsync<TodoResponse[]>("/api/todos"))
            .Should().BeEmpty();
        (await strangerClient.PatchAsync($"/api/todos/{todo!.Id}/complete", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var ownerTodos = await ownerClient.GetFromJsonAsync<TodoResponse[]>("/api/todos");
        ownerTodos.Should().ContainSingle().Which.IsCompleted.Should().BeFalse();
    }

    private static async Task<SessionResponse> Register(
        HttpClient client,
        string email,
        string name)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/register",
            new RegisterRequest(name, email, "a-long-enough-password"));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SessionResponse>())!;
    }

    public void Dispose()
    {
        _users.Dispose();
        _todos.Dispose();
        _database.Dispose();
    }
}
