using Microsoft.IdentityModel.Tokens;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// Makes the person path refuse a service token.
/// </summary>
/// <remarks>
/// A service token is signed by the same key, names the same issuer and can be
/// minted for any audience the issuer allows — so the only thing separating it
/// from a person's token is the <c>typ</c> header, and it has to be checked on
/// every path that reads a person's token. A list of the types allowed would be
/// tighter and would sign out every existing person whose issuer chose another
/// <c>typ</c>; this refuses the one type that did not exist before and leaves
/// everything else as it was.
/// </remarks>
internal static class ServiceTokenTypeGuard
{
    private const string ApplicationPrefix = "application/";

    /// <summary>
    /// Adds the refusal to the parameters and keeps whatever type rule they
    /// already had, so a configured validator or <c>ValidTypes</c> still decides.
    /// </summary>
    internal static TokenValidationParameters Apply(TokenValidationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var previous = parameters.TypeValidator;

        parameters.TypeValidator = (typ, token, validation) =>
        {
            if (IsServiceTokenType(typ))
            {
                throw new SecurityTokenInvalidTypeException(
                    "A service token is not accepted where a person's token is expected.")
                {
                    InvalidType = typ
                };
            }

            return previous is not null
                ? previous(typ, token, validation)
                : DefaultRule(typ, validation);
        };

        return parameters;
    }

    /// <summary>
    /// What the token library does when no validator is set: with no <c>ValidTypes</c> every
    /// type passes, otherwise the type has to be one of them.
    /// </summary>
    /// <remarks>
    /// Written out rather than delegated to <c>Validators.ValidateTokenType</c>, which calls
    /// the validator set on the parameters — this one — and so never returns.
    /// </remarks>
    private static string DefaultRule(string? typ, TokenValidationParameters validation)
    {
        var valid = validation.ValidTypes;
        if (valid is null || !valid.Any())
        {
            return typ!;
        }

        if (string.IsNullOrEmpty(typ) || !valid.Contains(typ, StringComparer.Ordinal))
        {
            throw new SecurityTokenInvalidTypeException(
                "The token type is not one of the types this service accepts.")
            {
                InvalidType = typ
            };
        }

        return typ;
    }

    /// <summary>
    /// Media types compare without regard to case, and RFC 7515 lets the
    /// <c>application/</c> prefix be left off; both spellings are the same type.
    /// </summary>
    private static bool IsServiceTokenType(string? typ)
    {
        if (string.IsNullOrEmpty(typ))
        {
            return false;
        }

        var bare = typ.StartsWith(ApplicationPrefix, StringComparison.OrdinalIgnoreCase)
            ? typ[ApplicationPrefix.Length..]
            : typ;

        return string.Equals(bare, ServiceTokenDefaults.TokenType, StringComparison.OrdinalIgnoreCase);
    }
}
