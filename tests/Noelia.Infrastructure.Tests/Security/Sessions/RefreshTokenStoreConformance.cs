using System.Security.Cryptography;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;

namespace Noelia.Infrastructure.Tests.Security.Sessions;

/// <summary>
/// What every <see cref="IRefreshTokenStore"/> must do, whatever it stores in.
/// </summary>
/// <remarks>
/// Written once and run against each implementation, because the interesting
/// properties — that a rotation happens exactly once under concurrency, that a
/// replay outside the grace window ends the session — are properties of the
/// storage, not of the calling code. A test that only ever ran against an
/// in-process dictionary would prove none of them about a database.
/// </remarks>
public abstract class RefreshTokenStoreConformance
{
    protected abstract IRefreshTokenStore Store { get; }

    /// <summary>
    /// Another client of the same storage.
    /// </summary>
    /// <remarks>
    /// Concurrent callers are separate requests, and a database client is
    /// usually not shareable across threads — an EF <c>DbContext</c> refuses
    /// outright. Sharing one in a test would prove something about a
    /// configuration nobody runs.
    /// </remarks>
    protected virtual IRefreshTokenStore NewClient() => Store;

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);
    private const int MaxConcurrent = 5;

    private readonly DateTimeOffset _now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_fresh_token_rotates()
    {
        var (session, token) = await SignInAsync();

        var result = await ConsumeAsync(session, token, _now);

        result.Outcome.Should().Be(ConsumeOutcome.Rotated);
        result.Issued!.Value.Session.Should().Be(session, "the sign-in outlives its tokens");
    }

    /// <summary>
    /// The session identifier survives rotation. Anything that named a session
    /// — a device list, a revocation, an audit entry — depends on it.
    /// </summary>
    [Fact]
    public async Task The_session_identifier_survives_every_rotation()
    {
        var (session, token) = await SignInAsync();

        for (var i = 0; i < 3; i++)
        {
            var result = await ConsumeAsync(session, token, _now.AddMinutes(i));
            result.Outcome.Should().Be(ConsumeOutcome.Rotated);
            result.Issued!.Value.Session.Should().Be(session);
            token = LastIssuedToken;
        }
    }

    /// <summary>
    /// Two tabs refresh at once. Exactly one may rotate; the other must get a
    /// working token rather than being signed out.
    /// </summary>
    /// <remarks>
    /// The case that decides whether rotation is usable. Treating the loser of
    /// the race as a thief produces random sign-outs for people who did nothing.
    /// </remarks>
    [Fact]
    public async Task Concurrent_presentations_rotate_exactly_once_and_nobody_is_thrown_out()
    {
        // Allowance 8 for eight presentations: once the cap closes the end of
        // the chain, every further presentation of the old token is Revoked, so
        // the claim "nobody is thrown out" holds exactly as long as the
        // allowance covers the tabs (ADR-0008, decision 2). The expectations
        // below are the original ones.
        const int allowance = 8;
        var (session, token) = await SignInAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            // Staggered on purpose: a second tab does not refresh in the same
            // instant, and with one shared timestamp a grace window of zero
            // would satisfy this test.
            // Its own successor, built without touching shared state: a field
            // written by eight threads hands two of them the same token, and
            // the store then fails on a duplicate for reasons that are the
            // test's fault.
            var (successor, _) = NewSuccessor(session);
            return await NewClient().TryConsumeAsync(
                Hash(token), successor, _now.AddSeconds(i), Grace, allowance);
        })).ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(attempts);

        results.Count(r => r.Outcome == ConsumeOutcome.Rotated)
            .Should().Be(1, "two holders of one session is the failure this prevents");
        results.Count(r => r.Outcome == ConsumeOutcome.RotatedWithinGrace).Should().Be(7);
        results.Should().AllSatisfy(r => r.Succeeded.Should().BeTrue());
    }

    /// <summary>
    /// A replay shortly after a rotation is a race, and must not end the
    /// session.
    /// </summary>
    /// <remarks>
    /// The concurrency case above cannot pin this on its own: if every caller
    /// shares one timestamp, a grace window of zero satisfies it. Here the
    /// replay is deliberately later.
    /// </remarks>
    [Fact]
    public async Task A_replay_inside_the_grace_window_is_a_race_not_a_theft()
    {
        var (session, token) = await SignInAsync();
        await ConsumeAsync(session, token, _now);

        var replay = await ConsumeAsync(session, token, _now + Grace - TimeSpan.FromSeconds(1));

        replay.Outcome.Should().Be(ConsumeOutcome.RotatedWithinGrace);
        replay.Issued!.Value.Session.Should().Be(session);
    }

    /// <summary>
    /// And the token it hands back has to work — the caller was given something
    /// usable, not merely spared an alarm.
    /// </summary>
    [Fact]
    public async Task The_token_issued_inside_the_grace_window_works()
    {
        var (session, token) = await SignInAsync();
        await ConsumeAsync(session, token, _now);
        await ConsumeAsync(session, token, _now + TimeSpan.FromSeconds(2));

        var result = await ConsumeAsync(session, LastIssuedToken, _now + TimeSpan.FromSeconds(3));

        result.Outcome.Should().Be(ConsumeOutcome.Rotated);
    }

    /// <summary>
    /// The same replay after the window is a copied token, and then the whole
    /// sign-in ends — we cannot tell which holder is the thief, so neither keeps it.
    /// </summary>
    [Fact]
    public async Task A_replay_after_the_grace_window_ends_the_whole_session()
    {
        var (session, token) = await SignInAsync();
        await ConsumeAsync(session, token, _now);
        var successor = LastIssuedToken;

        var replay = await ConsumeAsync(session, token, _now + Grace + TimeSpan.FromSeconds(1));

        replay.Outcome.Should().Be(ConsumeOutcome.ReuseDetected);

        var afterwards = await ConsumeAsync(session, successor, _now + Grace + TimeSpan.FromSeconds(2));
        afterwards.Outcome.Should().Be(ConsumeOutcome.Revoked, "the successor falls with the session");
    }

    /// <summary>
    /// A retry loop must not grow a session without bound.
    /// </summary>
    [Fact]
    public async Task A_session_holds_no_more_than_the_permitted_number_of_open_tokens()
    {
        var (session, token) = await SignInAsync();

        for (var i = 0; i < MaxConcurrent + 3; i++)
        {
            await ConsumeAsync(session, token, _now.AddSeconds(1));
        }

        var sessions = await Store.ActiveSessionsAsync(SubjectOf(session), _now.AddSeconds(2));
        sessions.Should().ContainSingle("siblings belong to one sign-in, not several");

        (await OpenTokenCountAsync(session)).Should().BeLessThanOrEqualTo(MaxConcurrent);
    }

    /// <summary>
    /// Sliding expiration must not outrun the ceiling the sign-in was given.
    /// </summary>
    /// <remarks>
    /// Without this, refreshing forever keeps a session alive forever — which is
    /// precisely what an undetected stolen token does.
    /// </remarks>
    [Fact]
    public async Task The_absolute_ceiling_beats_sliding_expiration()
    {
        var (session, token) = await SignInAsync(sessionLifetime: TimeSpan.FromHours(2));

        var result = await ConsumeAsync(session, token, _now.AddHours(3));

        result.Outcome.Should().Be(ConsumeOutcome.SessionExpired);
    }

    [Fact]
    public async Task A_successor_never_outlives_the_session_ceiling()
    {
        var (session, token) = await SignInAsync(sessionLifetime: TimeSpan.FromMinutes(30));

        var result = await ConsumeAsync(session, token, _now, tokenLifetime: TimeSpan.FromDays(14));

        result.Issued!.Value.ExpiresAt.Should().Be(result.Issued.Value.SessionExpiresAt);
    }

    /// <summary>
    /// When both have run out, the ceiling is what is reported.
    /// </summary>
    /// <remarks>
    /// In the steady state a successor is clamped to the ceiling, so the two
    /// expire in the same instant and this is the ordinary end of a sign-in.
    /// Reporting <see cref="ConsumeOutcome.Expired"/> would describe the
    /// smaller fact and send the caller looking for the wrong remedy.
    /// </remarks>
    [Fact]
    public async Task When_both_have_run_out_the_ceiling_is_what_is_reported()
    {
        var (session, token) = await SignInAsync(
            tokenLifetime: TimeSpan.FromHours(1),
            sessionLifetime: TimeSpan.FromHours(1));

        var result = await ConsumeAsync(session, token, _now.AddHours(2));

        result.Outcome.Should().Be(ConsumeOutcome.SessionExpired);
    }

    [Fact]
    public async Task An_unknown_token_is_not_found()
    {
        var (session, _) = await SignInAsync();

        var result = await Store.TryConsumeAsync(
            Hash("never issued"), Successor(session), _now, Grace, MaxConcurrent);

        result.Outcome.Should().Be(ConsumeOutcome.NotFound);
    }

    [Fact]
    public async Task An_expired_token_is_expired()
    {
        var (session, token) = await SignInAsync(tokenLifetime: TimeSpan.FromMinutes(5));

        var result = await ConsumeAsync(session, token, _now.AddMinutes(6));

        result.Outcome.Should().Be(ConsumeOutcome.Expired);
    }

    [Fact]
    public async Task Closing_a_session_refuses_its_tokens()
    {
        var (session, token) = await SignInAsync();

        (await Store.CloseSessionAsync(session, _now)).Should().Be(1);

        (await ConsumeAsync(session, token, _now.AddSeconds(1))).Outcome
            .Should().Be(ConsumeOutcome.Revoked);
    }

    [Fact]
    public async Task Closing_one_session_leaves_the_others_alone()
    {
        var subject = SubjectId.New();
        var (first, firstToken) = await SignInAsync(subject);
        var (second, secondToken) = await SignInAsync(subject);

        await Store.CloseSessionAsync(first, _now);

        (await ConsumeAsync(first, firstToken, _now.AddSeconds(1))).Outcome
            .Should().Be(ConsumeOutcome.Revoked);
        (await ConsumeAsync(second, secondToken, _now.AddSeconds(1))).Outcome
            .Should().Be(ConsumeOutcome.Rotated);
    }

    [Fact]
    public async Task Closing_every_session_of_a_person_reaches_all_of_them()
    {
        var subject = SubjectId.New();
        var (first, firstToken) = await SignInAsync(subject);
        var (second, secondToken) = await SignInAsync(subject);

        (await Store.CloseAllSessionsAsync(subject, _now)).Should().Be(2);

        (await ConsumeAsync(first, firstToken, _now.AddSeconds(1))).Outcome
            .Should().Be(ConsumeOutcome.Revoked);
        (await ConsumeAsync(second, secondToken, _now.AddSeconds(1))).Outcome
            .Should().Be(ConsumeOutcome.Revoked);
    }

    [Fact]
    public async Task Closing_reaches_only_the_person_it_names()
    {
        var mine = SubjectId.New();
        var theirs = SubjectId.New();
        await SignInAsync(mine);
        var (theirSession, theirToken) = await SignInAsync(theirs);

        await Store.CloseAllSessionsAsync(mine, _now);

        (await ConsumeAsync(theirSession, theirToken, _now.AddSeconds(1))).Outcome
            .Should().Be(ConsumeOutcome.Rotated);
    }

    [Fact]
    public async Task A_person_sees_their_own_sessions_and_no_others()
    {
        var mine = SubjectId.New();
        var theirs = SubjectId.New();
        await SignInAsync(mine);
        await SignInAsync(mine);
        await SignInAsync(theirs);

        var sessions = await Store.ActiveSessionsAsync(mine, _now);

        sessions.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_closed_session_no_longer_appears_in_the_list()
    {
        var subject = SubjectId.New();
        var (session, _) = await SignInAsync(subject);

        await Store.CloseSessionAsync(session, _now);

        (await Store.ActiveSessionsAsync(subject, _now)).Should().BeEmpty();
    }

    /// <summary>
    /// Purging removes what is finished and nothing that is in use.
    /// </summary>
    [Fact]
    public async Task Purging_removes_finished_rows_and_spares_live_ones()
    {
        var subject = SubjectId.New();
        var (closed, _) = await SignInAsync(subject);
        var (live, liveToken) = await SignInAsync(subject);
        await Store.CloseSessionAsync(closed, _now);

        var removed = await Store.PurgeAsync(_now.AddSeconds(1), batchSize: 100);

        removed.Should().BeGreaterThan(0);
        (await ConsumeAsync(live, liveToken, _now.AddSeconds(2))).Outcome
            .Should().Be(ConsumeOutcome.Rotated, "a live session must survive a purge");
    }

    [Fact]
    public async Task Purging_stops_at_the_batch_size()
    {
        var subject = SubjectId.New();
        for (var i = 0; i < 5; i++)
        {
            var (session, _) = await SignInAsync(subject);
            await Store.CloseSessionAsync(session, _now);
        }

        var removed = await Store.PurgeAsync(_now.AddSeconds(1), batchSize: 2);

        removed.Should().Be(2, "a single unbounded delete competes with the sign-in path");
    }

    // ---- the end of a session ends the grace window (ADR-0008, NO-1) ----

    /// <summary>
    /// Boss test of Q-0016: a token that was rotated a moment ago must not hand
    /// out a sibling once the person has signed out everywhere.
    /// </summary>
    [Fact]
    public async Task RefreshGrace_AfterSignOutEverywhere_ReturnsSessionEnded()
    {
        var subject = SubjectId.New();
        var (session, token) = await SignInAsync(subject);
        (await ConsumeAsync(session, token, _now)).Outcome.Should().Be(ConsumeOutcome.Rotated);

        await Store.CloseAllSessionsAsync(subject, _now.AddSeconds(1));
        var replay = await ConsumeAsync(session, token, _now.AddSeconds(2));

        replay.Outcome.Should().Be(ConsumeOutcome.Revoked);
        replay.Issued.Should().BeNull();
        (await CountOpenAsync(session)).Should().Be(0, "a sign-out that a sibling survives is not a sign-out");
    }

    [Fact]
    public async Task RefreshGrace_AfterSignOut_ReturnsSessionEnded()
    {
        var (session, token) = await SignInAsync();
        (await ConsumeAsync(session, token, _now)).Outcome.Should().Be(ConsumeOutcome.Rotated);

        await Store.CloseSessionAsync(session, _now.AddSeconds(1));
        var replay = await ConsumeAsync(session, token, _now.AddSeconds(2));

        replay.Outcome.Should().Be(ConsumeOutcome.Revoked);
        (await CountOpenAsync(session)).Should().Be(0);
    }

    /// <summary>
    /// A sibling past the ceiling would be born dead and reported as a success.
    /// </summary>
    [Fact]
    public async Task RefreshGrace_AfterTheCeiling_IsSessionExpired()
    {
        var (session, token) = await SignInAsync(sessionLifetime: TimeSpan.FromHours(2));
        var ceiling = _now.AddHours(2);

        // Rotated just before the ceiling, replayed just after it: still inside
        // the grace window, already outside the sign-in.
        (await ConsumeAsync(session, token, ceiling - TimeSpan.FromSeconds(5))).Outcome
            .Should().Be(ConsumeOutcome.Rotated);
        var replay = await ConsumeAsync(session, token, ceiling + TimeSpan.FromSeconds(1));

        replay.Outcome.Should().Be(ConsumeOutcome.SessionExpired);
        replay.Issued.Should().BeNull();
        (await CountOpenAsync(session)).Should().Be(1, "only the successor of the rotation, no sibling");
    }

    /// <summary>
    /// With an allowance of one, a second tab is still served, and the session
    /// stays open with exactly one token.
    /// </summary>
    [Fact]
    public async Task RefreshGrace_WithAllowanceOne_KeepsTheSessionOpen()
    {
        var (session, token) = await SignInAsync();
        (await ConsumeAsync(session, token, _now, maxConcurrent: 1)).Outcome
            .Should().Be(ConsumeOutcome.Rotated);

        var second = await ConsumeAsync(session, token, _now.AddSeconds(1), maxConcurrent: 1);

        second.Outcome.Should().Be(ConsumeOutcome.RotatedWithinGrace);
        (await CountOpenAsync(session)).Should().Be(1);
        (await Store.ActiveSessionsAsync(SubjectOf(session), _now.AddSeconds(2)))
            .Should().ContainSingle("the session is not ended, only narrowed to one token");
    }

    /// <summary>
    /// The cap closed the end of the chain, so the old token yields nothing
    /// more, while the sibling of the second tab stays usable.
    /// </summary>
    [Fact]
    public async Task RefreshGrace_AfterTheCapClosedTheChainEnd_IsRevoked()
    {
        var (session, token) = await SignInAsync();
        await ConsumeAsync(session, token, _now, maxConcurrent: 1);
        var second = await ConsumeAsync(session, token, _now.AddSeconds(1), maxConcurrent: 1);
        second.Outcome.Should().Be(ConsumeOutcome.RotatedWithinGrace);
        var siblingToken = LastIssuedToken;

        var third = await ConsumeAsync(session, token, _now.AddSeconds(2), maxConcurrent: 1);

        third.Outcome.Should().Be(ConsumeOutcome.Revoked);
        third.Issued.Should().BeNull();
        (await ConsumeAsync(session, siblingToken, _now.AddSeconds(3), maxConcurrent: 1)).Outcome
            .Should().Be(ConsumeOutcome.Rotated, "the second tab's token was not touched");
    }

    [Fact]
    public async Task RefreshGrace_FollowsTheChainToItsOpenEnd()
    {
        var (session, token) = await SignInAsync();
        await ConsumeAsync(session, token, _now);
        var second = LastIssuedToken;
        await ConsumeAsync(session, second, _now.AddSeconds(1));

        var replay = await ConsumeAsync(session, token, _now.AddSeconds(2));

        replay.Outcome.Should().Be(ConsumeOutcome.RotatedWithinGrace);
        (await CountOpenAsync(session)).Should().Be(2, "the end of the chain and the new sibling");
    }

    /// <summary>
    /// A chain of 32 links is followed to its end; one more is read as closed.
    /// </summary>
    [Fact]
    public async Task RefreshGrace_AChainOfThirtyTwoLinks_StillReachesItsOpenEnd()
    {
        var (session, first) = await SignInAsync();
        var token = first;
        for (var i = 0; i < 32; i++)
        {
            (await ConsumeAsync(session, token, _now)).Outcome.Should().Be(ConsumeOutcome.Rotated);
            token = LastIssuedToken;
        }

        var replay = await ConsumeAsync(session, first, _now.AddSeconds(1));

        replay.Outcome.Should().Be(ConsumeOutcome.RotatedWithinGrace);
    }

    [Fact]
    public async Task RefreshGrace_WhenTheChainIsTooLong_IsRevoked()
    {
        var (session, first) = await SignInAsync();
        var token = first;
        for (var i = 0; i < 33; i++)
        {
            (await ConsumeAsync(session, token, _now)).Outcome.Should().Be(ConsumeOutcome.Rotated);
            token = LastIssuedToken;
        }

        var replay = await ConsumeAsync(session, first, _now.AddSeconds(1));

        replay.Outcome.Should().Be(ConsumeOutcome.Revoked, "an unreadable end is read as closed");
        (await CountOpenAsync(session)).Should().Be(1, "no sibling was written");
    }

    [Fact]
    public async Task RefreshGrace_WhenTheChainIsBroken_IsRevoked()
    {
        var (session, first) = await SignInAsync();
        await ConsumeAsync(session, first, _now);
        var second = LastIssuedToken;
        await ConsumeAsync(session, second, _now.AddSeconds(1));
        await DeleteRowAsync(Hash(second));

        var replay = await ConsumeAsync(session, first, _now.AddSeconds(2));

        replay.Outcome.Should().Be(ConsumeOutcome.Revoked, "a purged link means the end cannot be shown to be open");
        (await CountOpenAsync(session)).Should().Be(1);
    }

    /// <summary>
    /// Eight tabs, an allowance of five: the surplus may be turned away, but
    /// never a second rotation, never an alarm, and never more than five tokens.
    /// </summary>
    [Fact]
    public async Task ConcurrentPresentations_BeyondTheAllowance_AreRevokedNeverRotatedTwice()
    {
        var (session, token) = await SignInAsync();

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            var (successor, _) = NewSuccessor(session);
            return await NewClient().TryConsumeAsync(
                Hash(token), successor, _now.AddSeconds(i), Grace, MaxConcurrent);
        })).ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(attempts);

        results.Count(r => r.Outcome == ConsumeOutcome.Rotated).Should().Be(1);
        results.Should().NotContain(r => r.Outcome == ConsumeOutcome.ReuseDetected);
        results.Should().AllSatisfy(r => r.Outcome.Should().BeOneOf(
            ConsumeOutcome.Rotated, ConsumeOutcome.RotatedWithinGrace, ConsumeOutcome.Revoked));
        (await CountOpenAsync(session)).Should().BeLessThanOrEqualTo(MaxConcurrent);
    }

    /// <summary>
    /// A replay after the window is a theft even in a session that was ended
    /// meanwhile: the window is judged first.
    /// </summary>
    [Fact]
    public async Task A_replay_after_the_grace_window_in_an_ended_session_is_still_reuse()
    {
        var (session, token) = await SignInAsync();
        await ConsumeAsync(session, token, _now);
        await Store.CloseSessionAsync(session, _now.AddSeconds(1));

        var replay = await ConsumeAsync(session, token, _now + Grace + TimeSpan.FromSeconds(1));

        replay.Outcome.Should().Be(ConsumeOutcome.ReuseDetected);
    }

    [Fact]
    public async Task Closing_twice_counts_only_what_the_first_call_closed()
    {
        var (session, _) = await SignInAsync();

        (await Store.CloseSessionAsync(session, _now)).Should().Be(1);
        (await Store.CloseSessionAsync(session, _now.AddSeconds(1)))
            .Should().Be(0, "whatever a store keeps to remember an ended session is not a token");
    }

    /// <summary>
    /// An open token of a session that has reached its ceiling is still closed
    /// and counted, as it always was.
    /// </summary>
    [Fact]
    public async Task Closing_everything_still_closes_the_open_tokens_of_a_session_past_its_ceiling()
    {
        var subject = SubjectId.New();
        var (session, _) = await SignInAsync(subject, sessionLifetime: TimeSpan.FromHours(1));

        var closed = await Store.CloseAllSessionsAsync(subject, _now.AddHours(2));

        closed.Should().Be(1);
        (await CountOpenAsync(session)).Should().Be(0);
    }

    // ---- harness ----

    // Read from several threads in the concurrency case.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<SessionId, SubjectId> _subjects = new();

    private string _lastIssuedToken = string.Empty;

    private string LastIssuedToken => _lastIssuedToken;

    private SubjectId SubjectOf(SessionId session) => _subjects[session];

    private async Task<(SessionId Session, string Token)> SignInAsync(
        SubjectId? subject = null,
        TimeSpan? tokenLifetime = null,
        TimeSpan? sessionLifetime = null)
    {
        var who = subject ?? SubjectId.New();
        var session = SessionId.New();
        var token = NewToken();

        _subjects[session] = who;

        await Store.CreateAsync(new RefreshTokenRecord(
            RefreshTokenId.New(),
            session,
            who,
            Hash(token),
            _now,
            _now + (tokenLifetime ?? TimeSpan.FromDays(14)),
            _now,
            _now + (sessionLifetime ?? TimeSpan.FromDays(30)),
            null,
            null,
            null));

        return (session, token);
    }

    private async Task<ConsumeResult> ConsumeAsync(
        SessionId session,
        string token,
        DateTimeOffset now,
        TimeSpan? tokenLifetime = null,
        int maxConcurrent = MaxConcurrent)
    {
        var successor = Successor(session, tokenLifetime, now);
        var result = await Store.TryConsumeAsync(Hash(token), successor, now, Grace, maxConcurrent);
        return result;
    }

    private RefreshTokenRecord Successor(
        SessionId session,
        TimeSpan? tokenLifetime = null,
        DateTimeOffset? now = null)
    {
        var (record, token) = NewSuccessor(session, tokenLifetime, now);
        _lastIssuedToken = token;
        return record;
    }

    /// <summary>A successor and its token, sharing nothing with other callers.</summary>
    private (RefreshTokenRecord Record, string Token) NewSuccessor(
        SessionId session,
        TimeSpan? tokenLifetime = null,
        DateTimeOffset? now = null)
    {
        var token = NewToken();
        var at = now ?? _now;

        var record = new RefreshTokenRecord(
            RefreshTokenId.New(),
            session,
            _subjects[session],
            Hash(token),
            at,
            at + (tokenLifetime ?? TimeSpan.FromDays(14)),
            _now,
            _now.AddDays(30),
            null,
            null,
            null);

        return (record, token);
    }

    private async Task<int> OpenTokenCountAsync(SessionId session)
    {
        // Probing through the contract: a token that still consumes is open.
        var sessions = await Store.ActiveSessionsAsync(SubjectOf(session), _now.AddSeconds(2));
        return sessions.Count == 0 ? 0 : await CountOpenAsync(session);
    }

    /// <summary>Implementations expose their own count; the default probes.</summary>
    protected abstract Task<int> CountOpenAsync(SessionId session);

    /// <summary>
    /// Removes one row the way a purge would, to leave a hole in a rotation
    /// chain. Not part of the contract, so each implementation reaches its own
    /// storage.
    /// </summary>
    protected abstract Task DeleteRowAsync(byte[] tokenHash);

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static byte[] Hash(string token) =>
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
}
