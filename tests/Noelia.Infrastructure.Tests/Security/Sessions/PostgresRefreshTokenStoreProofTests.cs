using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;
using Noelia.Data.EntityFrameworkCore.Sessions;
using Npgsql;

namespace Noelia.Infrastructure.Tests.Security.Sessions;

/// <summary>
/// What ADR-0008 promises about a sign-out, put to a real PostgreSQL: the F4
/// cases (a close inside the caller's transaction), the marker conflict, the
/// cap overwriting a rotation (N5), and the index the close uses.
/// </summary>
/// <remarks>
/// What a test reports as <c>Aufbaufehler</c> is not a verdict: the arrangement did not reach
/// the state the case is about (the cycle was not formed, the wrong party was
/// the deadlock victim, a precondition was violated), and no verdict about the
/// store can be read from it.
/// <para>
/// There is no sleeping to place an interleaving: a statement is held in an
/// interceptor, and the test waits for a state of the database
/// (<c>pg_blocking_pids</c>) with a deadline. Every test is bounded by thirty
/// seconds, every connection by a command timeout, so none can hang.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed partial class PostgresRefreshTokenStoreProofTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private const string Table = "noelia_refresh_tokens";

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;
    private readonly List<Client> _clients = [];
    private readonly List<ProofGate> _gates = [];
    private readonly Dictionary<Guid, string> _labels = [];
    private PostgresDatabase _database = null!;
    private NpgsqlConnection _observer = null!;
    private Client _plain = null!;

    public PostgresRefreshTokenStoreProofTests(PostgresFixture fixture)
    {
        _fixture = fixture;

        if (!fixture.IsRunning)
        {
            throw new InvalidOperationException(
                "The PostgreSQL proof tests need a container runtime. Start Docker and run again.",
                fixture.StartupFailure);
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        // Held clients are let go first, so that nothing keeps a lock on the way out.
        foreach (var gate in _gates)
        {
            gate.Release();
        }

        foreach (var client in _clients)
        {
            client.Dispose();
        }

        _observer?.Dispose();
        return Task.CompletedTask;
    }

    // ---- arrangement ----

    /// <summary>A database of its own for the test, the schema in it, and the helper connection.</summary>
    private async Task ArrangeAsync(params string[] databaseSettings)
    {
        _database = await _fixture.CreateDatabaseAsync(databaseSettings);

        // Enlist=false: what this connection sees is what is committed, whatever
        // ambient transaction another thread of the test is in.
        _observer = new NpgsqlConnection(Limited(_database.ConnectionString(enlist: false)));
        await _observer.OpenAsync();

        _plain = NewClient();
        await _plain.Context.Database.EnsureCreatedAsync();
    }

    /// <summary>The test body, or a verdict that it hung: a hang is never left to the runner.</summary>
    private static async Task Bounded(Func<Task> body)
    {
        try
        {
            await body().WaitAsync(TestLimit);
        }
        catch (TimeoutException)
        {
            throw new SetupFailure($"the test did not finish within {TestLimit.TotalSeconds:0} s (hanging)");
        }
    }

    private static string Limited(string connectionString) => connectionString + ";Command Timeout=25";

    private Client NewClient(string? options = null, bool enlist = true)
    {
        var connection = new NpgsqlConnection(Limited(_database.ConnectionString(options, enlist)));
        connection.Open();

        var hooks = new ProofHooks();
        var builder = new DbContextOptionsBuilder<SessionTestContext>();
        builder.UseNpgsql(connection);
        builder.AddInterceptors(hooks);

        var context = new SessionTestContext(builder.Options);
        var client = new Client(
            context, new EntityFrameworkRefreshTokenStore<SessionTestContext>(context), hooks, connection);

        lock (_clients)
        {
            _clients.Add(client);
        }

        return client;
    }

    private ProofGate NewGate(Func<DbCommand, bool> hold)
    {
        var gate = new ProofGate(hold);
        lock (_gates)
        {
            _gates.Add(gate);
        }

        return gate;
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static RefreshTokenRecord Record(SessionId session, SubjectId subject, string token, DateTimeOffset issuedAt) =>
        new(
            RefreshTokenId.New(),
            session,
            subject,
            Hash(token),
            issuedAt,
            Now + TimeSpan.FromDays(14),
            Now,
            Now + TimeSpan.FromDays(30),
            null,
            null,
            null);

    private async Task<Guid> CreateAsync(SessionId session, SubjectId subject, string token, DateTimeOffset issuedAt, string label)
    {
        await _plain.Store.CreateAsync(Record(session, subject, token, issuedAt));
        var id = await IdOfAsync(token);
        _labels[id] = label;
        return id;
    }

    /// <summary>Presents <paramref name="token"/>, with a successor whose token is not kept.</summary>
    private static Task<ConsumeResult> PresentAsync(
        Client client, SessionId session, SubjectId subject, string token, DateTimeOffset now, int allowance = 5) =>
        RotateAsync(client, session, subject, token, NewToken(), now, allowance);

    /// <summary>Presents <paramref name="token"/>; the successor it may become is <paramref name="next"/>.</summary>
    private static Task<ConsumeResult> RotateAsync(
        Client client, SessionId session, SubjectId subject, string token, string next, DateTimeOffset now, int allowance = 5) =>
        client.Store.TryConsumeAsync(Hash(token), Record(session, subject, next, now), now, Grace, allowance);

    // ---- what the helper connection sees: committed state only ----

    private async Task<T> ScalarAsync<T>(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, _observer);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }

    private Task<long> OpenRowsAsync(SessionId session) =>
        ScalarAsync<long>(
            $"SELECT count(*) FROM {Table} WHERE \"SessionId\" = @s AND \"RevokedAt\" IS NULL AND \"ReplacedBy\" IS NULL",
            ("s", session.Value));

    private Task<long> RowsWithIdAsync(Guid id) =>
        ScalarAsync<long>($"SELECT count(*) FROM {Table} WHERE \"Id\" = @id", ("id", id));

    private Task<Guid> IdOfAsync(string token) =>
        ScalarAsync<Guid>($"SELECT \"Id\" FROM {Table} WHERE \"TokenHash\" = @h", ("h", Hash(token)));

    private Task<int[]> BlockingPidsAsync(int pid) =>
        ScalarAsync<int[]>("SELECT pg_blocking_pids(@pid)", ("pid", pid));

    private Task<long> DeadlocksAsync() =>
        ScalarAsync<long>("SELECT deadlocks FROM pg_stat_database WHERE datname = current_database()");

    /// <summary>True when another transaction holds the row: a row that cannot be locked without waiting is skipped.</summary>
    private async Task<bool> IsHeldAsync(Guid id)
    {
        await using var transaction = await _observer.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT 1 FROM {Table} WHERE \"Id\" = @id FOR UPDATE SKIP LOCKED", _observer, transaction);
        command.Parameters.AddWithValue("id", id);
        var got = await command.ExecuteScalarAsync();
        await transaction.RollbackAsync();
        return got is null;
    }

    private async Task<List<Guid>> OpenRowsInHeapOrderAsync(SessionId session)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT \"Id\" FROM {Table} WHERE \"SessionId\" = @s AND \"RevokedAt\" IS NULL AND \"ReplacedBy\" IS NULL ORDER BY ctid",
            _observer);
        command.Parameters.AddWithValue("s", session.Value);
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private string Label(Guid id) => _labels.TryGetValue(id, out var label) ? label : id.ToString("N")[..8];

    /// <summary>The committed rows of a session, in the words of the test, for a failure message.</summary>
    private async Task<string> DumpAsync(SessionId session)
    {
        var text = new StringBuilder();
        await using var command = new NpgsqlCommand(
            $"SELECT \"Id\", \"IssuedAt\", \"RevokedAt\", \"ReplacedBy\" FROM {Table} WHERE \"SessionId\" = @s ORDER BY \"IssuedAt\", ctid",
            _observer);
        command.Parameters.AddWithValue("s", session.Value);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetGuid(0);
            var marker = id == MarkerId(session.Value) ? "marker" : Label(id);
            var revoked = reader.IsDBNull(2) ? "null" : reader.GetDateTime(2).ToString("HH:mm:ss");
            var replaced = reader.IsDBNull(3) ? "null" : Label(reader.GetGuid(3));
            text.Append($"[{marker} issued={reader.GetDateTime(1):HH:mm:ss} revoked={revoked} replacedBy={replaced}] ");
        }

        return text.ToString();
    }

    /// <summary>The fixed derivation of the marker, spelled out a second time on purpose.</summary>
    private static Guid MarkerId(Guid session)
    {
        var domain = Encoding.UTF8.GetBytes("noelia.refresh-token.session-ended.v1");
        var hash = SHA256.HashData([.. domain, .. session.ToByteArray()]);
        return new Guid(hash.AsSpan(0, 16));
    }

    // ---- waiting: states, with a deadline ----

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, params Task[] failsIfDone)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!await condition())
        {
            if (failsIfDone.Any(task => task.IsCompleted))
            {
                throw new SetupFailure($"{what}: a party ended before the state was reached");
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new SetupFailure($"{what}: not reached within {Patience.TotalSeconds:0} s");
            }

            await Task.Delay(20);
        }
    }

    private Task WaitUntilBlockedByAsync(int waiting, int holder, string what, params Task[] failsIfDone) =>
        WaitUntilAsync(
            async () => (await BlockingPidsAsync(waiting)) is [var only] && only == holder,
            what,
            failsIfDone);

    private static async Task<T> BoundedAsync<T>(Task<T> task, string what)
    {
        try
        {
            return await task.WaitAsync(Patience);
        }
        catch (TimeoutException)
        {
            throw new SetupFailure($"{what}: not finished within {Patience.TotalSeconds:0} s");
        }
    }

    private static async Task BoundedAsync(Task task, string what)
    {
        try
        {
            await task.WaitAsync(Patience);
        }
        catch (TimeoutException)
        {
            throw new SetupFailure($"{what}: not finished within {Patience.TotalSeconds:0} s");
        }
    }

    /// <summary>Waits for a held client to arrive at its hold; fails if it ended first.</summary>
    private static async Task ArrivedAsync(ProofGate gate, Task partyThatShouldNotEnd, string what)
    {
        var first = await Task.WhenAny(gate.Arrived, partyThatShouldNotEnd, Task.Delay(Patience));
        if (!ReferenceEquals(first, gate.Arrived))
        {
            var ended = ReferenceEquals(first, partyThatShouldNotEnd) ? "ended before the hold" : "did not arrive";
            throw new SetupFailure($"{what}: {ended}");
        }
    }

    // ---- the caller's transaction ----

    public enum Bracket
    {
        BeginTransaction,
        TransactionScope
    }

    /// <summary>What happened to a party that ran in a bracket.</summary>
    private sealed record Outcome(Exception? Thrown, long? Result, string? Probe)
    {
        public string? SqlState => SqlStateOf(Thrown);

        public override string ToString() =>
            Thrown is null
                ? $"returned {Result}"
                : $"threw {Thrown.GetType().Name} (SQLSTATE {SqlState ?? "none"}): {Thrown.Message}";
    }

    private static string? SqlStateOf(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres)
            {
                return postgres.SqlState;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs <paramref name="call"/> in the caller's transaction of the given kind,
    /// reads the backend id inside it, and commits only if the call did not throw.
    /// </summary>
    /// <remarks>
    /// After a failure the bracket is left as an application leaves it: rolled
    /// back. The scope variant probes the aborted transaction once first, because
    /// "the transaction is aborted" is itself a result (25P02).
    /// </remarks>
    private static async Task<Outcome> InBracketAsync(
        Client client,
        Bracket bracket,
        Func<Task<long>> call,
        TaskCompletionSource<int> backendId)
    {
        Exception? thrown = null;
        long? result = null;
        string? probe = null;

        async Task<int> PidAsync() =>
            (await client.Context.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").ToListAsync()).Single();

        if (bracket == Bracket.BeginTransaction)
        {
            IDbContextTransaction? transaction = null;
            try
            {
                transaction = await client.Context.Database.BeginTransactionAsync();
                backendId.TrySetResult(await PidAsync());
                result = await call();
                await transaction.CommitAsync();
            }
            catch (Exception exception)
            {
                thrown = exception;
            }
            finally
            {
                if (transaction is not null)
                {
                    try
                    {
                        await transaction.DisposeAsync();
                    }
                    catch (Exception)
                    {
                        // The rollback of a transaction the server already aborted.
                    }
                }
            }

            return new Outcome(thrown, result, probe);
        }

        TransactionScope? scope = null;
        try
        {
            scope = new TransactionScope(
                TransactionScopeOption.Required, TimeSpan.FromSeconds(60), TransactionScopeAsyncFlowOption.Enabled);
            backendId.TrySetResult(await PidAsync());
            result = await call();
            scope.Complete();
        }
        catch (Exception exception)
        {
            thrown = exception;

            try
            {
                await client.Context.Set<NoeliaRefreshToken>().AsNoTracking().CountAsync();
                probe = "usable";
            }
            catch (Exception next)
            {
                probe = SqlStateOf(next) ?? next.GetType().Name;
            }
        }
        finally
        {
            try
            {
                scope?.Dispose();
            }
            catch (Exception)
            {
                // The scope of a transaction that is aborted and is rolled back here.
            }
        }

        return new Outcome(thrown, result, probe);
    }

    // ---- harness types ----

    private sealed class SetupFailure(string message) : Exception("Aufbaufehler: " + message);

    private sealed record Client(
        SessionTestContext Context,
        EntityFrameworkRefreshTokenStore<SessionTestContext> Store,
        ProofHooks Hooks,
        NpgsqlConnection Connection) : IDisposable
    {
        /// <summary>The backend process of this client's connection.</summary>
        public int Pid => Connection.ProcessID;

        public void Dispose()
        {
            try
            {
                Context.Dispose();
                Connection.Dispose();
            }
            catch (Exception)
            {
                // Leaving the test: nothing to add to a failure that is already reported.
            }
        }
    }

    private sealed class ProofHooks : DbCommandInterceptor
    {
        public Func<DbCommand, Task>? Before { get; set; }

        public Func<DbCommand, Task>? After { get; set; }

        public Action<DbCommand, Exception>? Failed { get; set; }

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
            if (After is { } after)
            {
                await after(command);
            }

            return result;
        }

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (After is { } after)
            {
                await after(command);
            }

            return result;
        }

        public override Task CommandFailedAsync(
            DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            Failed?.Invoke(command, eventData.Exception);
            return Task.CompletedTask;
        }

        private Task RunBefore(DbCommand command) => Before?.Invoke(command) ?? Task.CompletedTask;
    }

    /// <summary>Holds a client at the first command that matches, until the test lets it go.</summary>
    private sealed class ProofGate(Func<DbCommand, bool> hold)
    {
        private readonly TaskCompletionSource _arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _held;

        public Task Arrived => _arrived.Task;

        public void Release() => _release.TrySetResult();

        public void Attach(Client client) =>
            client.Hooks.Before = command => hold(command) ? HoldAsync() : Task.CompletedTask;

        private async Task HoldAsync()
        {
            if (Interlocked.Exchange(ref _held, 1) != 0)
            {
                return;
            }

            _arrived.SetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(25));
        }
    }

    // What EF writes for a conditional update with no change: the column is set
    // to itself. Npgsql writes the alias bare, so both shapes match.
    private static bool IsNoopLock(DbCommand command) =>
        Regex.IsMatch(command.CommandText, "SET \"RevokedAt\" = ((\"\\w+\"|\\w+)\\.)?\"RevokedAt\"");

    private static bool IsInsert(DbCommand command) =>
        command.CommandText.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase);

    // The pass of a close: an update that sets RevokedAt to a value.
    private static bool IsClosePass(DbCommand command) =>
        command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
        && !IsNoopLock(command);

    // The read of the cap: the only ordered read a rotation makes after its insert.
    private static bool IsCapRead(DbCommand command) =>
        command.CommandText.Contains("ORDER BY", StringComparison.Ordinal);

    // The cap's own update: it sets RevokedAt and, unlike the claim, not ReplacedBy.
    private static bool IsCapUpdate(DbCommand command) =>
        IsClosePass(command) && !command.CommandText.Contains("\"ReplacedBy\" =", StringComparison.Ordinal);
}
