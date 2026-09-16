namespace UserService.Domain;

/// <summary>
/// An email address, normalised so that one person cannot hold two accounts by
/// changing the capitalisation.
/// </summary>
public readonly record struct EmailAddress
{
    private EmailAddress(string value) => Value = value;

    public string Value { get; }

    /// <summary>Parses, or reports that this is not an address.</summary>
    /// <remarks>
    /// Lets a caller distinguish "unusable input" from "unknown account"
    /// without an exception, which matters where both must take the same path.
    /// </remarks>
    public static bool TryParse(string? input, out EmailAddress address)
    {
        try
        {
            address = Parse(input);
            return true;
        }
        catch (ArgumentException)
        {
            address = default;
            return false;
        }
    }

    /// <exception cref="ArgumentException">
    /// The input is null, empty or not an address. Null is included on purpose:
    /// a field a client omitted arrives as null, and that is bad input rather
    /// than a fault in this service.
    /// </exception>
    public static EmailAddress Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("That is not an email address.", nameof(input));
        }

        var normalised = input.Trim().ToLowerInvariant();

        // Deliberately not a full RFC 5322 grammar: the only address that
        // matters is one that can receive mail, and nothing here can tell.
        var at = normalised.IndexOf('@');
        if (at <= 0 || at == normalised.Length - 1 || normalised.Contains(' ')
            || normalised.IndexOf('@', at + 1) >= 0 || !normalised[(at + 1)..].Contains('.'))
        {
            throw new ArgumentException("That is not an email address.", nameof(input));
        }

        return new EmailAddress(normalised);
    }

    public override string ToString() => Value;
}
