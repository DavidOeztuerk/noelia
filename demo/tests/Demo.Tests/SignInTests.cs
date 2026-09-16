using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Noelia.Infrastructure.Security.Keys;
using UserService.Contracts;

namespace Demo.Tests;

/// <summary>
/// Signing in, refreshing and signing out, over HTTP against the real
/// composition root.
/// </summary>
public class SignInTests : IDisposable
{
    private static readonly GeneratedKeyPair Keys = SigningKey.GenerateKeyPair("tests");

    private readonly TemporaryDatabase _database = new();
    private readonly ServiceFactory<UserService.Api.ServiceEntryPoint> _factory;
    private readonly HttpClient _client;

    public SignInTests()
    {
        _factory = new ServiceFactory<UserService.Api.ServiceEntryPoint>(
            Keys, mayIssue: true, _database.Path);

        // Cookies matter here: the refresh token travels as one.
        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task Registering_returns_a_usable_session()
    {
        var response = await Register("session@example.com", "Session Person");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await response.Content.ReadFromJsonAsync<SessionResponse>();
        session!.AccessToken.Should().NotBeNullOrWhiteSpace();
        session.DisplayName.Should().Be("Session Person");
        session.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The refresh token must not be in the body, where script could read it.
    /// </summary>
    [Fact]
    public async Task The_refresh_token_is_a_cookie_and_not_in_the_answer()
    {
        var response = await Register("cookie@example.com", "Cookie Person");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("refreshToken");

        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("noelia.rt="));
        cookie.Should().Contain("httponly", Exactly.Once());
        cookie.Should().Contain("samesite=strict", Exactly.Once());
        cookie.Should().Contain("path=/api/auth");
    }

    /// <summary>
    /// An unknown address and a wrong password must be indistinguishable.
    /// </summary>
    [Fact]
    public async Task A_wrong_password_and_an_unknown_address_answer_alike()
    {
        await Register("known@example.com", "Known Person");

        var wrongPassword = await _client.PostAsJsonAsync("/api/auth/login",
            new SignInRequest("known@example.com", "definitely-not-the-password"));
        var unknownAddress = await _client.PostAsJsonAsync("/api/auth/login",
            new SignInRequest("nobody@example.com", "definitely-not-the-password"));

        wrongPassword.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        unknownAddress.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await wrongPassword.Content.ReadAsStringAsync())
            .Should().Be(await unknownAddress.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_address_can_be_claimed_only_once()
    {
        await Register("taken@example.com", "First Person");

        (await Register("taken@example.com", "Second Person")).StatusCode
            .Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_short_password_is_refused_with_a_reason()
    {
        var response = await _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Short Password", "short@example.com", "too-short"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("password");
    }

    [Fact]
    public async Task Reading_your_own_record_needs_a_token()
    {
        (await _client.GetAsync("/api/users/me")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task With_a_token_you_read_your_own_record()
    {
        var session = await SessionFor("mine@example.com", "Mine Person");

        var response = await Get("/api/users/me", session.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<UserResponse>())!.Id.Should().Be(session.UserId);
    }

    /// <summary>
    /// Refreshing hands back a new access token and rotates the cookie.
    /// </summary>
    [Fact]
    public async Task Refreshing_extends_the_sign_in()
    {
        await SessionFor("refresh@example.com", "Refresh Person");

        var refreshed = await _client.PostAsync("/api/auth/refresh", null);

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await refreshed.Content.ReadFromJsonAsync<SessionResponse>())!
            .AccessToken.Should().NotBeNullOrWhiteSpace();
        refreshed.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("noelia.rt="));
    }

    /// <summary>
    /// Signing out ends the session. It costs a row in this service's own
    /// database — no other server has to be running for it to take effect.
    /// </summary>
    [Fact]
    public async Task Signing_out_ends_the_sign_in()
    {
        var session = await SessionFor("leaving@example.com", "Leaving Person");

        var signOut = await Post("/api/auth/sign-out", session.AccessToken);
        signOut.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _client.PostAsync("/api/auth/refresh", null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// And the access token it was carrying keeps working until it expires.
    /// </summary>
    /// <remarks>
    /// Stated rather than hidden. That window is the price of verifying a token
    /// from its signature alone, and it is a number a deployment sets —
    /// <c>JwtSettings:ExpireMinutes</c> — not a server it has to run. Closing
    /// it to zero is what a revocation store is for, and it stays optional.
    /// </remarks>
    [Fact]
    public async Task The_access_token_outlives_the_sign_out_until_it_expires()
    {
        var session = await SessionFor("window@example.com", "Window Person");
        await Post("/api/auth/sign-out", session.AccessToken);

        (await Get("/api/users/me", session.AccessToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_person_sees_their_own_sessions()
    {
        var session = await SessionFor("sessions@example.com", "Sessions Person");

        var response = await Get("/api/users/me/sessions", session.AccessToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SessionSummaryResponse[]>())
            .Should().ContainSingle();
    }

    private async Task<SessionResponse> SessionFor(string email, string displayName) =>
        (await (await Register(email, displayName)).Content.ReadFromJsonAsync<SessionResponse>())!;

    private Task<HttpResponseMessage> Register(string email, string displayName) =>
        _client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(displayName, email, "a-long-enough-password"));

    private Task<HttpResponseMessage> Get(string path, string token) =>
        Send(HttpMethod.Get, path, token);

    private Task<HttpResponseMessage> Post(string path, string token) =>
        Send(HttpMethod.Post, path, token);

    private Task<HttpResponseMessage> Send(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _database.Dispose();
        GC.SuppressFinalize(this);
    }
}
