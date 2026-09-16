using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Noelia.Data.EntityFrameworkCore.Sessions;
using Noelia.Infrastructure.Security.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Noelia.Infrastructure.Tests.Security.Sessions;

/// <summary>
/// The store inside a unit of work the caller owns.
/// </summary>
/// <remarks>
/// An application that writes an audit row in the same transaction as the change
/// it records has a transaction open before it reaches the store. Signing in
/// joins it, because a save joins whatever is open; consuming a token opened one
/// of its own and the connection refused the second. The line ran through the
/// middle of one interface, which is the part that made it a fault rather than a
/// missing feature.
/// </remarks>
[Trait("Category", "Unit")]
public class TransactionBracketTests : IDisposable
{
    private readonly string _dataSource =
        $"file:noelia-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection _keepAlive;
    private readonly List<SessionTestContext> _contexts = [];
    private readonly RaceInterceptor _race = new();
    private readonly SessionTestContext _context;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly TokenSessionService _sessions;

    public TransactionBracketTests()
    {
        _keepAlive = new SqliteConnection($"DataSource={_dataSource}");
        _keepAlive.Open();

        _context = NewContext();
        _context.Database.EnsureCreated();

        _sessions = new TokenSessionService(
            new EntityFrameworkRefreshTokenStore<SessionTestContext>(_context),
            Options.Create(new TokenSessionOptions()),
            _clock,
            NullLogger<TokenSessionService>.Instance,
            new SessionObservations());
    }

    /// <summary>
    /// The half that always worked, kept as the measure for the other half.
    /// </summary>
    [Fact]
    public async Task Eine_Anmeldung_laeuft_in_einer_offenen_Klammer()
    {
        await using var klammer = await _context.Database.BeginTransactionAsync();

        var angemeldet = await _sessions.SignInAsync(SubjectId.New());
        await klammer.CommitAsync();

        angemeldet.RefreshToken.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The half that did not, turned around: the reproduction from the report
    /// with its expectation inverted.
    /// </summary>
    [Fact]
    public async Task Eine_Erneuerung_laeuft_auch_in_einer_offenen_Klammer()
    {
        var angemeldet = await _sessions.SignInAsync(SubjectId.New());

        RefreshResult erneuert;
        await using (var klammer = await _context.Database.BeginTransactionAsync())
        {
            erneuert = await _sessions.RefreshAsync(angemeldet.RefreshToken);
            await klammer.CommitAsync();
        }

        erneuert.Outcome.Should().Be(ConsumeOutcome.Rotated);
        (await ExistsAsync(erneuert.RefreshToken!))
            .Should().BeTrue("the successor is committed with the caller's own work, not before it");
    }

    /// <summary>
    /// One bracket and not two: the caller's rollback has to take the rotation
    /// with it.
    /// </summary>
    /// <remarks>
    /// Committing separately would pass the test above and fail this one — the
    /// row would be there either way, but the old token would already be dead.
    /// </remarks>
    [Fact]
    public async Task Ein_Rollback_des_Aufrufers_laesst_den_alten_Token_wieder_gelten()
    {
        var angemeldet = await _sessions.SignInAsync(SubjectId.New());

        await using (var klammer = await _context.Database.BeginTransactionAsync())
        {
            var erneuert = await _sessions.RefreshAsync(angemeldet.RefreshToken);
            erneuert.Outcome.Should().Be(ConsumeOutcome.Rotated);
            await klammer.RollbackAsync();
        }

        var nochmal = await _sessions.RefreshAsync(angemeldet.RefreshToken);

        nochmal.Outcome.Should().Be(ConsumeOutcome.Rotated, "the rotation was undone with everything else");
    }

    /// <summary>
    /// The loser of the race, inside a caller's bracket: the answer is the same
    /// and the caller keeps its work.
    /// </summary>
    [Fact]
    public async Task Wiederverwendung_in_fremder_Klammer_schliesst_die_Sitzung_und_laesst_dem_Aufrufer_seine_Arbeit()
    {
        var (verloren, geschwister) = await RennenVorbereitenAsync();

        SignInResult fremd;
        await using (var klammer = await _context.Database.BeginTransactionAsync())
        {
            fremd = await _sessions.SignInAsync(SubjectId.New());
            _race.InvalidateBeforeNextUpdate(Hash(verloren));

            var wiederholt = await _sessions.RefreshAsync(verloren);
            wiederholt.Outcome.Should().Be(ConsumeOutcome.ReuseDetected);

            await klammer.CommitAsync();
        }

        (await _sessions.RefreshAsync(geschwister)).Outcome
            .Should().Be(ConsumeOutcome.Revoked, "the session was really closed, not merely reported closed");
        (await _sessions.RefreshAsync(fremd.RefreshToken)).Outcome
            .Should().Be(ConsumeOutcome.Rotated, "the caller's own sign-in was never the store's to discard");
    }

    /// <summary>
    /// And without a bracket, where the store owns the transaction: ending the
    /// session is a write, and it has to be committed rather than dropped on the
    /// way out.
    /// </summary>
    /// <remarks>
    /// The zero-row path returns early. If that return happens while the store's
    /// own transaction is still open, disposing it rolls back the closure that
    /// the answer already announced — and the reported theft leaves every other
    /// token of the session alive.
    /// </remarks>
    [Fact]
    public async Task Wiederverwendung_ohne_Klammer_schliesst_die_Sitzung_wirklich()
    {
        var (verloren, geschwister) = await RennenVorbereitenAsync();

        _race.InvalidateBeforeNextUpdate(Hash(verloren));
        var wiederholt = await _sessions.RefreshAsync(verloren);

        wiederholt.Outcome.Should().Be(ConsumeOutcome.ReuseDetected);
        (await _sessions.RefreshAsync(geschwister)).Outcome
            .Should().Be(ConsumeOutcome.Revoked, "the session was really closed, not merely reported closed");
    }

    /// <summary>
    /// Leaves a session holding two open tokens and hands back the one whose
    /// row the race will move out from under the update.
    /// </summary>
    private async Task<(string Verloren, string Geschwister)> RennenVorbereitenAsync()
    {
        var angemeldet = await _sessions.SignInAsync(SubjectId.New());

        var erste = await _sessions.RefreshAsync(angemeldet.RefreshToken);
        erste.Outcome.Should().Be(ConsumeOutcome.Rotated);

        // The same token again inside the grace window: a sibling for the same
        // sign-in, so closing the session has something to close.
        var geschwister = await _sessions.RefreshAsync(angemeldet.RefreshToken);
        geschwister.Outcome.Should().Be(ConsumeOutcome.RotatedWithinGrace);

        return (erste.RefreshToken!, geschwister.RefreshToken!);
    }

    private async Task<bool> ExistsAsync(string token)
    {
        var hash = Hash(token);
        await using var reader = NewContext();
        return await reader.Set<NoeliaRefreshToken>().AsNoTracking()
            .AnyAsync(row => row.TokenHash == hash);
    }

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private SessionTestContext NewContext()
    {
        var connection = new SqliteConnection($"DataSource={_dataSource}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA busy_timeout = 5000;";
            command.ExecuteNonQuery();
        }

        var context = new SessionTestContext(
            new DbContextOptionsBuilder<SessionTestContext>()
                .UseSqlite(connection)
                .AddInterceptors(_race)
                .Options);

        _contexts.Add(context);
        return context;
    }

    public void Dispose()
    {
        foreach (var context in _contexts)
        {
            context.Dispose();
        }

        _keepAlive.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Moves the row between the store's read and its conditional update.
    /// </summary>
    /// <remarks>
    /// The zero-row path is a race by definition, and a race is not a thing a
    /// test can arrange by calling in a particular order: reaching the store
    /// twice from outside means the second call reads what the first already
    /// wrote and takes the rotated branch instead. So the interference is placed
    /// where it actually happens — on the same connection and inside whatever
    /// transaction is open, one command before the update that expects to win.
    /// </remarks>
    private sealed class RaceInterceptor : DbCommandInterceptor
    {
        private byte[]? _hash;

        public void InvalidateBeforeNextUpdate(byte[] tokenHash) => _hash = tokenHash;

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (_hash is not { } hash
                || !command.CommandText.Contains("UPDATE", StringComparison.Ordinal)
                || !command.CommandText.Contains("noelia_refresh_tokens", StringComparison.Ordinal))
            {
                return ValueTask.FromResult(result);
            }

            _hash = null;

            using var winner = command.Connection!.CreateCommand();
            winner.Transaction = command.Transaction;
            // ReplacedBy takes the row's own id: the store reads only whether it
            // is set, and copying a column sidesteps how a Guid is stored.
            winner.CommandText =
                """UPDATE noelia_refresh_tokens SET "ReplacedBy" = "Id" WHERE "TokenHash" = $hash""";
            var parameter = winner.CreateParameter();
            parameter.ParameterName = "$hash";
            parameter.Value = hash;
            winner.Parameters.Add(parameter);
            winner.ExecuteNonQuery();

            return ValueTask.FromResult(result);
        }
    }
}
