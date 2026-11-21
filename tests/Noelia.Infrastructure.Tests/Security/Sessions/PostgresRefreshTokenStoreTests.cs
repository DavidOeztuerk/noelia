using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Noelia.Data.EntityFrameworkCore.Sessions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Noelia.Infrastructure.Tests.Security.Sessions;

/// <summary>
/// The conformance suite against PostgreSQL.
/// </summary>
/// <remarks>
/// SQLite queues its writers; PostgreSQL lets them collide on row locks and
/// re-checks a waiting update's condition when the lock is released. Whether the
/// conditional update still decides exactly one winner is what only this class
/// shows.
/// </remarks>
[Trait("Category", "Integration")]
public class PostgresRefreshTokenStoreTests
    : RefreshTokenStoreConformance, IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly List<SessionTestContext> _contexts = [];
    private PostgresDatabase _database = null!;
    private SessionTestContext _context = null!;
    private EntityFrameworkRefreshTokenStore<SessionTestContext> _store = null!;

    public PostgresRefreshTokenStoreTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        if (!fixture.IsRunning)
        {
            throw new InvalidOperationException(
                "The PostgreSQL conformance suite needs a container runtime. Start Docker and run again.",
                fixture.StartupFailure);
        }
    }

    public async Task InitializeAsync()
    {
        _database = await _fixture.CreateDatabaseAsync();
        _context = NewContext();
        await _context.Database.EnsureCreatedAsync();
        _store = new EntityFrameworkRefreshTokenStore<SessionTestContext>(_context);
    }

    public Task DisposeAsync()
    {
        lock (_contexts)
        {
            foreach (var context in _contexts)
            {
                context.Dispose();
            }
        }

        return Task.CompletedTask;
    }

    protected override IRefreshTokenStore Store => _store;

    protected override IRefreshTokenStore NewClient() =>
        new EntityFrameworkRefreshTokenStore<SessionTestContext>(NewContext());

    private SessionTestContext NewContext()
    {
        // Pooling is off in the string, so each client is its own backend.
        var connection = new NpgsqlConnection(_database.ConnectionString());
        connection.Open();

        var context = new SessionTestContext(
            new DbContextOptionsBuilder<SessionTestContext>().UseNpgsql(connection).Options);

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
}
