using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Noelia.Abstractions.Security;
using Noelia.Abstractions.Security.Sessions;
using Noelia.Core.Identity;

namespace Demo.Platform;

/// <summary>
/// What signing out means in this demo, written once for the services that sign
/// people in: end every session of the person, then refuse every access token
/// they were carrying.
/// </summary>
/// <remarks>
/// Four endpoints used to do slightly different things and one of them read the
/// session out of whatever claim the caller's token carried. The order of the
/// two steps is the whole point, which is why it lives here and not in four
/// lambdas: the sessions are closed first and the cutoff is read afterwards. A
/// cutoff taken first would let a refresh that lands between the two mint an
/// access token newer than the cutoff, and a close that fails after the cutoff
/// would leave refresh chains open for tokens that are already refused.
/// </remarks>
public static class SignOut
{
    /// <summary>
    /// Ends every session of the person behind the request and writes their
    /// cutoff.
    /// </summary>
    /// <remarks>
    /// The person is the principal's subject and nothing a caller sent. If
    /// closing throws, the cutoff is still written, because refused access tokens
    /// are better than none, and the answer is an error so the caller repeats it;
    /// signing out is idempotent. A failing revocation write is an error too. Neither
    /// answer carries exception text.
    /// </remarks>
    public static async Task<IResult> EverywhereAsync(
        ICurrentPrincipal principal,
        ITokenSessionService sessions,
        ITokenRevocationWriter revocations,
        TimeProvider clock,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var person = principal.Require().Subject;
        var failed = false;

        try
        {
            await sessions.SignOutEverywhereAsync(person, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failed = true;
            logger.LogError(exception, "Closing the sessions of {Person} failed; writing the cutoff anyway", person);
        }

        try
        {
            // After the close, never before: see the remarks on the type.
            await revocations.RevokeSubjectBeforeAsync(
                person.ToString(), clock.GetUtcNow(), "signed out", cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failed = true;
            logger.LogError(exception, "Writing the sign-out cutoff of {Person} failed", person);
        }

        return failed
            ? Results.Problem(
                title: "Das Abmelden konnte nicht vollständig durchgeführt werden.",
                detail: "Bitte versuche es erneut.",
                statusCode: StatusCodes.Status500InternalServerError)
            : Results.NoContent();
    }
}
