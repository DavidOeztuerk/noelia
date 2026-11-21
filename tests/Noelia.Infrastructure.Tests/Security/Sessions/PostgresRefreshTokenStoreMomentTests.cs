using System.Data.Common;
using System.Transactions;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Noelia.Infrastructure.Tests.Security.Sessions;

/// <summary>
/// The moment a sign-out and a refresh meet, on PostgreSQL.
/// </summary>
/// <remarks>
/// The same tests as on SQLite (<see cref="RefreshTokenStoreMomentTests"/>), line
/// for line, against a database whose writers do not queue. Where a test is red
/// here, the store or the assumption behind it is what is wrong; the test is not
/// adjusted to the database.
/// </remarks>
[Trait("Category", "Integration")]
public sealed class PostgresRefreshTokenStoreMomentTests
    : RefreshTokenStoreMomentTests, IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;
    private PostgresDatabase _database = null!;

    public PostgresRefreshTokenStoreMomentTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        if (!fixture.IsRunning)
        {
            throw new InvalidOperationException(
                "The PostgreSQL moment tests need a container runtime. Start Docker and run again.",
                fixture.StartupFailure);
        }
    }

    protected override async Task CreateDatabaseAsync() =>
        _database = await _fixture.CreateDatabaseAsync();

    protected override DbConnection OpenConnection()
    {
        // Not pooled: each client is a backend of its own, as separate requests are.
        var connection = new NpgsqlConnection(_database.ConnectionString());
        connection.Open();
        return connection;
    }

    protected override void UseProvider(DbContextOptionsBuilder<SessionTestContext> options, DbConnection connection) =>
        options.UseNpgsql((NpgsqlConnection)connection);

    protected override void RotateByHand(DbCommand source, Guid rowId, DateTimeOffset at)
    {
        var successor = Guid.NewGuid();

        Run(source, """
            INSERT INTO noelia_refresh_tokens
                ("Id", "SessionId", "SubjectId", "TokenHash", "IssuedAt", "ExpiresAt",
                 "SessionStartedAt", "SessionExpiresAt", "RevokedAt", "ReplacedBy", "ClientFingerprint")
            SELECT @new, "SessionId", "SubjectId", sha256(uuid_send(gen_random_uuid())), @at, "ExpiresAt",
                   "SessionStartedAt", "SessionExpiresAt", NULL, NULL, "ClientFingerprint"
            FROM noelia_refresh_tokens WHERE "Id" = @old
            """,
            ("@new", successor), ("@old", rowId), ("@at", at.UtcDateTime));
        Run(source, """
            UPDATE noelia_refresh_tokens SET "RevokedAt" = @at, "ReplacedBy" = @new WHERE "Id" = @old
            """,
            ("@new", successor), ("@old", rowId), ("@at", at.UtcDateTime));
    }

    protected override void PlantMarkerByHand(DbCommand source, Guid session) =>
        Run(source, """
            INSERT INTO noelia_refresh_tokens
                ("Id", "SessionId", "SubjectId", "TokenHash", "IssuedAt", "ExpiresAt",
                 "SessionStartedAt", "SessionExpiresAt", "RevokedAt", "ReplacedBy", "ClientFingerprint")
            SELECT @id, "SessionId", "SubjectId", sha256(uuid_send(gen_random_uuid())), "IssuedAt", "IssuedAt",
                   "SessionStartedAt", "SessionExpiresAt", "IssuedAt", NULL, NULL
            FROM noelia_refresh_tokens WHERE "SessionId" = @session LIMIT 1
            """,
            ("@id", MarkerId(session)), ("@session", session));

    // ---- the TransactionScope half the SQLite tests could not show ----
    //
    // Npgsql enlists a connection in an ambient transaction, so here the work
    // done under a scope really commits and rolls back with it. The rows are
    // counted on a connection that does not enlist (Enlist=false): what it sees
    // is what is committed, which is the only thing that says anything.

    [Fact]
    public async Task Rotation_InsideATransactionScope_IsRolledBackWithoutComplete()
    {
        var (session, subject, first) = await SignInAsync();
        var tab = NewScopeClient(out _);

        ConsumeResult result;
        using (new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            result = await Present(tab, session, subject, first, Now.AddSeconds(1));
            // No Complete().
        }

        result.Outcome.Should().Be(ConsumeOutcome.Rotated);
        (await CommittedRowsAsync(session)).Should().Be(1, "the successor was written in the scope and the scope was abandoned");
        (await OpenAsync(session)).Should().Be(1, "the presented token is still the open one");
    }

    [Fact]
    public async Task Rotation_InsideATransactionScope_CommitsWithComplete_NotBefore()
    {
        var (session, subject, first) = await SignInAsync();
        var tab = NewScopeClient(out _);

        ConsumeResult result;
        using (var scope = new TransactionScope(
                   TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            result = await Present(tab, session, subject, first, Now.AddSeconds(1));
            (await CommittedRowsAsync(session)).Should().Be(1, "another connection sees nothing before the commit");
            scope.Complete();
            (await CommittedRowsAsync(session)).Should().Be(1, "Complete() alone does not commit; leaving the scope does");
        }

        result.Outcome.Should().Be(ConsumeOutcome.Rotated);
        (await CommittedRowsAsync(session)).Should().Be(2);
        (await OpenAsync(session)).Should().Be(1, "only the successor is open");
    }

    [Fact]
    public async Task Grace_InsideATransactionScope_IsRolledBackWithoutComplete()
    {
        var (session, subject, first) = await SignInAsync();
        await RotateAsync(NewClient(), session, subject, first, Now);
        var tab = NewScopeClient(out _);

        ConsumeResult result;
        using (new TransactionScope(TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            result = await Present(tab, session, subject, first, Now.AddSeconds(1));
        }

        result.Outcome.Should().Be(ConsumeOutcome.RotatedWithinGrace);
        (await CommittedRowsAsync(session)).Should().Be(2, "the sibling of the grace branch was rolled back");
        (await OpenAsync(session)).Should().Be(1);
    }

    [Fact]
    public async Task Grace_InsideATransactionScope_CommitsWithComplete_NotBefore()
    {
        var (session, subject, first) = await SignInAsync();
        await RotateAsync(NewClient(), session, subject, first, Now);
        var tab = NewScopeClient(out _);

        ConsumeResult result;
        using (var scope = new TransactionScope(
                   TransactionScopeOption.Required, TransactionScopeAsyncFlowOption.Enabled))
        {
            result = await Present(tab, session, subject, first, Now.AddSeconds(1));
            (await CommittedRowsAsync(session)).Should().Be(2, "the sibling is not visible to another connection before the commit");
            scope.Complete();
        }

        result.Outcome.Should().Be(ConsumeOutcome.RotatedWithinGrace);
        (await CommittedRowsAsync(session)).Should().Be(3);
        (await OpenAsync(session)).Should().Be(2, "the end of the chain and the sibling");
    }

    /// <summary>The rows of the session as another, non-enlisting connection sees them.</summary>
    private async Task<long> CommittedRowsAsync(SessionId session)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString(enlist: false));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM noelia_refresh_tokens WHERE \"SessionId\" = @session", connection);
        command.Parameters.AddWithValue("session", session.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
