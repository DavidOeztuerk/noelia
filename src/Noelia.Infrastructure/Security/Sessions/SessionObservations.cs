using System.Collections.Concurrent;
using Noelia.Core.Identity;

namespace Noelia.Infrastructure.Security.Sessions;

/// <summary>
/// The subjects this process has issued or refreshed a session for.
/// </summary>
/// <remarks>
/// <para>A singleton, and that is the whole point of it existing. The set used
/// to be a field on <c>TokenSessionService</c>, which is registered scoped —
/// so every request built a fresh, empty dictionary, and the dashboard's
/// session panel read one that had never seen anything. It reported "0 active
/// sessions observed by this instance" in every deployment, forever, and the
/// wording made the emptiness sound like an answer.</para>
///
/// <para>Subjects, not sessions. The store knows which sessions a subject has
/// and can answer that safely; what it cannot do is enumerate every subject,
/// because a store shared by several services would then let one of them read
/// the others' inventory. This holds only what this process itself did.</para>
/// </remarks>
public sealed class SessionObservations
{
    private readonly ConcurrentDictionary<SubjectId, byte> _subjects = new();

    /// <summary>The subjects seen here, in a stable order.</summary>
    public IEnumerable<SubjectId> Subjects =>
        _subjects.Keys.OrderBy(subject => subject.ToString(), StringComparer.Ordinal);

    /// <summary>Notes that this process issued or refreshed for a subject.</summary>
    /// <param name="subject">Who signed in.</param>
    public void Add(SubjectId subject) => _subjects.TryAdd(subject, 0);

    /// <summary>Forgets a subject whose every session ended here.</summary>
    /// <param name="subject">Who signed out.</param>
    public void Remove(SubjectId subject) => _subjects.TryRemove(subject, out _);
}
