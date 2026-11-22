using Npgsql;
using Testcontainers.PostgreSql;

namespace Noelia.Infrastructure.Tests.Security.Sessions;

/// <summary>
/// One PostgreSQL container per test class, and a database of its own per test.
/// </summary>
/// <remarks>
/// Like <c>RedisFixture</c>, this records a failed start instead of throwing
/// from <see cref="InitializeAsync"/>, and the test class throws from its
/// constructor: a missing container runtime must fail the suite. A silently
/// skipped integration suite is indistinguishable from a passing one.
/// <para>
/// A database per test, created from <c>template0</c>, means no test shares a
/// row or a lock with another. Connections are not pooled: the point of the
/// concurrency tests is that each client is a separate backend process.
/// </para>
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    /// <summary>Why the container did not start, if it did not.</summary>
    public Exception? StartupFailure { get; private set; }

    /// <summary>True when the container runs and databases can be created.</summary>
    public bool IsRunning => _container is not null && StartupFailure is null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            StartupFailure = ex;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates <c>noelia_&lt;guid&gt;</c> from <c>template0</c>.
    /// </summary>
    /// <param name="databaseSettings">
    /// <c>ALTER DATABASE … SET</c> clauses such as <c>enable_seqscan = off</c>,
    /// applied right after creation. They only reach sessions opened afterwards,
    /// which is why they are given here and not set from a test's own connection.
    /// </param>
    public async Task<PostgresDatabase> CreateDatabaseAsync(params string[] databaseSettings)
    {
        if (_container is null || StartupFailure is not null)
        {
            throw new InvalidOperationException(
                "The PostgreSQL suite needs a container runtime. Start Docker and run again.", StartupFailure);
        }

        var name = $"noelia_{Guid.NewGuid():N}";
        var database = new PostgresDatabase(_container.GetConnectionString(), name);

        await using var admin = new NpgsqlConnection(database.AdminConnectionString);
        await admin.OpenAsync();

        // The name is a generated identifier, never input.
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0", admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        foreach (var setting in databaseSettings)
        {
            await using var alter = new NpgsqlCommand($"ALTER DATABASE \"{name}\" SET {setting}", admin);
            await alter.ExecuteNonQueryAsync();
        }

        return database;
    }
}

/// <summary>A database made by <see cref="PostgresFixture.CreateDatabaseAsync"/>.</summary>
public sealed class PostgresDatabase
{
    private readonly string _containerConnectionString;

    internal PostgresDatabase(string containerConnectionString, string name)
    {
        _containerConnectionString = containerConnectionString;
        Name = name;
    }

    /// <summary>The database name.</summary>
    public string Name { get; }

    internal string AdminConnectionString =>
        new NpgsqlConnectionStringBuilder(_containerConnectionString) { Pooling = false, Enlist = false }.ConnectionString;

    /// <summary>
    /// A connection string for one connection to this database.
    /// </summary>
    /// <param name="options">
    /// Backend options for the session, e.g. <c>-c deadlock_timeout=2s</c>.
    /// </param>
    /// <param name="enlist">
    /// False for helper connections that must never join the test's ambient
    /// transaction: a query on one of them is what shows what is committed.
    /// </param>
    public string ConnectionString(string? options = null, bool enlist = true)
    {
        var builder = new NpgsqlConnectionStringBuilder(_containerConnectionString)
        {
            Database = Name,
            Pooling = false,
            Enlist = enlist
        };

        if (options is not null)
        {
            builder.Options = options;
        }

        return builder.ConnectionString;
    }
}
