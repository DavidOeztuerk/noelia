using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Noelia.Infrastructure.Tests.Security.Sessions;

/// <summary>
/// The moment a sign-out and a refresh meet, on SQLite.
/// </summary>
/// <remarks>
/// SQLite admits one writer at a time, so these tests prove the order of the
/// steps the store takes, not the locking behaviour of a database with row
/// versions; that is what <see cref="PostgresRefreshTokenStoreMomentTests"/>
/// runs the same tests for. The tests are in <see cref="RefreshTokenStoreMomentTests"/>.
/// </remarks>
[Trait("Category", "Unit")]
public sealed class SqliteRefreshTokenStoreMomentTests : RefreshTokenStoreMomentTests
{
    private readonly string _dataSource =
        $"file:noelia-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private SqliteConnection? _keepAlive;

    protected override Task CreateDatabaseAsync()
    {
        // The named in-memory database lives as long as one connection to it.
        _keepAlive = new SqliteConnection($"DataSource={_dataSource}");
        _keepAlive.Open();
        return Task.CompletedTask;
    }

    protected override Task ReleaseDatabaseAsync()
    {
        _keepAlive?.Dispose();
        return Task.CompletedTask;
    }

    protected override DbConnection OpenConnection()
    {
        var connection = new SqliteConnection($"DataSource={_dataSource}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    protected override void UseProvider(DbContextOptionsBuilder<SessionTestContext> options, DbConnection connection) =>
        options.UseSqlite((SqliteConnection)connection);

    protected override void RotateByHand(DbCommand source, Guid rowId, DateTimeOffset at)
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

    protected override void PlantMarkerByHand(DbCommand source, Guid session) =>
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
}
