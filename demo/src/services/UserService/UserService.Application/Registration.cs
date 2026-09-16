using Noelia.Abstractions.Security.Passwords;
using Noelia.Application.Extensions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UserService.Domain;

namespace UserService.Application;

public sealed record RegisterUserCommand(string DisplayName, string Email, string Password)
    : IRequest<RegistrationResult>;

/// <summary>
/// Either the new account or the reason there is none.
/// </summary>
/// <remarks>
/// A result type rather than an exception: "this address is taken" is an
/// expected answer, not a fault.
/// </remarks>
public abstract record RegistrationResult
{
    public sealed record Registered(User User) : RegistrationResult;

    public sealed record EmailTaken : RegistrationResult;

    public sealed record Rejected(string Field, string Message) : RegistrationResult;
}

public sealed class RegisterUserCommandHandler(
    IUserRepository users,
    IPasswordHasher passwords,
    TimeProvider clock,
    ILogger<RegisterUserCommandHandler> logger)
    : IRequestHandler<RegisterUserCommand, RegistrationResult>
{
    public async Task<RegistrationResult> Handle(RegisterUserCommand command, CancellationToken cancellationToken)
    {
        EmailAddress email;
        try
        {
            email = EmailAddress.Parse(command.Email);
        }
        catch (ArgumentException error)
        {
            return new RegistrationResult.Rejected("email", error.Message);
        }

        if (command.Password is not { Length: >= 12 })
        {
            return new RegistrationResult.Rejected("password", "Use at least 12 characters.");
        }

        if (await users.FindByEmailAsync(email, cancellationToken) is not null)
        {
            return new RegistrationResult.EmailTaken();
        }

        User user;
        try
        {
            user = User.Register(
                email, command.DisplayName ?? string.Empty,
                passwords.Hash(command.Password), clock.GetUtcNow());
        }
        catch (ArgumentException error)
        {
            return new RegistrationResult.Rejected("displayName", error.Message);
        }

        await users.AddAsync(user, cancellationToken);
        logger.LogInformation("Registered user {UserId}", user.Id);

        return new RegistrationResult.Registered(user);
    }
}

public static class UserApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddUserApplication(this IServiceCollection services)
    {
        services.AddCQRS([typeof(UserApplicationServiceCollectionExtensions).Assembly]);
        return services;
    }
}
