using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Noelia.Infrastructure.Security.Keys;
using Noelia.Infrastructure.Security.Sessions;
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
    public async Task Monolith_report_matches_its_reviewed_baseline_without_gateway_modules()
    {
        await Demo.TestSupport.DemoBaselineAssertions.MatchAsync(_client, "monolith-memory");
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
        var html = await WholeDashboard(_client);

        html.Should().Contain("Dashboard");
        html.Should().Contain("PasswordHashing");
        html.Should().Contain("TokenSessions");
        html.Should().NotContain("Ocelot");
        html.Should().NotContain(GateCanary.Value);
    }

    /// <summary>
    /// Signing out takes the access token with it, the refresh chain, and every
    /// other session of the same person, and it does so for the todos too: one
    /// process, one store.
    /// </summary>
    [Theory]
    [InlineData("/api/auth/sign-out")]
    [InlineData("/api/auth/sign-out-everywhere")]
    public async Task SignOut_RevokesAccessToken(string endpoint)
    {
        var first = await Register("revoked@example.com");
        using var other = _factory.CreateClient();
        var second = await (await other.PostAsJsonAsync("/api/auth/login",
            new SignInRequest("revoked@example.com", "a-long-enough-password")))
            .Content.ReadFromJsonAsync<SessionResponse>();

        (await Send(HttpMethod.Get, "/api/todos", first.AccessToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Send(HttpMethod.Post, endpoint, first.AccessToken)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Send(HttpMethod.Get, "/api/todos", first.AccessToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.PostAsync("/api/auth/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await other.PostAsync("/api/auth/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Send(HttpMethod.Get, "/api/todos", second!.AccessToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// If closing the sessions throws, the cutoff is written anyway and the
    /// answer is an error.
    /// </summary>
    [Fact]
    public async Task A_failing_close_still_writes_the_cutoff_and_answers_with_an_error()
    {
        using var failing = _factory.WithWebHostBuilder(host => host.ConfigureTestServices(services =>
        {
            services.RemoveAll<ITokenSessionService>();
            services.AddScoped<ITokenSessionService>(provider =>
                new ClosingThrows(ActivatorUtilities.CreateInstance<TokenSessionService>(provider)));
        }));
        using var client = failing.CreateClient();
        var session = await (await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Failing Person", "failing@example.com", "a-long-enough-password")))
            .Content.ReadFromJsonAsync<SessionResponse>();

        var signOut = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-out");
        signOut.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session!.AccessToken);
        var answer = await client.SendAsync(signOut);

        ((int)answer.StatusCode).Should().BeGreaterThanOrEqualTo(500);
        (await answer.Content.ReadAsStringAsync()).Should().NotContain("closing is down");

        var todos = new HttpRequestMessage(HttpMethod.Get, "/api/todos");
        todos.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        (await client.SendAsync(todos)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<SessionResponse> Register(string email) =>
        (await (await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Monolith Person", email, "a-long-enough-password")))
            .Content.ReadFromJsonAsync<SessionResponse>())!;

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    private sealed class ClosingThrows(ITokenSessionService inner) : ITokenSessionService
    {
        public Task<SignInResult> SignInAsync(SubjectId subject, string? clientFingerprint = null, CancellationToken cancellationToken = default) =>
            inner.SignInAsync(subject, clientFingerprint, cancellationToken);
        public Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            inner.RefreshAsync(refreshToken, cancellationToken);
        public Task SignOutAsync(SessionId session, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("closing is down");
        public Task SignOutEverywhereAsync(SubjectId subject, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("closing is down");
        public Task<IReadOnlyList<SessionSummary>> ActiveSessionsAsync(SubjectId subject, CancellationToken cancellationToken = default) =>
            inner.ActiveSessionsAsync(subject, cancellationToken);
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
