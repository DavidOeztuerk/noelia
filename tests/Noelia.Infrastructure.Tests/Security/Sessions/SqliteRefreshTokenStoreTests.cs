using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Noelia.Data.EntityFrameworkCore.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Noelia.Infrastructure.Tests.Security.Sessions;

/// <summary>
/// The same conformance suite against a real database.
/// </summary>
/// <remarks>
/// The in-process store satisfies it with one lock, which proves nothing about
/// SQL. Here the transition is a conditional update whose row count decides the
/// outcome, and the database is what enforces that exactly one caller wins.
/// <para>
/// SQLite has a single writer, so concurrent callers queue rather than collide.
/// A busy timeout keeps that queueing from surfacing as an error, which is what
/// a deployment would configure too.
/// </para>
/// </remarks>
[Trait("Category", "Unit")]
public class SqliteRefreshTokenStoreTests : RefreshTokenStoreConformance, IDisposable
{
    // A named in-memory database, kept alive by one open connection. Every
    // client opens its own connection to it, the way separate requests do.
    private readonly string _dataSource =
        $"file:noelia-{Guid.NewGuid():N}?mode=memory&cache=shared";

    private readonly SqliteConnection _keepAlive;
    private readonly List<SessionTestContext> _contexts = [];
    private readonly SessionTestContext _context;
    private readonly EntityFrameworkRefreshTokenStore<SessionTestContext> _store;

    public SqliteRefreshTokenStoreTests()
    {
        _keepAlive = new SqliteConnection($"DataSource={_dataSource}");
        _keepAlive.Open();

        _context = NewContext();
        _context.Database.EnsureCreated();
        _store = new EntityFrameworkRefreshTokenStore<SessionTestContext>(_context);
    }

    protected override IRefreshTokenStore Store => _store;

    protected override IRefreshTokenStore NewClient() =>
        new EntityFrameworkRefreshTokenStore<SessionTestContext>(NewContext());

    private SessionTestContext NewContext()
    {
        var connection = new SqliteConnection($"DataSource={_dataSource}");
        connection.Open();

        using (var command = connection.CreateCommand())
        {
            // SQLite has one writer; concurrent callers queue. Without this
            // they surface as SQLITE_BUSY instead of waiting their turn.
            command.CommandText = "PRAGMA busy_timeout = 5000;";
            command.ExecuteNonQuery();
        }

        var context = new SessionTestContext(
            new DbContextOptionsBuilder<SessionTestContext>().UseSqlite(connection).Options);

        lock (_contexts)
        {
            _contexts.Add(context);
        }

        return context;
    }

    protected override async Task<int> CountOpenAsync(SessionId session) =>
        await _context.Set<NoeliaRefreshToken>()
            .AsNoTracking()
            .CountAsync(token => token.SessionId == session.Value
                                 && token.RevokedAt == null
                                 && token.ReplacedBy == null);

    protected override async Task DeleteRowAsync(byte[] tokenHash) =>
        await _context.Set<NoeliaRefreshToken>()
            .Where(token => token.TokenHash == tokenHash)
            .ExecuteDeleteAsync();

    public void Dispose()
    {
        foreach (var context in _contexts)
        {
            context.Dispose();
        }

        _keepAlive.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>An application's context, with Noelia's table configured into it.</summary>
public sealed class SessionTestContext(DbContextOptions<SessionTestContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ConfigureNoeliaRefreshTokens();
}
