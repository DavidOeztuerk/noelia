using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Noelia.Infrastructure.Security.Keys;
using TodoService.Contracts;
using UserService.Contracts;

namespace Demo.Monolith.Tests;

public sealed class MonolithTests : IDisposable
{
    private static readonly GeneratedKeyPair Keys = SigningKey.GenerateKeyPair("monolith-tests");
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(), $"noelia-monolith-{Guid.NewGuid():N}.db");
    private readonly MonolithFactory _factory;
    private readonly HttpClient _client;

    public MonolithTests()
    {
        _factory = new MonolithFactory(Keys, _databasePath);
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task One_host_serves_authentication_and_todos_without_a_gateway()
    {
        var registration = await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Monolith Person", "monolith@example.com", "a-long-enough-password"));

        registration.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await registration.Content.ReadFromJsonAsync<SessionResponse>();
        session.Should().NotBeNull();

        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", session!.AccessToken);

        var created = await _client.PostAsJsonAsync("/api/todos", new CreateTodoRequest("No gateway required"));
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var todos = await _client.GetFromJsonAsync<TodoResponse[]>("/api/todos");
        todos.Should().ContainSingle(todo => todo.Title == "No gateway required");
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_work_without_a_gateway(string path)
    {
        var response = await _client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refresh_cookie_and_security_headers_keep_their_security_properties()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Secure Person", "secure@example.com", "a-long-enough-password"));

        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("noelia.rt="));
        cookie.Should().Contain("httponly", Exactly.Once());
        cookie.Should().Contain("samesite=strict", Exactly.Once());
        response.Headers.GetValues("X-Content-Type-Options").Should().ContainSingle("nosniff");
    }

    [Fact]
    public void Gateway_runtime_is_not_part_of_the_monolith()
    {
        _factory.Services.Should().NotBeNull();
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetName().Name)
            .Should().NotContain("Ocelot");
    }

    [Fact]
    public async Task Dashboard_reports_the_real_monolith_composition_without_the_canary()
    {
        var html = await _client.GetStringAsync("/noelia");

        html.Should().Contain("Dashboard");
        html.Should().Contain("PasswordHashing");
        html.Should().Contain("TokenSessions");
        html.Should().NotContain("Ocelot");
        html.Should().NotContain(GateCanary.Value);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        foreach (var file in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
