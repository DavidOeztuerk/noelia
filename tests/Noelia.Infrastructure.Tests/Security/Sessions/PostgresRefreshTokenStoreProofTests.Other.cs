using System.Collections.Concurrent;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Noelia.Data.EntityFrameworkCore.Sessions;
using Npgsql;

namespace Noelia.Infrastructure.Tests.Security.Sessions;

public sealed partial class PostgresRefreshTokenStoreProofTests
{
    // ---- the marker conflict (ADR-0008, decision 3) ----
    // B waits for A's uncommitted marker, then fails with 23505: a savepoint keeps a BeginTransaction
    // usable, a TransactionScope has none and ends in 25P02.

    [Theory]
    [InlineData(Bracket.BeginTransaction)]
    [InlineData(Bracket.TransactionScope)]
    public Task MarkerConflict_TwoSignOutsOfOneSession_BehavesAsTheAdrDescribesFor(Bracket bracket) =>
        Bounded(async () =>
        {
            await ArrangeAsync();

            var session = SessionId.New();
            var subject = SubjectId.New();
            await CreateAsync(session, subject, NewToken(), Now, "O1");

            var a = NewClient();
            await using var aTransaction = await a.Context.Database.BeginTransactionAsync();
            (await a.Store.CloseSessionAsync(session, Now.AddSeconds(1))).Should().Be(1);

            if (await RowsWithIdAsync(MarkerId(session.Value)) != 0)
            {
                throw new SetupFailure("A's marker is visible before A committed");
            }

            var b = NewClient();
            var failed = new ConcurrentQueue<string>();
            b.Hooks.Failed = (_, exception) => failed.Enqueue(SqlStateOf(exception) ?? exception.GetType().Name);
            var backendId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bTask = Task.Run(() => InBracketAsync(
                b,
                bracket,
                async () =>
                {
                    var closed = await b.Store.CloseSessionAsync(session, Now.AddSeconds(2));

                    // Still usable after the conflict, or not.
                    await b.Context.Set<NoeliaRefreshToken>().AsNoTracking().CountAsync();
                    return closed;
                },
                backendId));

            var pidB = (await Task.WhenAny(backendId.Task, bTask, Task.Delay(Patience))) == backendId.Task
                ? backendId.Task.Result
                : throw new SetupFailure("B did not report its backend id inside the bracket");

            await WaitUntilBlockedByAsync(pidB, a.Pid, "B waits for A's uncommitted marker", bTask);

            await aTransaction.CommitAsync();
            var outcome = await BoundedAsync(bTask, "B");

            var markers = await RowsWithIdAsync(MarkerId(session.Value));
            var open = await OpenRowsAsync(session);

            using var scope = new AssertionScope();
            failed.Should().StartWith("23505", "the second insert of the marker is the key conflict");
            markers.Should().Be(1, "one marker per session, A's");
            open.Should().Be(0);

            if (bracket == Bracket.BeginTransaction)
            {
                outcome.Thrown.Should().BeNull("the savepoint takes the conflict and the store reads it as 'already marked': " + outcome);
                outcome.Probe.Should().BeNull();
                failed.Should().Equal(["23505"], "nothing else failed in B");
            }
            else
            {
                outcome.Thrown.Should().NotBeNull("the conflict aborts the transaction of the scope");
                outcome.SqlState.Should().Be("25P02", "the store's fresh check runs in an aborted transaction");
                outcome.Probe.Should().Be("25P02", "everything after the conflict in the scope fails until rollback");
            }
        });

    // ---- N5: the cap must not overwrite a rotation ----
    // A surplus row rotated between the cap's read and update keeps its rotation time as RevokedAt.

    [Fact]
    public Task N5_ACapAfterAConcurrentRotation_KeepsTheRotationTime() =>
        Bounded(async () =>
        {
            await ArrangeAsync();

            var session = SessionId.New();
            var subject = SubjectId.New();
            var surplusToken = NewToken();
            var surplus = await CreateAsync(session, subject, surplusToken, Now, "Z");
            var openToken = NewToken();
            await CreateAsync(session, subject, openToken, Now.AddSeconds(1), "P");

            var rotatedAt = Now.AddSeconds(5);
            var cappedAt = Now.AddSeconds(10);

            // G rotates P with an allowance of one: the cap will pick Z, the older row,
            // and stops after it has read the open rows, before it updates.
            var g = NewClient();
            var gate = NewGate(IsCapUpdate);
            gate.Attach(g);
            var gTask = Task.Run(() => PresentAsync(g, session, subject, openToken, cappedAt, allowance: 1));
            await ArrivedAsync(gate, gTask, "G at the update of its cap");

            if (!(await OpenRowsInHeapOrderAsync(session)).Contains(surplus))
            {
                throw new SetupFailure("Z must still be open when G has read the open rows");
            }

            // Rotated by hand on a second connection, so that no cap of its own runs.
            await using (var other = new NpgsqlConnection(Limited(_database.ConnectionString(enlist: false))))
            {
                await other.OpenAsync();
                await using var transaction = await other.BeginTransactionAsync();
                var successor = Guid.NewGuid();
                await using (var insert = new NpgsqlCommand($"""
                    INSERT INTO {Table}
                        ("Id", "SessionId", "SubjectId", "TokenHash", "IssuedAt", "ExpiresAt",
                         "SessionStartedAt", "SessionExpiresAt", "RevokedAt", "ReplacedBy", "ClientFingerprint")
                    SELECT @new, "SessionId", "SubjectId", sha256(uuid_send(gen_random_uuid())), @at, "ExpiresAt",
                           "SessionStartedAt", "SessionExpiresAt", NULL, NULL, "ClientFingerprint"
                    FROM {Table} WHERE "Id" = @old
                    """, other, transaction))
                {
                    insert.Parameters.AddWithValue("new", successor);
                    insert.Parameters.AddWithValue("old", surplus);
                    insert.Parameters.AddWithValue("at", rotatedAt.UtcDateTime);
                    await insert.ExecuteNonQueryAsync();
                }

                await using (var update = new NpgsqlCommand(
                    $"UPDATE {Table} SET \"RevokedAt\" = @at, \"ReplacedBy\" = @new WHERE \"Id\" = @old", other, transaction))
                {
                    update.Parameters.AddWithValue("new", successor);
                    update.Parameters.AddWithValue("old", surplus);
                    update.Parameters.AddWithValue("at", rotatedAt.UtcDateTime);
                    await update.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
            }

            gate.Release();
            var rotated = await BoundedAsync(gTask, "G");
            if (rotated.Outcome != ConsumeOutcome.Rotated)
            {
                throw new SetupFailure($"G's rotation was answered {rotated.Outcome}");
            }

            var revokedAt = await ScalarAsync<DateTime>($"SELECT \"RevokedAt\" FROM {Table} WHERE \"Id\" = @id", ("id", surplus));

            // After t_rot + grace and before t_cap + grace: a reuse by the first, a
            // race by the second, which is the window the overwrite opens.
            var presentedAt = rotatedAt + Grace + TimeSpan.FromSeconds(1);
            var answer = await PresentAsync(_plain, session, subject, surplusToken, presentedAt);

            using var scope = new AssertionScope();
            revokedAt.Should().Be(rotatedAt.UtcDateTime, "the cap closed a row that had been rotated meanwhile, and wrote over the moment of the rotation");
            answer.Outcome.Should().Be(
                ConsumeOutcome.ReuseDetected,
                "the grace of Z ended at t_rot + grace; the cap at t_cap > t_rot must not give it more");
        });

    // ---- the index ----

    [Fact]
    public Task CloseAll_AcrossACommittedRotation_UsesTheSessionIndex_LeavesNothingOpen() =>
        Bounded(async () =>
        {
            // Applied by the fixture right after CREATE DATABASE; it reaches new sessions only.
            await ArrangeAsync("enable_seqscan = off");

            var setting = (await _plain.Context.Database.SqlQueryRaw<string>(
                "SELECT current_setting('enable_seqscan') AS \"Value\"").ToListAsync()).Single();
            if (setting != "off")
            {
                throw new SetupFailure($"enable_seqscan is '{setting}' on the connection of the test");
            }

            var session = SessionId.New();
            var subject = SubjectId.New();
            var token = NewToken();
            await CreateAsync(session, subject, token, Now, "O1");

            // A rotation holds the open row and stops at its insert.
            var rotating = NewClient();
            var gate = NewGate(IsInsert);
            gate.Attach(rotating);
            var rotation = Task.Run(() => PresentAsync(rotating, session, subject, token, Now.AddSeconds(1)));
            await ArrivedAsync(gate, rotation, "the rotation holding O1");

            // CloseAll waits for it; the SQL of its pass is kept for the plan.
            var closer = NewClient();
            var backendId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? passSql = null;
            NpgsqlParameter[]? passParameters = null;
            closer.Hooks.Before = command =>
            {
                if (IsClosePass(command) && passSql is null)
                {
                    passSql = command.CommandText;
                    passParameters = command.Parameters.Cast<NpgsqlParameter>().Select(p => p.Clone()).ToArray();
                    backendId.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
                }

                return Task.CompletedTask;
            };
            var closing = Task.Run(async () => await closer.Store.CloseAllSessionsAsync(subject, Now.AddSeconds(2)));

            var pidCloser = (await Task.WhenAny(backendId.Task, closing, Task.Delay(Patience))) == backendId.Task
                ? backendId.Task.Result
                : throw new SetupFailure("CloseAll did not send a pass");
            await WaitUntilBlockedByAsync(pidCloser, rotating.Pid, "CloseAll waits for the rotation", closing);

            // The plan of the statement that was actually sent.
            var plan = new List<string>();
            await using (var explain = new NpgsqlCommand("EXPLAIN " + passSql, _observer))
            {
                foreach (var parameter in passParameters!)
                {
                    explain.Parameters.Add(parameter);
                }

                await using var reader = await explain.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    plan.Add(reader.GetString(0));
                }
            }

            gate.Release();
            var rotated = await BoundedAsync(rotation, "the rotation");
            if (rotated.Outcome != ConsumeOutcome.Rotated)
            {
                throw new SetupFailure($"the rotation was answered {rotated.Outcome}");
            }

            await BoundedAsync(closing, "CloseAll");

            var planText = string.Join(Environment.NewLine, plan);
            using var scope = new AssertionScope();
            planText.Should().Contain("IX_noelia_refresh_tokens_SessionId", "the pass of the close filters on SessionId; SQL: " + passSql);
            planText.Should().NotContain("Seq Scan");
            (await OpenRowsAsync(session)).Should().Be(
                0, "the successor was committed while the pass waited, and the next pass closes it; rows of S: " + await DumpAsync(session));
        });
}
