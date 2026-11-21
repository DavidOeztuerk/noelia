using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Noelia.Abstractions.Security;
using Noelia.InMemory.Security;
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

    /// <summary>
    /// A cutoff written in the todo service's own process refuses the old token
    /// there. This is the Development case: in process, per service, but present.
    /// </summary>
    [Fact]
    public async Task The_todo_service_refuses_a_token_older_than_its_own_cutoff()
    {
        using var userClient = _users.CreateClient();
        var person = await Register(userClient, "cutoff@example.com", "Cutoff");
        using var todoClient = _todos.CreateClient();
        todoClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", person.AccessToken);
        (await todoClient.GetAsync("/api/todos")).StatusCode.Should().Be(HttpStatusCode.OK);

        var writer = _todos.Services.GetRequiredService<ITokenRevocationWriter>();
        await writer.RevokeSubjectBeforeAsync(
            person.UserId.ToString(), DateTimeOffset.UtcNow.AddSeconds(2), "test");

        (await todoClient.GetAsync("/api/todos")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Signing out at the user service ends the token at the todo service, when
    /// both write to and read from one store, which is what Valkey is in
    /// Staging and Production.
    /// </summary>
    [Fact]
    public async Task Signing_out_at_the_user_service_refuses_the_token_at_the_todo_service()
    {
        // One instance for both hosts, standing in for the shared Valkey.
        var shared = new InMemoryTokenRevocationStore();
        void Share(IWebHostBuilder host) => host.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITokenRevocationEvaluator>();
            services.RemoveAll<ITokenRevocationWriter>();
            services.AddSingleton<ITokenRevocationEvaluator>(shared);
            services.AddSingleton<ITokenRevocationWriter>(shared);
        });
        using var users = _users.WithWebHostBuilder(Share);
        using var todos = _todos.WithWebHostBuilder(Share);

        using var userClient = users.CreateClient();
        var person = await Register(userClient, "across@example.com", "Across");
        using var todoClient = todos.CreateClient();
        todoClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", person.AccessToken);
        (await todoClient.GetAsync("/api/todos")).StatusCode.Should().Be(HttpStatusCode.OK);

        var signOut = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-out");
        signOut.Headers.Authorization = new AuthenticationHeaderValue("Bearer", person.AccessToken);
        (await userClient.SendAsync(signOut)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await todoClient.GetAsync("/api/todos")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
