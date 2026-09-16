using Noelia.Abstractions.Security.Passwords;
using MediatR;
using Microsoft.Extensions.Logging;
using UserService.Domain;

namespace UserService.Application;

public sealed record SignInCommand(string Email, string Password) : IRequest<User?>;

/// <summary>
/// Answers with the person or with nothing.
/// </summary>
/// <remarks>
/// One answer for "no such address" and for "wrong password". Telling them
/// apart turns the sign-in form into a way to ask whether someone has an
/// account here — and that holds for how long the answer takes as well as for
/// what it says, which is why the hasher is handed a null entry rather than
/// being skipped.
/// </remarks>
public sealed class SignInCommandHandler(
    IUserRepository users,
    IPasswordHasher passwords,
    ILogger<SignInCommandHandler> logger)
    : IRequestHandler<SignInCommand, User?>
{
    public async Task<User?> Handle(SignInCommand command, CancellationToken cancellationToken)
    {
        var user = EmailAddress.TryParse(command.Email, out var email)
            ? await users.FindByEmailAsync(email, cancellationToken)
            : null;

        // Not `user is null || !Verify(...)`: `||` short-circuits, so hashing
        // never runs for an unknown address and the answer comes back in
        // microseconds instead of hundreds of milliseconds. Passing the null
        // entry in is what keeps both paths equally expensive.
        var verification = passwords.Verify(command.Password, user?.PasswordHash);

        if (user is null || verification == PasswordVerification.Failed)
        {
            // No address in the log either: a failed sign-in is where a typo of
            // someone else's address ends up.
            logger.LogInformation("Sign-in refused");
            return null;
        }

        if (verification == PasswordVerification.SuccessRehashNeeded)
        {
            // The only moment the password exists to derive from. Skip it and
            // a raised cost never reaches anyone who registered before.
            await users.SaveAsync(user.WithPasswordHash(passwords.Hash(command.Password)), cancellationToken);
            logger.LogInformation("Password entry rewritten at the current cost");
        }

        return user;
    }
}
