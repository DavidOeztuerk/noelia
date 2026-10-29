using System.Collections.Concurrent;

namespace Noelia.Infrastructure.Security.M2M;

/// <summary>
/// The in-process store of service tokens, one per client and audience.
/// </summary>
/// <remarks>
/// A singleton on purpose: <see cref="ServiceTokenProvider"/> is a typed
/// <c>HttpClient</c> and so transient, and a cache that lived in it would fetch a new
/// token on every resolution. It lives in the process and nowhere else. A service token
/// in a shared cache would be readable, and replayable, by everything that can read the
/// cache.
/// </remarks>
public sealed class ServiceTokenCache
{
    // After a failed renewal inside the window the token in hand is still good; asking
    // again on every call would put a stalled endpoint on the path of every request.
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);

    internal sealed record Entry(string Token, DateTimeOffset ExpiresAt, DateTimeOffset RefreshAt);

    private sealed class Slot
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public volatile Entry? Current;
        public DateTimeOffset RetryAfter;
    }

    /// <summary>
    /// The token for <paramref name="key"/>, fetched through <paramref name="fetch"/> when
    /// there is none, when it has expired, or when it is inside its renewal window.
    /// </summary>
    /// <remarks>
    /// Single-flight per key: callers that arrive while a fetch is under way wait for it
    /// and take its result. Inside the window a failed fetch is not an error as long as
    /// the old token is valid; after expiry it always is.
    /// </remarks>
    internal async Task<Entry> GetAsync(
        string key,
        TimeProvider time,
        Func<CancellationToken, Task<Entry>> fetch,
        Action<Exception> onRenewalFailed,
        CancellationToken cancellationToken)
    {
        var slot = _slots.GetOrAdd(key, _ => new Slot());

        var current = slot.Current;
        if (Usable(current, slot, time.GetUtcNow()))
        {
            return current!;
        }

        await slot.Gate.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            current = slot.Current;
            if (Usable(current, slot, now))
            {
                return current!;
            }

            try
            {
                var fresh = await fetch(cancellationToken);
                slot.Current = fresh;
                slot.RetryAfter = default;
                return fresh;
            }
            catch (ServiceTokenUnavailableException exception)
                when (current is not null && time.GetUtcNow() < current.ExpiresAt)
            {
                slot.RetryAfter = now + RetryAfterFailure;
                onRenewalFailed(exception);
                return current;
            }
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>Drops the token for <paramref name="key"/>, so the next call fetches.</summary>
    internal void Remove(string key)
    {
        if (_slots.TryGetValue(key, out var slot))
        {
            slot.Current = null;
            slot.RetryAfter = default;
        }
    }

    internal Entry? Peek(string key) => _slots.TryGetValue(key, out var slot) ? slot.Current : null;

    private static bool Usable(Entry? entry, Slot slot, DateTimeOffset now)
    {
        if (entry is null || now >= entry.ExpiresAt)
        {
            return false;
        }

        // Inside the window the token is served only while a renewal is being held off.
        return now < entry.RefreshAt || now < slot.RetryAfter;
    }
}
