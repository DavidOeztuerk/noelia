using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Transactions;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Noelia.Data.EntityFrameworkCore.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Noelia.Infrastructure.Tests.Security.Sessions;

/// <summary>
/// The moment a sign-out and a refresh meet, against the Entity Framework
/// store.
/// </summary>
/// <remarks>
/// A race is not something a test arranges by calling in a particular order, so
/// the interleavings are placed where they happen: an interceptor holds one
/// client at a chosen statement while another client runs to completion, or it
/// does the other party's work on the spot. SQLite admits one writer at a time,
/// which means two things for what follows. It proves the order of the steps
/// the store takes, not the locking behaviour of a database with row versions
/// (that is for the PostgreSQL conformance, a separate gate). And a client held
/// after its <c>BEGIN</c> would hold the write lock for everyone else, which is
/// why a hold that is meant to let another writer through sits before the
/// <c>BEGIN</c>, never after it.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class SqliteRefreshTokenStoreMomentTests : IDisposable
{
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private const int Allowance = 5;

    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataSource =
        $"file:noelia-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection _keepAlive;
    private readonly List<SessionTestContext> _contexts = [];
    private readonly Client _plain;

    public SqliteRefreshTokenStoreMomentTests()
    {
        _keepAlive = new SqliteConnection($"DataSource={_dataSource}");
        _keepAlive.Open();

        _plain = NewClient();
        _plain.Context.Database.EnsureCreated();
    }

    // ---- the grace branch against a sign-out ----

    /// <summary>
    /// Two tabs present the same rotated token, both stop before their first
    /// write, and the sign-out runs to the end in between.
    /// </summary>
    [Fact]
    public async Task TwoGraceSiblings_SignOutInBetween_AreBothRevoked()
    {
        var (session, subject, first) = await SignInAsync();
        await RotateAsync(_plain, session, subject, first, Now);

        var gateA = WriteGate.AtTheFirstWrite();
        var gateB = WriteGate.AtTheFirstWrite();
        var tabA = NewClient(gateA.Interceptors());
        var tabB = NewClient(gateB.Interceptors());

        var a = Task.Run(() => Present(tabA, session, subject, first, Now.AddSeconds(1)));
        var b = Task.Run(() => Present(tabB, session, subject, first, Now.AddSeconds(1)));
        await gateA.Arrived.WaitAsync(Patience);
        await gateB.Arrived.WaitAsync(Patience);

        await _plain.Store.CloseAllSessionsAsync(subject, Now.AddSeconds(2));

        gateA.Release();
        gateB.Release();
        var results = await Task.WhenAll(a, b);

        results.Should().AllSatisfy(r => r.Outcome.Should().Be(ConsumeOutcome.Revoked));
        (await OpenAsync(session)).Should().Be(0, "a sibling written after the sign-out would outlive it");
    }

    /// <summary>
    /// The grace branch stops after it has taken its lock and before it writes
    /// the sibling; the sign-out starts on a second connection.
    /// </summary>
    [Fact]
    public async Task SignOut_BetweenLockAndInsert_LeavesNothingOpen()
    {
        var (session, subject, first) = await SignInAsync();
        await RotateAsync(_plain, session, subject, first, Now);

        var gate = WriteGate.At(IsInsert);
        var tab = NewClient(gate.Interceptors());
        var presenting = Task.Run(() => Present(tab, session, subject, first, Now.AddSeconds(1)));
        await gate.Arrived.WaitAsync(Patience);

        var closing = Task.Run(() => _plain.Store.CloseAllSessionsAsync(subject, Now.AddSeconds(2)));
        // Held back or not, depending on the lock the grace branch took. What
        // matters is the state once both have returned.
        await Task.WhenAny(closing, Task.Delay(300));

        gate.Release();
        var result = await presenting.WaitAsync(Patience);
        await closing.WaitAsync(Patience);

        result.Outcome.Should().Be(ConsumeOutcome.RotatedWithinGrace);
        (await OpenAsync(session)).Should().Be(0, "when the sign-out returns, nothing of the session is open");
    }

    /// <summary>
    /// A grace request that arrives after the sign-out has written its marker
    /// gets nothing.
    /// </summary>
    [Fact]
    public async Task Grace_AfterTheMarker_IsRevoked()
    {
        var (session, subject, first) = await SignInAsync();
        await RotateAsync(_plain, session, subject, first, Now);

        ConsumeResult? duringTheClose = null;
        var closer = NewClient();
        closer.Commands.Before = async command =>
        {
            if (IsClosePass(command) && duringTheClose is null)
            {
                duringTheClose = await Present(_plain, session, subject, first, Now.AddSeconds(2));
            }
        };

        await closer.Store.CloseSessionAsync(session, Now.AddSeconds(1));

        duringTheClose!.Value.Outcome.Should().Be(ConsumeOutcome.Revoked);
        duringTheClose.Value.Issued.Should().BeNull();
        (await OpenAsync(session)).Should().Be(0);
    }

    /// <summary>
    /// The same for a rotation: a token that was open when the sign-out began
    /// cannot be rotated once the marker is down.
    /// </summary>
    [Fact]
    public async Task Rotation_AfterTheMarker_IsRevoked()
    {
        var (session, subject, first) = await SignInAsync();
        var open = (await RotateAsync(_plain, session, subject, first, Now)).Token;

        ConsumeResult? duringTheClose = null;
        var closer = NewClient();
        closer.Commands.Before = async command =>
        {
            if (IsClosePass(command) && duringTheClose is null)
            {
                duringTheClose = await Present(_plain, session, subject, open, Now.AddSeconds(2));
            }
        };

        await closer.Store.CloseSessionAsync(session, Now.AddSeconds(1));

        duringTheClose!.Value.Outcome.Should().Be(ConsumeOutcome.Revoked);
        duringTheClose.Value.Issued.Should().BeNull();
        (await OpenAsync(session)).Should().Be(0);
    }

    /// <summary>
    /// The fast actor of ADR-0008: before every pass of the sign-out, it
    /// rotates the newest open token. The marker ends that after the first.
    /// </summary>
    [Fact]
    public async Task Close_AgainstARotationBeforeEveryPass_EndsWithinTwoPassesAfterTheMarker()
    {
        var (session, subject, first) = await SignInAsync();
        var newest = first;

        var passes = 0;
        var closer = NewClient();
        closer.Commands.Before = async command =>
        {
            if (!IsClosePass(command))
            {
                return;
            }

            passes++;
            var rotated = await RotateAsync(_plain, session, subject, newest, Now.AddSeconds(1 + passes));
            if (rotated.Result.Outcome == ConsumeOutcome.Rotated)
            {
                newest = rotated.Token;
            }
        };

        await closer.Store.CloseSessionAsync(session, Now.AddSeconds(1));

        passes.Should().BeLessThanOrEqualTo(2);
        (await OpenAsync(session)).Should().Be(0);
    }

    /// <summary>
    /// After the first pass, the old token yields nothing, however many times
    /// it is presented, and the sign-out ends within two passes.
    /// </summary>
    [Fact]
    public async Task GraceAfterTheFirstPass_IsRevoked_AndClosingEndsWithinTwoPasses()
    {
        var (session, subject, first) = await SignInAsync();
        await RotateAsync(_plain, session, subject, first, Now);

        var passes = 0;
        var closer = NewClient();
        closer.Commands.Before = async command =>
        {
            if (IsClosePass(command))
            {
                passes++;
                await Present(_plain, session, subject, first, Now.AddSeconds(2));
            }
        };

        await closer.Store.CloseSessionAsync(session, Now.AddSeconds(1));
        var later = await Present(_plain, session, subject, first, Now.AddSeconds(3));
        var again = await Present(_plain, session, subject, first, Now.AddSeconds(4));

        later.Outcome.Should().Be(ConsumeOutcome.Revoked);
        again.Outcome.Should().Be(ConsumeOutcome.Revoked);
        passes.Should().BeLessThanOrEqualTo(2);
        (await OpenAsync(session)).Should().Be(0);
    }

    /// <summary>
    /// The end of the chain is rotated between finding it and locking it; the
    /// lock follows the chain instead of reading the rotation as a closure.
    /// </summary>
    [Fact]
    public async Task GraceLock_AfterTheChainEndWasRotated_FollowsTheChain()
    {
        var (session, subject, first) = await SignInAsync();
        var second = (await RotateAsync(_plain, session, subject, first, Now)).Token;
        var secondId = await IdOfAsync(second);

        var rotatedBehindItsBack = false;
        var tab = NewClient();
        tab.Commands.Before = command =>
        {
            if (IsNoopLock(command) && !rotatedBehindItsBack)
            {
                rotatedBehindItsBack = true;
                // On the connection and inside the transaction the lock is about
                // to be taken in: nothing else can be in between on SQLite, so
                // the concurrent rotation is placed by hand.
                RotateByHand(command, secondId, Now.AddSeconds(1));
            }

            return Task.CompletedTask;
        };

        var result = await Present(tab, session, subject, first, Now.AddSeconds(2));

        rotatedBehindItsBack.Should().BeTrue("the lock on the end of the chain is the step under test");
        result.Outcome.Should().Be(ConsumeOutcome.RotatedWithinGrace, "the chain went on; it was not closed");
        (await OpenAsync(session)).Should().Be(2, "the new end of the chain and the sibling");
    }

    // ---- the rotation against a sign-out ----

    /// <summary>
    /// A sign-out, or a cap, closes the token between the read and the claim.
    /// The reread finds a row that was closed, not rotated.
    /// </summary>
    [Fact]
    public async Task Refresh_SignOutBetweenReadAndClaim_IsRevoked()
    {
        var (session, subject, first) = await SignInAsync();

        var gate = WriteGate.AtTheFirstWrite();
        var tab = NewClient(gate.Interceptors());
        var refreshing = Task.Run(() => Present(tab, session, subject, first, Now.AddSeconds(1)));
        await gate.Arrived.WaitAsync(Patience);

        await _plain.Store.CloseSessionAsync(session, Now);
        gate.Release();
        var result = await refreshing.WaitAsync(Patience);

        result.Outcome.Should().Be(ConsumeOutcome.Revoked);
        result.Issued.Should().BeNull();
        (await OpenAsync(session)).Should().Be(0);
    }

    [Fact]
    public async Task Refresh_CapBetweenReadAndClaim_IsRevoked()
    {
        var (session, subject, first) = await SignInAsync();
        var second = (await RotateAsync(_plain, session, subject, first, Now, allowance: 1)).Token;

        // Holds the presentation of the open token, after its read.
        var gate = WriteGate.AtTheFirstWrite();
        var tab = NewClient(gate.Interceptors());
        var refreshing = Task.Run(() => Present(tab, session, subject, second, Now.AddSeconds(2), allowance: 1));
        await gate.Arrived.WaitAsync(Patience);

        // The second tab of the first token takes the allowance of one over,
        // which closes the older token: closed, not rotated.
        var sibling = await Present(_plain, session, subject, first, Now.AddSeconds(1), allowance: 1);
        sibling.Outcome.Should().Be(ConsumeOutcome.RotatedWithinGrace);

        gate.Release();
        var result = await refreshing.WaitAsync(Patience);

        result.Outcome.Should().Be(ConsumeOutcome.Revoked);
        (await OpenAsync(session)).Should().Be(1, "only the sibling is left");
    }

    // ---- closing ----

    [Fact]
    public async Task CloseSession_WhenTokensKeepAppearing_ThrowsAfter32Passes()
    {
        var (session, subject, token) = await SignInAsync();

        var passes = 0;
        var closer = NewClient();
        closer.Commands.Before = command =>
        {
            if (IsClosePass(command))
            {
                passes++;
            }

            return Task.CompletedTask;
        };
        closer.Commands.After = async command =>
        {
            if (IsClosePass(command))
            {
                await AddOpenTokenAsync(session, subject);
            }
        };

        var closing = () => closer.Store.CloseSessionAsync(session, Now.AddSeconds(1));

        var thrown = (await closing.Should().ThrowAsync<InvalidOperationException>()).Which;
        passes.Should().Be(32);
        thrown.Message.Should().Contain(session.Value.ToString());
        thrown.Message.Should().NotContain(token, "the message names the session, never a token");
    }

    [Fact]
    public async Task CloseAllSessions_WhenTokensKeepAppearing_ThrowsAfter32Passes()
    {
        var (session, subject, token) = await SignInAsync();

        var passes = 0;
        var closer = NewClient();
        closer.Commands.Before = command =>
        {
            if (IsClosePass(command))
            {
                passes++;
            }

            return Task.CompletedTask;
        };
        closer.Commands.After = async command =>
        {
            if (IsClosePass(command))
            {
                await AddOpenTokenAsync(session, subject);
            }
        };

        var closing = () => closer.Store.CloseAllSessionsAsync(subject, Now.AddSeconds(1));

        var thrown = (await closing.Should().ThrowAsync<InvalidOperationException>()).Which;
        passes.Should().Be(32);
        thrown.Message.Should().Contain(subject.Value.ToString());
        thrown.Message.Should().NotContain(token);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public async Task Close_OnATransientError_RetriesUpToThreeTimes(int failures, bool succeeds)
    {
        var (session, _, _) = await SignInAsync();

        var thrown = 0;
        var closer = NewClient();
        closer.Commands.Before = command =>
        {
            if (IsClosePass(command) && thrown < failures)
            {
                thrown++;
                throw new TransientDbException();
            }

            return Task.CompletedTask;
        };

        var closing = () => closer.Store.CloseSessionAsync(session, Now.AddSeconds(1));

        if (succeeds)
        {
            (await closing()).Should().Be(1);
            (await OpenAsync(session)).Should().Be(0);
        }
        else
        {
            await closing.Should().ThrowAsync<DbException>();
        }

        thrown.Should().Be(failures);
    }

    [Fact]
    public async Task CloseAll_OnATransientError_RetriesToo()
    {
        var (session, subject, _) = await SignInAsync();

        var thrown = 0;
        var closer = NewClient();
        closer.Commands.Before = command =>
        {
            if (IsClosePass(command) && thrown == 0)
            {
                thrown++;
                throw new TransientDbException();
            }

            return Task.CompletedTask;
        };

        (await closer.Store.CloseAllSessionsAsync(subject, Now.AddSeconds(1))).Should().Be(1);
        (await OpenAsync(session)).Should().Be(0);
    }

    /// <summary>
    /// The writing of the marker is a statement like any other: a transient
    /// error from it arrives wrapped by <c>SaveChanges</c>, and is retried.
    /// </summary>
    [Fact]
    public async Task Close_OnATransientErrorWhileWritingTheMarker_Retries()
    {
        var (session, _, _) = await SignInAsync();

        var thrown = 0;
        var closer = NewClient();
        closer.Commands.Before = command =>
        {
            if (IsInsert(command) && thrown == 0)
            {
                thrown++;
                throw new TransientDbException();
            }

            return Task.CompletedTask;
        };

        (await closer.Store.CloseSessionAsync(session, Now.AddSeconds(1))).Should().Be(1);

        thrown.Should().Be(1);
        (await MarkerAsync(session)).Should().NotBeNull();
    }

    [Fact]
    public async Task Close_InsideACallersTransaction_DoesNotRetry()
    {
        var (session, _, _) = await SignInAsync();

        var thrown = 0;
        var closer = NewClient();
        closer.Commands.Before = command =>
        {
            if (IsClosePass(command))
            {
                thrown++;
                throw new TransientDbException();
            }

            return Task.CompletedTask;
        };

        await using var bracket = await closer.Context.Database.BeginTransactionAsync();
        var closing = () => closer.Store.CloseSessionAsync(session, Now.AddSeconds(1));

        await closing.Should().ThrowAsync<DbException>();
        thrown.Should().Be(1, "a retry inside the caller's transaction may run against a transaction that is already lost");
    }

    /// <summary>
    /// An ambient <c>TransactionScope</c> is the caller's transaction as much
    /// as one from <c>BeginTransaction</c>: nothing retries, and the
    /// rotation and the grace branch do not open one of their own.
    /// </summary>
    [Fact]
    public async Task Close_InsideATransactionScope_DoesNotRetry()
    {
        var (session, _, _) = await SignInAsync();

        var thrown = 0;
        var closer = NewScopeClient(out _);
        closer.Commands.Before = command =>
        {
            if (IsClosePass(command))
            {
                thrown++;
                throw new TransientDbException();
            }

            return Task.CompletedTask;
        };

        using var scope = new TransactionScope(
            TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled);
        var closing = () => closer.Store.CloseSessionAsync(session, Now.AddSeconds(1));

        await closing.Should().ThrowAsync<DbException>();
        thrown.Should().Be(1);
    }

    // The Microsoft.Data.Sqlite provider cannot enlist a connection in an
    // ambient transaction, so on SQLite a scope proves only that the store
    // recognises one and keeps its hands off the transaction; that the work then
    // commits and rolls back with the scope is the provider's part, and is for
    // the PostgreSQL gate to show.
    [Fact]
    public async Task Rotation_InsideATransactionScope_OpensNoTransactionOfItsOwn()
    {
        var (session, subject, first) = await SignInAsync();
        var tab = NewScopeClient(out var begin);

        ConsumeResult result;
        using (var scope = new TransactionScope(
                   TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            result = await Present(tab, session, subject, first, Now.AddSeconds(1));
            scope.Complete();
        }

        result.Outcome.Should().Be(ConsumeOutcome.Rotated);
        begin.Started.Should().Be(0, "a connection in the caller's transaction admits no second one");
    }

    [Fact]
    public async Task Grace_InsideATransactionScope_OpensNoTransactionOfItsOwn()
    {
        var (session, subject, first) = await SignInAsync();
        await RotateAsync(_plain, session, subject, first, Now);
        var tab = NewScopeClient(out var begin);

        ConsumeResult result;
        using (var scope = new TransactionScope(
                   TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            result = await Present(tab, session, subject, first, Now.AddSeconds(1));
            scope.Complete();
        }

        result.Outcome.Should().Be(ConsumeOutcome.RotatedWithinGrace);
        begin.Started.Should().Be(0);
    }

    /// <summary>
    /// Another writer puts the marker down between the store's read and its
    /// insert. That is a sign-out by someone else, not a failure.
    /// </summary>
    [Fact]
    public async Task Close_WhenTheMarkerAppearsBetweenReadAndInsert_TreatsItAsMarked()
    {
        var (session, subject, _) = await SignInAsync();

        var planted = false;
        var closer = NewClient();
        closer.Commands.Before = async command =>
        {
            if (IsInsert(command) && !planted)
            {
                planted = true;
                await PlantMarkerAsync(session, subject);
            }
        };

        (await closer.Store.CloseSessionAsync(session, Now.AddSeconds(1))).Should().Be(1);

        planted.Should().BeTrue();
        (await CountRowsAsync(MarkerId(session.Value))).Should().Be(1);

        // The context stays usable: a marker left in its change tracker would be
        // inserted again by the next save, and fail again.
        closer.Context.Set<NoeliaRefreshToken>().Add(OtherRow(session, subject));
        var save = () => closer.Context.SaveChangesAsync();
        await save.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Close_OnAnotherError_WhileWritingTheMarker_Throws()
    {
        var (session, _, _) = await SignInAsync();

        var closer = NewClient();
        closer.Commands.Before = command => IsInsert(command)
            ? throw new InvalidOperationException("the disk is full")
            : Task.CompletedTask;

        var closing = () => closer.Store.CloseSessionAsync(session, Now.AddSeconds(1));

        var thrown = (await closing.Should().ThrowAsync<Exception>()).Which;
        Describe(thrown).Should().Contain("the disk is full", "only a key conflict is caught, nothing else");
        (await MarkerAsync(session)).Should().BeNull("a marker that was never written must not be assumed");
    }

    /// <summary>
    /// In a bracket from <c>BeginTransaction</c> the conflict is rolled back to
    /// a savepoint and the transaction stays usable.
    /// </summary>
    [Fact]
    public async Task MarkerConflict_InsideBeginTransaction_LeavesTheTransactionUsable()
    {
        var (session, _, _) = await SignInAsync();

        var planted = false;
        var closer = NewClient();
        closer.Commands.Before = command =>
        {
            // At the last read before the insert, not at the insert itself: the
            // savepoint Entity Framework sets just before saving would roll a
            // row written after it back together with the failed insert, and the
            // other writer's marker, which in life is committed, would vanish
            // with it. The only read that orders is the one for a template row.
            if (!planted && command.CommandText.Contains("ORDER BY", StringComparison.Ordinal))
            {
                planted = true;
                PlantMarkerByHand(command, session.Value);
            }

            return Task.CompletedTask;
        };

        await using var bracket = await closer.Context.Database.BeginTransactionAsync();
        (await closer.Store.CloseSessionAsync(session, Now.AddSeconds(1))).Should().Be(1);

        planted.Should().BeTrue();
        var next = () => closer.Context.Set<NoeliaRefreshToken>().AsNoTracking().CountAsync();
        (await next.Should().NotThrowAsync()).Subject.Should().BeGreaterThan(0);
        await bracket.CommitAsync();

        (await CountRowsAsync(MarkerId(session.Value))).Should().Be(1);
    }

    // ---- the marker row ----

    [Fact]
    public async Task MarkerId_MatchesTheFixedTestVector()
    {
        var session = new SessionId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));
        var subject = SubjectId.New();
        await _plain.Store.CreateAsync(Record(session, subject, Convert.ToBase64String(RandomNumberGenerator.GetBytes(8))));

        await _plain.Store.CloseSessionAsync(session, Now);

        // The identifier is data-format surface: a change would be a migration.
        (await CountRowsAsync(Guid.Parse("06463759-edbf-469d-0a5c-11a097d1db66")))
            .Should().Be(1, "the marker's identifier is derived from the session, byte for byte");
        MarkerId(session.Value).Should().Be(Guid.Parse("06463759-edbf-469d-0a5c-11a097d1db66"));
    }

    [Fact]
    public async Task TheMarker_IsNeverOpen_NeverListed_AndPurged()
    {
        var (session, subject, _) = await SignInAsync();

        (await _plain.Store.CloseSessionAsync(session, Now.AddSeconds(1))).Should().Be(1);

        var marker = await MarkerAsync(session);
        marker.Should().NotBeNull();
        marker!.SubjectId.Should().Be(subject.Value);
        marker.RevokedAt.Should().Be(Now.AddSeconds(1).UtcDateTime);
        marker.ExpiresAt.Should().Be(Now.AddSeconds(1).UtcDateTime);
        marker.IssuedAt.Should().Be(Now.AddSeconds(1).UtcDateTime);
        marker.ReplacedBy.Should().BeNull();
        marker.ClientFingerprint.Should().BeNull();
        marker.TokenHash.Should().HaveCount(32);

        (await OpenAsync(session)).Should().Be(0, "the marker is closed from the start");
        (await _plain.Store.ActiveSessionsAsync(subject, Now)).Should().BeEmpty();

        var removed = await _plain.Store.PurgeAsync(Now.AddDays(1), batchSize: 100);

        removed.Should().Be(2, "the closed token and the marker fall like any closed row");
        (await MarkerAsync(session)).Should().BeNull();
    }

    /// <summary>
    /// What the sign-out marks is decided by a condition no update changes: the
    /// ceiling of the session, not whether a token happens to be open.
    /// </summary>
    [Fact]
    public async Task CloseAll_MarksEverySessionBeforeItsCeiling()
    {
        var subject = SubjectId.New();
        var at = Now.AddHours(2);

        var (live, _, _) = await SignInAsync(subject);
        var (liveWithoutOpenToken, _, _) = await SignInAsync(subject);
        await CloseRowsByHandAsync(liveWithoutOpenToken);
        var (pastCeilingOpen, _, _) = await SignInAsync(subject, sessionLifetime: TimeSpan.FromHours(1));
        var (pastCeilingClosed, _, _) = await SignInAsync(subject, sessionLifetime: TimeSpan.FromHours(1));
        await CloseRowsByHandAsync(pastCeilingClosed);

        var closed = await _plain.Store.CloseAllSessionsAsync(subject, at);

        closed.Should().Be(2, "the open tokens of the live session and of the one past its ceiling, no marker");
        (await MarkerAsync(live)).Should().NotBeNull();
        (await MarkerAsync(liveWithoutOpenToken)).Should().NotBeNull("it can still get a token if a rotation is in flight");
        (await MarkerAsync(pastCeilingOpen)).Should().NotBeNull("it had an open row, which is closed as before");
        (await MarkerAsync(pastCeilingClosed)).Should().BeNull("nothing there can be rotated any more");
        (await OpenAsync(live)).Should().Be(0);
        (await OpenAsync(pastCeilingOpen)).Should().Be(0);
    }

    /// <summary>
    /// A sign-in that starts while the sign-out runs belongs to a session the
    /// sign-out never saw. It stays open and does not extend the call.
    /// </summary>
    [Fact]
    public async Task CloseAll_IgnoresASignInThatStartsDuringIt()
    {
        var (session, subject, _) = await SignInAsync();

        Guid? newcomer = null;
        var closer = NewClient();
        closer.Commands.After = async command =>
        {
            if (IsClosePass(command) && newcomer is null)
            {
                newcomer = (await SignInAsync(subject)).Session.Value;
            }
        };

        (await closer.Store.CloseAllSessionsAsync(subject, Now.AddSeconds(1))).Should().Be(1);

        (await OpenAsync(session)).Should().Be(0);
        (await OpenAsync(new SessionId(newcomer!.Value))).Should().Be(1);
    }

    // ---- harness ----

    private sealed record Client(
        SessionTestContext Context,
        EntityFrameworkRefreshTokenStore<SessionTestContext> Store,
        CommandHooks Commands);

    private Client NewClient(params IInterceptor[] interceptors) => Build(false, interceptors);

    /// <summary>
    /// A client for use under an ambient <c>TransactionScope</c>. Entity
    /// Framework refuses an ambient transaction on a provider that cannot enlist
    /// in it, by default with an exception; here it is let through, so that what
    /// the store does about a scope it recognises can be seen.
    /// </summary>
    private Client NewScopeClient(out BeginHook begin)
    {
        begin = new BeginHook();
        return Build(true, [begin]);
    }

    private Client Build(bool ambientScope, IInterceptor[] interceptors)
    {
        var connection = new SqliteConnection($"DataSource={_dataSource}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA busy_timeout = 5000;";
            command.ExecuteNonQuery();
        }

        var commands = new CommandHooks();
        var options = new DbContextOptionsBuilder<SessionTestContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptors.Append(commands).ToArray());
        if (ambientScope)
        {
            options.ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.AmbientTransactionWarning));
        }

        var context = new SessionTestContext(options.Options);

        lock (_contexts)
        {
            _contexts.Add(context);
        }

        return new Client(context, new EntityFrameworkRefreshTokenStore<SessionTestContext>(context), commands);
    }

    private async Task<(SessionId Session, SubjectId Subject, string Token)> SignInAsync(
        SubjectId? subject = null,
        TimeSpan? sessionLifetime = null)
    {
        var who = subject ?? SubjectId.New();
        var session = SessionId.New();
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        await _plain.Store.CreateAsync(Record(session, who, token, sessionLifetime));
        return (session, who, token);
    }

    private static RefreshTokenRecord Record(
        SessionId session,
        SubjectId subject,
        string token,
        TimeSpan? sessionLifetime = null) =>
        new(
            RefreshTokenId.New(),
            session,
            subject,
            Hash(token),
            Now,
            Now + TimeSpan.FromDays(14),
            Now,
            Now + (sessionLifetime ?? TimeSpan.FromDays(30)),
            null,
            null,
            null);

    private async Task<ConsumeResult> Present(
        Client client,
        SessionId session,
        SubjectId subject,
        string token,
        DateTimeOffset now,
        int allowance = Allowance)
    {
        var successor = Record(session, subject, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var result = await client.Store.TryConsumeAsync(Hash(token), successor with { IssuedAt = now }, now, Grace, allowance);
        return result;
    }


    /// <summary>Rotates and hands back the token that replaces <paramref name="token"/>, if any.</summary>
    private async Task<(ConsumeResult Result, string Token)> RotateAsync(
        Client client,
        SessionId session,
        SubjectId subject,
        string token,
        DateTimeOffset now,
        int allowance = Allowance)
    {
        var next = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var successor = Record(session, subject, next) with { IssuedAt = now };
        var result = await client.Store.TryConsumeAsync(Hash(token), successor, now, Grace, allowance);
        return (result, next);
    }

    private async Task<int> OpenAsync(SessionId session)
    {
        await using var reader = NewClient().Context;
        return await reader.Set<NoeliaRefreshToken>().AsNoTracking()
            .CountAsync(row => row.SessionId == session.Value && row.RevokedAt == null && row.ReplacedBy == null);
    }

    private async Task<int> CountRowsAsync(Guid id)
    {
        await using var reader = NewClient().Context;
        return await reader.Set<NoeliaRefreshToken>().AsNoTracking().CountAsync(row => row.Id == id);
    }

    private async Task<NoeliaRefreshToken?> MarkerAsync(SessionId session)
    {
        var id = MarkerId(session.Value);
        await using var reader = NewClient().Context;
        return await reader.Set<NoeliaRefreshToken>().AsNoTracking().FirstOrDefaultAsync(row => row.Id == id);
    }

    private async Task<Guid> IdOfAsync(string token)
    {
        var hash = Hash(token);
        await using var reader = NewClient().Context;
        return await reader.Set<NoeliaRefreshToken>().AsNoTracking()
            .Where(row => row.TokenHash == hash).Select(row => row.Id).SingleAsync();
    }

    private async Task AddOpenTokenAsync(SessionId session, SubjectId subject) =>
        await _plain.Store.CreateAsync(Record(
            session, subject, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    private async Task PlantMarkerAsync(SessionId session, SubjectId subject)
    {
        await using var writer = NewClient().Context;
        writer.Set<NoeliaRefreshToken>().Add(new NoeliaRefreshToken
        {
            Id = MarkerId(session.Value),
            SessionId = session.Value,
            SubjectId = subject.Value,
            TokenHash = RandomNumberGenerator.GetBytes(32),
            IssuedAt = Now.UtcDateTime,
            ExpiresAt = Now.UtcDateTime,
            SessionStartedAt = Now.UtcDateTime,
            SessionExpiresAt = Now.AddDays(30).UtcDateTime,
            RevokedAt = Now.UtcDateTime
        });
        await writer.SaveChangesAsync();
    }

    private static NoeliaRefreshToken OtherRow(SessionId session, SubjectId subject) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = session.Value,
        SubjectId = subject.Value,
        TokenHash = RandomNumberGenerator.GetBytes(32),
        IssuedAt = Now.UtcDateTime,
        ExpiresAt = Now.AddDays(1).UtcDateTime,
        SessionStartedAt = Now.UtcDateTime,
        SessionExpiresAt = Now.AddDays(30).UtcDateTime,
        RevokedAt = Now.UtcDateTime
    };

    private async Task CloseRowsByHandAsync(SessionId session)
    {
        await _plain.Context.Set<NoeliaRefreshToken>()
            .Where(row => row.SessionId == session.Value)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.RevokedAt, Now.UtcDateTime));
    }

    /// <summary>
    /// The fixed derivation, spelled out a second time on purpose: a test that
    /// called the store's own helper would agree with it whatever it did.
    /// </summary>
    private static Guid MarkerId(Guid session)
    {
        var domain = Encoding.UTF8.GetBytes("noelia.refresh-token.session-ended.v1");
        var hash = SHA256.HashData([.. domain, .. session.ToByteArray()]);
        return new Guid(hash.AsSpan(0, 16));
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static string Describe(Exception exception)
    {
        var text = new StringBuilder();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine(current.Message);
        }

        return text.ToString();
    }

    // What EF writes for a conditional update with no change: the column is set
    // to itself. Distinguishing it from the pass of a close is what lets a test
    // hold or count exactly one of them.
    private static bool IsNoopLock(DbCommand command) =>
        Regex.IsMatch(command.CommandText, "SET \"RevokedAt\" = (\"\\w+\"\\.)?\"RevokedAt\"");

    private static bool IsInsert(DbCommand command) =>
        command.CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase);

    private static bool IsWrite(DbCommand command) =>
        IsInsert(command)
        || command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
        || command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase);

    // The pass of a close: an update that sets RevokedAt to a value. The cap
    // writes the same shape, but nothing under test here caps while it is
    // being watched.
    private static bool IsClosePass(DbCommand command) =>
        command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
        && !IsNoopLock(command);

    /// <summary>The concurrent rotation of <paramref name="rowId"/>, written in the caller's own transaction.</summary>
    private static void RotateByHand(DbCommand source, Guid rowId, DateTimeOffset at)
    {
        var successor = Guid.NewGuid().ToString().ToUpperInvariant();

        Run(source, """
            INSERT INTO noelia_refresh_tokens
                ("Id", "SessionId", "SubjectId", "TokenHash", "IssuedAt", "ExpiresAt",
                 "SessionStartedAt", "SessionExpiresAt", "RevokedAt", "ReplacedBy", "ClientFingerprint")
            SELECT $new, "SessionId", "SubjectId", randomblob(32), $at, "ExpiresAt",
                   "SessionStartedAt", "SessionExpiresAt", NULL, NULL, "ClientFingerprint"
            FROM noelia_refresh_tokens WHERE "Id" = $old
            """,
            ("$new", successor), ("$old", rowId.ToString().ToUpperInvariant()), ("$at", at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF")));
        Run(source, """
            UPDATE noelia_refresh_tokens SET "RevokedAt" = $at, "ReplacedBy" = $new WHERE "Id" = $old
            """,
            ("$new", successor), ("$old", rowId.ToString().ToUpperInvariant()), ("$at", at.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF")));
    }

    /// <summary>The marker, written by another sign-out, inside the caller's transaction.</summary>
    private static void PlantMarkerByHand(DbCommand source, Guid session) =>
        Run(source, """
            INSERT INTO noelia_refresh_tokens
                ("Id", "SessionId", "SubjectId", "TokenHash", "IssuedAt", "ExpiresAt",
                 "SessionStartedAt", "SessionExpiresAt", "RevokedAt", "ReplacedBy", "ClientFingerprint")
            SELECT $id, "SessionId", "SubjectId", randomblob(32), "IssuedAt", "IssuedAt",
                   "SessionStartedAt", "SessionExpiresAt", "IssuedAt", NULL, NULL
            FROM noelia_refresh_tokens WHERE "SessionId" = $session LIMIT 1
            """,
            ("$id", MarkerId(session).ToString().ToUpperInvariant()),
            ("$session", session.ToString().ToUpperInvariant()));

    private static void Run(DbCommand source, string sql, params (string Name, string Value)[] parameters)
    {
        using var command = source.Connection!.CreateCommand();
        command.Transaction = source.Transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (_contexts)
        {
            foreach (var context in _contexts)
            {
                context.Dispose();
            }
        }

        _keepAlive.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class TransientDbException : DbException
    {
        public TransientDbException()
            : base("a deadlock, say")
        {
        }

        public override bool IsTransient => true;
    }

    /// <summary>Hooks before and after every command a context sends.</summary>
    private sealed class CommandHooks : DbCommandInterceptor
    {
        public Func<DbCommand, Task>? Before { get; set; }

        public Func<DbCommand, Task>? After { get; set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await RunBefore(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await RunBefore(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            await RunBefore(command);
            return result;
        }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            await RunAfter(command);
            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            await RunAfter(command);
            return result;
        }

        private Task RunBefore(DbCommand command) => Before?.Invoke(command) ?? Task.CompletedTask;

        private Task RunAfter(DbCommand command) => After?.Invoke(command) ?? Task.CompletedTask;
    }

    private sealed class BeginHook : DbTransactionInterceptor
    {
        public Func<Task>? Before { get; set; }

        /// <summary>How many transactions the client has begun.</summary>
        public int Started { get; private set; }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
            DbConnection connection,
            TransactionStartingEventData eventData,
            InterceptionResult<DbTransaction> result,
            CancellationToken cancellationToken = default)
        {
            Started++;

            if (Before is { } before)
            {
                await before();
            }

            return result;
        }
    }

    /// <summary>
    /// Holds a client at one event until the test lets it go, once.
    /// </summary>
    private sealed class WriteGate
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<DbCommand, bool> _commandMatches;
        private readonly bool _atBegin;
        private int _held;

        private WriteGate(Func<DbCommand, bool> commandMatches, bool atBegin)
        {
            _commandMatches = commandMatches;
            _atBegin = atBegin;
        }

        /// <summary>
        /// The first write the client attempts: its <c>BEGIN</c> where it opens a
        /// transaction, its first <c>INSERT</c> or <c>UPDATE</c> where it does not.
        /// </summary>
        public static WriteGate AtTheFirstWrite() => new(IsWrite, atBegin: true);

        public static WriteGate At(Func<DbCommand, bool> match) => new(match, atBegin: false);

        public Task Arrived => _arrived.Task;

        public void Release() => _release.TrySetResult();

        public IInterceptor[] Interceptors()
        {
            var hooks = new CommandHooks { Before = command => _commandMatches(command) ? HoldAsync() : Task.CompletedTask };
            var begin = new BeginHook { Before = () => _atBegin ? HoldAsync() : Task.CompletedTask };
            return [hooks, begin];
        }

        private async Task HoldAsync()
        {
            if (Interlocked.Exchange(ref _held, 1) != 0)
            {
                return;
            }

            _arrived.SetResult();
            await _release.Task;
        }
    }
}
