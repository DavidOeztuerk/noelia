namespace UserService.Contracts;

public sealed record RegisterRequest(string DisplayName, string Email, string Password);

public sealed record SignInRequest(string Email, string Password);

/// <summary>
/// What the browser needs in order to act as the signed-in person.
/// </summary>
/// <remarks>
/// The refresh token is deliberately absent: it travels as an HttpOnly cookie,
/// where script cannot read it. Handing it to JavaScript would turn one
/// cross-site scripting bug into days of access instead of minutes.
/// </remarks>
public sealed record SessionResponse(
    Guid UserId,
    string DisplayName,
    string AccessToken,
    DateTimeOffset ExpiresAt);

public sealed record UserResponse(Guid Id, string DisplayName, string Email, DateTimeOffset CreatedAt);

/// <summary>One sign-in, as a person sees it in their own list.</summary>
public sealed record SessionSummaryResponse(
    Guid SessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset LastUsedAt,
    DateTimeOffset ExpiresAt);
