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
    /// Signing out takes the access token with it, and every other session of
    /// the same person.
    /// </summary>
    /// <remarks>
    /// This replaces a test that asserted the opposite, that the token lived on
    /// until it expired. The cutoff is written after the sessions are closed and
    /// rounded up to a full second, so a token issued earlier in the same second
    /// as the sign-out is refused too, unless the sign-out lands exactly on the
    /// boundary millisecond: the assertions below need no sleep.
    /// </remarks>
    [Theory]
    [InlineData("/api/auth/sign-out")]
    [InlineData("/api/auth/sign-out-everywhere")]
    public async Task SignOut_RevokesAccessToken(string endpoint)
    {
        var first = await SessionFor("revoked@example.com", "Revoked Person");

        // A second session of the same person, with its own cookie jar.
        using var other = _factory.CreateClient();
        var second = await (await other.PostAsJsonAsync("/api/auth/login",
            new SignInRequest("revoked@example.com", "a-long-enough-password")))
            .Content.ReadFromJsonAsync<SessionResponse>();

        (await Get("/api/users/me", first.AccessToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Post(endpoint, first.AccessToken)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Get("/api/users/me", first.AccessToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _client.PostAsync("/api/auth/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await other.PostAsync("/api/auth/refresh", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Get("/api/users/me", second!.AccessToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// If closing the sessions throws, the cutoff is written anyway and the
    /// answer is an error.
    /// </summary>
    /// <remarks>
    /// Revoked access tokens with an open refresh chain are the worse half of
    /// the two failures, so the endpoint takes the better one and tells the
    /// caller to repeat.
    /// </remarks>
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

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/sign-out");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session!.AccessToken);
        var answer = await client.SendAsync(request);

        ((int)answer.StatusCode).Should().BeGreaterThanOrEqualTo(500);
        (await answer.Content.ReadAsStringAsync()).Should().NotContain("closing is down");

        var me = new HttpRequestMessage(HttpMethod.Get, "/api/users/me");
        me.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        (await client.SendAsync(me)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A new sign-in after the cutoff's second has passed works.
    /// </summary>
    /// <remarks>
    /// The same-second rejection stays what it is: the cutoff is rounded up, so
    /// a token issued in the second of the sign-out is refused. This waits for
    /// the boundary instead of sleeping for a fixed time.
    /// </remarks>
    [Fact]
    public async Task A_sign_in_after_the_boundary_works_again()
    {
        var first = await SessionFor("again@example.com", "Again Person");
        (await Post("/api/auth/sign-out", first.AccessToken)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var next = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);
        var wait = next - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait + TimeSpan.FromMilliseconds(50));
        }

        var again = await (await _client.PostAsJsonAsync("/api/auth/login",
            new SignInRequest("again@example.com", "a-long-enough-password")))
            .Content.ReadFromJsonAsync<SessionResponse>();

        (await Get("/api/users/me", again!.AccessToken)).StatusCode.Should().Be(HttpStatusCode.OK);
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
