using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Noelia.InMemory.Sessions;
using Noelia.Infrastructure.Security.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Noelia.Infrastructure.Tests.Security.Sessions;

/// <summary>
/// Signing in, refreshing and signing out, over the store contract.
/// </summary>
/// <remarks>
/// This is what a service actually calls. The store's own conformance suite
/// pins the storage rules; these pin that the caller is handed something usable
/// and that the plaintext token exists nowhere but in the answer.
/// </remarks>
[Trait("Category", "Unit")]
public class TokenSessionServiceTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryRefreshTokenStore _store = new();
    private readonly TokenSessionOptions _options = new();

    /// <summary>
    /// One observation set for the whole test, as the container gives the
    /// application: it is a singleton, and a per-call one would be empty every
    /// time — which is exactly the defect this parameter exists to have fixed.
    /// </summary>
    private readonly SessionObservations _observed = new();

    private TokenSessionService Service() =>
        new(_store, Options.Create(_options), _clock,
            NullLogger<TokenSessionService>.Instance, _observed);

    /// <summary>
    /// A sign-in has to be visible to a later reader in the same process.
    /// </summary>
    /// <remarks>
    /// Until 5.3.0 the observation set was a field on this service, which is
    /// registered scoped — a fresh, empty dictionary on every request. The
    /// dashboard's session panel therefore reported "0 active sessions observed
    /// by this instance" in every deployment, forever, and the wording made the
    /// emptiness sound like an answer rather than a defect.
    /// <para>
    /// Two service instances over one observation set is exactly what the
    /// container produces: the request that signs in and the request that reads
    /// the dashboard are different scopes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_sign_in_is_visible_to_the_next_request()
    {
        var subject = SubjectId.New();

        await Service().SignInAsync(subject);

        var inspection = await Service().InspectAsync();

        inspection.IsAvailable.Should().BeTrue();
        inspection.Sessions.Should().ContainSingle()
            .Which.Subject.Should().Be(subject.ToString());
    }

    [Fact]
    public async Task Signing_out_everywhere_takes_the_subject_out_of_the_view()
    {
        var subject = SubjectId.New();
        await Service().SignInAsync(subject);

        await Service().SignOutEverywhereAsync(subject);

        (await Service().InspectAsync()).Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task Signing_in_issues_a_token_that_refreshes()
    {
        var service = Service();
        var signIn = await service.SignInAsync(SubjectId.New());

        var refreshed = await service.RefreshAsync(signIn.RefreshToken);

        refreshed.Outcome.Should().Be(ConsumeOutcome.Rotated);
        refreshed.RefreshToken.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Rotation means a new token every time, and the old one stops working.
    /// </summary>
    [Fact]
    public async Task Refreshing_replaces_the_token()
    {
        var service = Service();
        var signIn = await service.SignInAsync(SubjectId.New());

        var first = await service.RefreshAsync(signIn.RefreshToken);

        first.RefreshToken.Should().NotBe(signIn.RefreshToken);
    }

    /// <summary>
    /// The session is the same sign-in throughout, whatever the token is.
    /// </summary>
    [Fact]
    public async Task The_session_stays_the_same_across_refreshes()
    {
        var service = Service();
        var signIn = await service.SignInAsync(SubjectId.New());

        var first = await service.RefreshAsync(signIn.RefreshToken);
        _clock.Advance(TimeSpan.FromMinutes(20));
        var second = await service.RefreshAsync(first.RefreshToken!);

        second.Session.Should().Be(signIn.Session);
    }

    /// <summary>
    /// The store must never hold anything that could be presented.
    /// </summary>
    /// <remarks>
    /// A refresh token is a bearer credential, and the store ends up in backups
    /// and on screens. In the clear, one leaked dump is every session.
    /// </remarks>
    [Fact]
    public async Task The_token_itself_is_never_stored()
    {
        var service = Service();
        var signIn = await service.SignInAsync(SubjectId.New());

        var presented = System.Text.Encoding.UTF8.GetBytes(signIn.RefreshToken);
        var stored = await _store.TryConsumeAsync(
            presented, Successor(signIn.Session), _clock.GetUtcNow(),
            _options.ReuseGracePeriod, _options.MaxConcurrentTokensPerSession);

        stored.Outcome.Should().Be(ConsumeOutcome.NotFound,
            "the token is looked up by its hash, so the raw value matches nothing");
    }

    /// <summary>
    /// Refreshing has to say whose sign-in it was.
    /// </summary>
    /// <remarks>
    /// The next step is always issuing an access token, and that token names a
    /// person. Taken from the stored record, never from anything the caller
    /// sent — the presented token is the only thing they proved.
    /// </remarks>
    [Fact]
    public async Task Refreshing_reports_whose_session_it_is()
    {
        var subject = SubjectId.New();
        var service = Service();
        var signIn = await service.SignInAsync(subject);

        var refreshed = await service.RefreshAsync(signIn.RefreshToken);

        refreshed.Subject.Should().Be(subject);
    }

    [Fact]
    public async Task Signing_out_ends_the_session()
    {
        var service = Service();
        var signIn = await service.SignInAsync(SubjectId.New());

        await service.SignOutAsync(signIn.Session);

        (await service.RefreshAsync(signIn.RefreshToken)).Outcome
            .Should().Be(ConsumeOutcome.Revoked);
    }

    [Fact]
    public async Task Signing_out_everywhere_ends_every_session()
    {
        var subject = SubjectId.New();
        var service = Service();
        var first = await service.SignInAsync(subject);
        var second = await service.SignInAsync(subject);

        await service.SignOutEverywhereAsync(subject);

        (await service.RefreshAsync(first.RefreshToken)).Outcome.Should().Be(ConsumeOutcome.Revoked);
        (await service.RefreshAsync(second.RefreshToken)).Outcome.Should().Be(ConsumeOutcome.Revoked);
    }

    /// <summary>
    /// A stolen token used after the window ends the whole sign-in — including
    /// the token the legitimate holder is carrying.
    /// </summary>
    [Fact]
    public async Task A_replayed_token_ends_the_session_for_everyone()
    {
        var service = Service();
        var signIn = await service.SignInAsync(SubjectId.New());
        var legitimate = await service.RefreshAsync(signIn.RefreshToken);

        _clock.Advance(_options.ReuseGracePeriod + TimeSpan.FromSeconds(1));
        var replay = await service.RefreshAsync(signIn.RefreshToken);

        replay.Outcome.Should().Be(ConsumeOutcome.ReuseDetected);
        replay.Succeeded.Should().BeFalse();
        (await service.RefreshAsync(legitimate.RefreshToken!)).Outcome
            .Should().Be(ConsumeOutcome.Revoked);
    }

    /// <summary>
    /// Two tabs refreshing within moments of each other both keep working.
    /// </summary>
    [Fact]
    public async Task Two_refreshes_in_quick_succession_both_succeed()
    {
        var service = Service();
        var signIn = await service.SignInAsync(SubjectId.New());

        var first = await service.RefreshAsync(signIn.RefreshToken);
        _clock.Advance(TimeSpan.FromSeconds(2));
        var second = await service.RefreshAsync(signIn.RefreshToken);

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeTrue();
        second.Outcome.Should().Be(ConsumeOutcome.RotatedWithinGrace);
    }

    /// <summary>
    /// Refreshing forever must not keep a sign-in alive forever.
    /// </summary>
    [Fact]
    public async Task A_sign_in_ends_at_its_ceiling_however_often_it_is_refreshed()
    {
        _options.RefreshTokenLifetime = TimeSpan.FromHours(1);
        _options.AbsoluteSessionLifetime = TimeSpan.FromHours(3);

        var service = Service();
        var signIn = await service.SignInAsync(SubjectId.New());
        var token = signIn.RefreshToken;

        for (var i = 0; i < 10; i++)
        {
            _clock.Advance(TimeSpan.FromMinutes(50));
            var result = await service.RefreshAsync(token);
            if (!result.Succeeded)
            {
                result.Outcome.Should().Be(ConsumeOutcome.SessionExpired);
                return;
            }

            token = result.RefreshToken!;
        }

        Assert.Fail("the ceiling never took effect");
    }

    [Fact]
    public async Task An_unknown_token_is_refused_without_saying_more()
    {
        var result = await Service().RefreshAsync("never issued");

        result.Outcome.Should().Be(ConsumeOutcome.NotFound);
        result.RefreshToken.Should().BeNull();
        result.Session.Should().BeNull();
    }

    [Fact]
    public async Task A_person_sees_their_own_sessions()
    {
        var subject = SubjectId.New();
        var service = Service();
        await service.SignInAsync(subject, clientFingerprint: "a browser");
        await service.SignInAsync(subject);

        var sessions = await service.ActiveSessionsAsync(subject);

        sessions.Should().HaveCount(2);
        sessions.Should().Contain(s => s.ClientFingerprint == "a browser");
    }

    [Fact]
    public async Task Operator_inspection_never_contains_a_token_or_raw_fingerprint()
    {
        const string fingerprint = "CANARY-DEVICE-FINGERPRINT";
        var subject = SubjectId.New();
        var service = Service();
        var signIn = await service.SignInAsync(subject, fingerprint);

        var inspection = await service.InspectAsync();
        var serialized = System.Text.Json.JsonSerializer.Serialize(inspection);

        inspection.IsAvailable.Should().BeTrue();
        inspection.IsInstanceScoped.Should().BeTrue();
        inspection.Sessions.Should().ContainSingle().Which.Subject.Should().Be(subject.ToString());
        serialized.Should().NotContain(signIn.RefreshToken);
        serialized.Should().NotContain(fingerprint);
        serialized.Should().Contain("set");
    }

    private static RefreshTokenRecord Successor(SessionId session) => new(
        RefreshTokenId.New(), session, SubjectId.New(), [1, 2, 3],
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1),
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(30), null, null, null);
}
