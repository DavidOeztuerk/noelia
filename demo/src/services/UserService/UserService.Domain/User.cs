using Noelia.Core.Identity;

namespace UserService.Domain;

public sealed class User
{
    private User(SubjectId id, EmailAddress email, string displayName, string passwordHash, DateTimeOffset createdAt)
    {
        Id = id;
        Email = email;
        DisplayName = displayName;
        PasswordHash = passwordHash;
        CreatedAt = createdAt;
    }

    public SubjectId Id { get; }
    public EmailAddress Email { get; }
    public string DisplayName { get; }

    /// <summary>
    /// The stored verifier, never the password. Carries its own algorithm and
    /// parameters, so a later change of cost does not invalidate old entries.
    /// </summary>
    public string PasswordHash { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <param name="email">Already parsed, so an invalid address never reaches here.</param>
    /// <param name="displayName">What other people see.</param>
    /// <param name="passwordHash">Produced by the hasher; this type never sees the password.</param>
    /// <param name="now">Injected rather than read, so the clock stays testable.</param>
    public static User Register(
        EmailAddress email,
        string displayName,
        string passwordHash,
        DateTimeOffset now)
    {
        var normalisedName = displayName.Trim();
        if (normalisedName.Length is < 2 or > 80)
        {
            throw new ArgumentException("Display name must contain between 2 and 80 characters.", nameof(displayName));
        }

        return new User(SubjectId.New(), email, normalisedName, passwordHash, now);
    }

    /// <summary>
    /// The same person with a re-derived password entry.
    /// </summary>
    /// <remarks>
    /// Written after a successful sign-in whose stored entry used weaker
    /// parameters than are configured now — the one moment the password is
    /// available to derive from. Without this the cost can be raised and never
    /// reaches anyone who registered before.
    /// </remarks>
    public User WithPasswordHash(string passwordHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        return new User(Id, Email, DisplayName, passwordHash, CreatedAt);
    }
}
