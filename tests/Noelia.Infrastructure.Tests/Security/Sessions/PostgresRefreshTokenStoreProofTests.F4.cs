using System.Collections.Concurrent;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Npgsql;

namespace Noelia.Infrastructure.Tests.Security.Sessions;

// F4: a close inside the caller's transaction (ADR-0008, "Grenze: Schließen in
// einer Transaktion des Aufrufers"). Two mechanisms are named there, a deadlock
// and generations. The expectation is the safe one in both: when the close is
// over, nothing of the session is open, measured on a fresh connection after the
// bracket has ended, committed or not.
public sealed partial class PostgresRefreshTokenStoreProofTests
{
    public enum Closing
    {
        /// <summary><c>CloseSessionAsync</c>.</summary>
        SignOut,

        /// <summary>A rotated token presented after its grace: <c>TryConsumeAsync</c> closes the session.</summary>
        Theft,

        /// <summary><c>CloseAllSessionsAsync</c>.</summary>
        SignOutAll
    }

    // ---- the deadlock ----
    //
    // The session has an older open sibling R and a chain T0 -> T1 -> E (E open),
    // R written before E and issued before it. G, a grace request with T1, takes
    // the lock on E and stops at the insert of its sibling. C, the close, locks R
    // and waits for E. G goes on: its cap picks {R, E} and waits for R, which C
    // holds. That is a cycle; C has the shorter deadlock_timeout and is the victim.
    // The close is then rolled back with the caller's transaction, G writes its
    // sibling X, and X is open: nothing noticed that the sign-out did not happen.

    private sealed record World(SessionId Session, SubjectId Subject, string T0, string T1, Guid R, Guid E);

    private async Task<World> BuildWorldAsync()
    {
        var session = SessionId.New();
        var subject = SubjectId.New();

        var t0 = NewToken();
        await CreateAsync(session, subject, t0, Now, "T0");
        var r = await CreateAsync(session, subject, NewToken(), Now.AddSeconds(5), "R");

        // T0 is rotated long before the grace of the next token; T1 just now.
        var t1 = NewToken();
        var first = await RotateAsync(_plain, session, subject, t0, t1, Now.AddSeconds(10));
        if (first.Outcome != ConsumeOutcome.Rotated)
        {
            throw new SetupFailure($"T0 was not rotated ({first.Outcome})");
        }

        _labels[await IdOfAsync(t1)] = "T1";

        var e = NewToken();
        var second = await RotateAsync(_plain, session, subject, t1, e, Now.AddSeconds(100));
        if (second.Outcome != ConsumeOutcome.Rotated)
        {
            throw new SetupFailure($"T1 was not rotated ({second.Outcome})");
        }

        var eId = await IdOfAsync(e);
        _labels[eId] = "E";

        var heapOrder = await OpenRowsInHeapOrderAsync(session);
        if (!heapOrder.SequenceEqual([r, eId]))
        {
            throw new SetupFailure(
                $"R must come before E in heap order and be the only other open row, got [{string.Join(", ", heapOrder.Select(Label))}]");
        }

        return new World(session, subject, t0, t1, r, eId);
    }

    /// <summary>
    /// G: a grace request with T1, on a connection of its own whose deadlock
    /// timeout is long, held at the insert of its sibling.
    /// </summary>
    private async Task<(Client Client, ProofGate Gate, Task<ConsumeResult> Task)> StartGraceHolderAsync(World world)
    {
        var client = NewClient("-c deadlock_timeout=60s");
        var gate = NewGate(IsInsert);
        gate.Attach(client);

        Task<ConsumeResult> task;
        // A scope on the test's thread must not carry G into the transaction of C.
        using (ExecutionContext.SuppressFlow())
        {
            task = Task.Run(() => PresentAsync(client, world.Session, world.Subject, world.T1, Now.AddSeconds(105), allowance: 1));
        }

        await ArrivedAsync(gate, task, "G (grace request) at the insert of its sibling");
        return (client, gate, task);
    }

    private static string Describe(Task<ConsumeResult> g) =>
        g.IsFaulted
            ? $"threw {g.Exception!.GetBaseException().GetType().Name} (SQLSTATE {SqlStateOf(g.Exception) ?? "none"})"
            : g.IsCompletedSuccessfully ? $"returned {g.Result.Outcome}" : "not finished";

    /// <summary>
    /// Lets the cycle resolve. Polled at short intervals: the victim is the party
    /// whose timer runs out first, and C's timer is the short one only as long as
    /// G begins to wait within it.
    /// </summary>
    private async Task AwaitCycleAsync(Task c, Task<ConsumeResult> g, int pidC, int pidG)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        var gWaitedOnC = false;

        while (!(c.IsCompleted && g.IsCompleted))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new SetupFailure(
                    $"the cycle did not resolve within 20 s (G waited on C: {gWaitedOnC}; G {Describe(g)}); "
                    + "a victim G would be chosen only after 60 s");
            }

            if (!g.IsCompleted && (await BlockingPidsAsync(pidG)).Contains(pidC))
            {
                gWaitedOnC = true;
            }

            await Task.Delay(40);
        }
    }

    /// <summary>The counter is flushed once the backends are gone; it is read in autocommit, with a deadline.</summary>
    private async Task ExpectOneDeadlockMoreAsync(long before, params Client[] parties)
    {
        foreach (var party in parties)
        {
            party.Dispose();
        }

        await WaitUntilAsync(async () => await DeadlocksAsync() >= before + 1, "pg_stat_database.deadlocks did not rise");

        var after = await DeadlocksAsync();
        if (after != before + 1)
        {
            throw new SetupFailure($"pg_stat_database.deadlocks rose by {after - before}, not by exactly 1");
        }
    }

    [Theory]
    [InlineData(Closing.SignOut, Bracket.BeginTransaction)]
    [InlineData(Closing.SignOut, Bracket.TransactionScope)]
    [InlineData(Closing.Theft, Bracket.BeginTransaction)]
    [InlineData(Closing.Theft, Bracket.TransactionScope)]
    [InlineData(Closing.SignOutAll, Bracket.BeginTransaction)]
    public Task F4_Deadlock_CloseInTheCallersTransaction_LeavesNothingOpen(Closing closing, Bracket bracket) =>
        Bounded(async () =>
        {
            await ArrangeAsync();
            var world = await BuildWorldAsync();
            var before = await DeadlocksAsync();

            var (g, gate, gTask) = await StartGraceHolderAsync(world);

            // C: the application's transaction, its close at a time after G's.
            var c = NewClient("-c deadlock_timeout=2s");
            var backendId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closeAt = Now.AddSeconds(101);
            Func<Task<long>> call = closing switch
            {
                Closing.SignOut => async () => await c.Store.CloseSessionAsync(world.Session, closeAt),
                Closing.SignOutAll => async () => await c.Store.CloseAllSessionsAsync(world.Subject, closeAt),
                _ => async () =>
                {
                    var result = await PresentAsync(c, world.Session, world.Subject, world.T0, closeAt);
                    return result.Outcome == ConsumeOutcome.ReuseDetected
                        ? 1
                        : throw new SetupFailure($"the old token was answered {result.Outcome}, not ReuseDetected");
                }
            };
            var cTask = Task.Run(() => InBracketAsync(c, bracket, call, backendId));

            var pidC = (await Task.WhenAny(backendId.Task, cTask, Task.Delay(Patience))) == backendId.Task
                ? backendId.Task.Result
                : throw new SetupFailure("C did not report its backend id inside the bracket");

            await WaitUntilBlockedByAsync(pidC, g.Pid, "C waits for G (pg_blocking_pids)", cTask);
            await WaitUntilAsync(async () => await IsHeldAsync(world.R), "C holds R", cTask);

            // The state is there: let G go, at once.
            gate.Release();
            await AwaitCycleAsync(cTask, gTask, pidC, g.Pid);

            var outcome = await cTask;
            var gDescription = Describe(gTask);

            if (outcome.SqlState != "40P01" || gTask.IsFaulted)
            {
                throw new SetupFailure(
                    $"C must fail with 40P01 and G must not; C {outcome}; G {gDescription}");
            }

            await ExpectOneDeadlockMoreAsync(before, c, g);

            var open = await OpenRowsAsync(world.Session);
            var rows = await DumpAsync(world.Session);
            open.Should().Be(
                0,
                "after the sign-out nothing of the session may be open; C {0}; G {1}; rows of S: {2}",
                outcome.ToString(),
                gDescription,
                rows);
        });

    /// <summary>
    /// The control: the same arrangement without a bracket. The marker is
    /// committed before the first pass, G is held at its insert all the same, so
    /// the same cycle forms; the store repeats the pass the database aborted.
    /// </summary>
    [Fact]
    public Task F4_Deadlock_ControlWithoutABracket_IsRetriedAndLeavesNothingOpen() =>
        Bounded(async () =>
        {
            await ArrangeAsync();
            var world = await BuildWorldAsync();
            var before = await DeadlocksAsync();

            var (g, gate, gTask) = await StartGraceHolderAsync(world);

            var c = NewClient("-c deadlock_timeout=2s");
            var backendId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var passes = 0;
            var failures = new ConcurrentQueue<string>();
            c.Hooks.Before = command =>
            {
                if (IsClosePass(command))
                {
                    Interlocked.Increment(ref passes);

                    // Without a bracket there is no "inside": the id is taken from the
                    // connection that sends the pass.
                    backendId.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
                }

                return Task.CompletedTask;
            };
            c.Hooks.Failed = (_, exception) => failures.Enqueue(SqlStateOf(exception) ?? exception.GetType().Name);

            var cTask = Task.Run(async () => await c.Store.CloseSessionAsync(world.Session, Now.AddSeconds(101)));

            var pidC = (await Task.WhenAny(backendId.Task, cTask, Task.Delay(Patience))) == backendId.Task
                ? backendId.Task.Result
                : throw new SetupFailure("C did not send a pass");

            await WaitUntilBlockedByAsync(pidC, g.Pid, "C waits for G (pg_blocking_pids)", cTask);
            await WaitUntilAsync(async () => await IsHeldAsync(world.R), "C holds R", cTask);

            gate.Release();
            await AwaitCycleAsync(cTask, gTask, pidC, g.Pid);

            if (cTask.IsFaulted)
            {
                throw new SetupFailure($"the unbracketed close threw: {cTask.Exception!.GetBaseException().Message}");
            }

            await ExpectOneDeadlockMoreAsync(before, c, g);

            using var scope = new AssertionScope();
            failures.Should().Contain("40P01", "C was the victim of the cycle and the store caught it");
            passes.Should().BeGreaterThanOrEqualTo(2, "the aborted pass is repeated");
            gTask.IsFaulted.Should().BeFalse();
            (await OpenRowsAsync(world.Session)).Should().Be(
                0, "without a bracket the marker is committed and the close goes on; rows of S: " + await DumpAsync(world.Session));
        });

    // ---- the generations ----
    //
    // An actor A rotates the newest open token on a connection of its own and
    // holds before it commits: its claim is a lock on O_k, its successor O_k+1 is
    // not yet visible. C, the close, blocks on O_k in pass k. A commits; pass k
    // skips O_k and the count sees O_k+1. Before pass k+1 an interceptor holds C
    // until A has locked O_k+1. In a bracket the marker is invisible to A, so A
    // can do this for as long as it likes, and the close ends at the limit of 32
    // passes by throwing.

    private sealed class Round(ProofGate gate, Task<ConsumeResult> task, string next)
    {
        public ProofGate Gate { get; } = gate;

        public Task<ConsumeResult> Task { get; } = task;

        public string Next { get; } = next;
    }

    /// <summary>A rotates <paramref name="token"/> and holds, uncommitted, at the read of its cap.</summary>
    private async Task<Round> StartRoundAsync(
        Client a, SessionId session, SubjectId subject, string token, int round, ConcurrentDictionary<int, TaskCompletionSource> held)
    {
        var next = NewToken();
        var gate = NewGate(IsCapRead);
        gate.Attach(a);

        var task = Task.Run(() => RotateAsync(a, session, subject, token, next, Now.AddSeconds(1 + round)));
        await ArrivedAsync(gate, task, $"A, round {round}, rotating and holding before its commit");
        held.GetOrAdd(round, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        return new Round(gate, task, next);
    }

    [Fact]
    public Task F4_Generations_CloseInTheCallersTransaction_LeavesNothingOpen() =>
        Bounded(async () =>
        {
            await ArrangeAsync();

            var session = SessionId.New();
            var subject = SubjectId.New();
            var first = NewToken();
            await CreateAsync(session, subject, first, Now, "O1");

            if (await OpenRowsAsync(session) != 1)
            {
                throw new SetupFailure("the session must have exactly one open row");
            }

            var a = NewClient();
            var c = NewClient();
            var held = new ConcurrentDictionary<int, TaskCompletionSource>();
            var pass = 0;
            c.Hooks.Before = async command =>
            {
                if (IsClosePass(command))
                {
                    var n = Interlocked.Increment(ref pass);
                    await held.GetOrAdd(n, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task
                        .WaitAsync(TimeSpan.FromSeconds(20));
                }
            };

            var round = await StartRoundAsync(a, session, subject, first, 1, held);

            var backendId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cTask = Task.Run(() => InBracketAsync(
                c, Bracket.BeginTransaction, async () => await c.Store.CloseSessionAsync(session, Now.AddSeconds(1000)), backendId));

            var pidC = (await Task.WhenAny(backendId.Task, cTask, Task.Delay(Patience))) == backendId.Task
                ? backendId.Task.Result
                : throw new SetupFailure("C did not report its backend id inside the bracket");

            for (var n = 1; n <= 32; n++)
            {
                await WaitUntilBlockedByAsync(pidC, a.Pid, $"C blocks on O{n} held by A (pass {n})", cTask);

                if (n == 1 && await RowsWithIdAsync(MarkerId(session.Value)) != 0)
                {
                    throw new SetupFailure("the marker is visible to other connections inside the bracket");
                }

                round.Gate.Release();
                var answered = await BoundedAsync(round.Task, $"A, round {n}");
                if (answered.Outcome != ConsumeOutcome.Rotated)
                {
                    throw new SetupFailure($"A is answered {answered.Outcome} in round {n}, not Rotated");
                }

                if (n < 32)
                {
                    round = await StartRoundAsync(a, session, subject, round.Next, n + 1, held);
                }
            }

            var outcome = await BoundedAsync(cTask, "C after pass 32");
            var open = await OpenRowsAsync(session);

            open.Should().Be(
                0,
                "after the sign-out nothing of the session may be open; C {0}; passes sent by C: {1}; rows of S: {2}",
                outcome.ToString(),
                pass,
                await DumpAsync(session));
        });

    /// <summary>
    /// The control: without a bracket the marker is committed before pass 1, A
    /// sees it, and the close ends within two passes.
    /// </summary>
    [Fact]
    public Task F4_Generations_ControlWithoutABracket_EndsWithinTwoPasses() =>
        Bounded(async () =>
        {
            await ArrangeAsync();

            var session = SessionId.New();
            var subject = SubjectId.New();
            var first = NewToken();
            await CreateAsync(session, subject, first, Now, "O1");

            if (await OpenRowsAsync(session) != 1)
            {
                throw new SetupFailure("the session must have exactly one open row");
            }

            var a = NewClient();
            var c = NewClient();
            var held = new ConcurrentDictionary<int, TaskCompletionSource>();
            var secondRoundAnswered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var backendId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var pass = 0;
            c.Hooks.Before = async command =>
            {
                if (IsClosePass(command))
                {
                    var n = Interlocked.Increment(ref pass);
                    backendId.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);

                    if (n == 2)
                    {
                        // Before the second pass A tries once more, and the marker is there to be seen.
                        await secondRoundAnswered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    }
                }
            };

            var round = await StartRoundAsync(a, session, subject, first, 1, held);
            var cTask = Task.Run(async () => await c.Store.CloseSessionAsync(session, Now.AddSeconds(1000)));

            var pidC = (await Task.WhenAny(backendId.Task, cTask, Task.Delay(Patience))) == backendId.Task
                ? backendId.Task.Result
                : throw new SetupFailure("C did not send a pass");

            await WaitUntilBlockedByAsync(pidC, a.Pid, "C blocks on O1 held by A", cTask);

            if (await RowsWithIdAsync(MarkerId(session.Value)) != 1)
            {
                throw new SetupFailure("without a bracket the marker must be committed before pass 1");
            }

            round.Gate.Release();
            var answered = await BoundedAsync(round.Task, "A, round 1");
            if (answered.Outcome != ConsumeOutcome.Rotated)
            {
                throw new SetupFailure($"A began before the marker and must be Rotated, got {answered.Outcome}");
            }

            a.Hooks.Before = null;
            var again = await RotateAsync(a, session, subject, round.Next, NewToken(), Now.AddSeconds(3));
            secondRoundAnswered.SetResult();

            await BoundedAsync(cTask, "the close");

            using var scope = new AssertionScope();
            again.Outcome.Should().Be(ConsumeOutcome.Revoked, "A sees the marker and writes nothing");
            pass.Should().BeLessThanOrEqualTo(2);
            (await OpenRowsAsync(session)).Should().Be(0, "rows of S: " + await DumpAsync(session));
        });
}
