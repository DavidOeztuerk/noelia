using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.Tokens;
using Noelia.Infrastructure.Observability;

namespace Noelia.Infrastructure.Security.ServiceTokens;

/// <summary>
/// What the receiving side writes down about each request to a door.
/// </summary>
/// <remarks>
/// Outwardly every refusal is the same 404; this is where they stop being the
/// same. A mistyped audience or a rotated secret looks, from the caller's side,
/// like a path that does not exist, and without a record here it would stay that
/// way for the operator too.
/// <para>
/// A reason is one of a closed set of classes. Nothing the caller sent is
/// written: not the token, not any part of it, not a claim from a token that did
/// not verify. The caller's id appears only when the signature held.
/// </para>
/// </remarks>
internal sealed class ServiceCallerAudit(ILoggerFactory loggerFactory)
{
    internal const string NoToken = "no_token";
    internal const string InvalidToken = "invalid_token";
    internal const string Expired = "expired";
    internal const string WrongType = "wrong_type";
    internal const string WrongAudience = "wrong_audience";
    internal const string CallerNotAllowed = "caller_not_allowed";
    internal const string OtherPolicy = "other_policy";

    /// <summary>Where the scheme leaves the reason a token failed, for this request only.</summary>
    internal const string FailureKey = "Noelia.ServiceCaller.Failure";

    private static readonly Meter Meter = new(TelemetryConstants.MeterName);

    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "noelia.m2m.requests",
        "requests",
        "Requests to doors that only named services may open, by outcome and reason");

    private readonly ILogger _logger = loggerFactory.CreateLogger(typeof(ServiceCallerAudit).FullName!);

    /// <summary>
    /// The class of a token the scheme refused. Only the kind of failure is kept; the
    /// exception text can quote the token.
    /// </summary>
    internal static string Classify(Exception? exception) => exception switch
    {
        SecurityTokenExpiredException => Expired,
        SecurityTokenInvalidAudienceException => WrongAudience,
        SecurityTokenInvalidTypeException => WrongType,
        _ => InvalidToken
    };

    /// <summary>
    /// Why a request with no service identity was refused: the scheme's own record if it
    /// failed, otherwise whether the caller sent anything that looked like a credential.
    /// </summary>
    /// <remarks>
    /// With no <c>Authorization</c> header the bearer handler never reaches validation,
    /// so it leaves no record; that silence is <c>no_token</c>. A header that is there
    /// but yielded no token (another scheme, a doubled header) is an attempt, and counts
    /// as <c>invalid_token</c>.
    /// </remarks>
    internal static string ReasonWithoutIdentity(HttpContext context)
    {
        if (context.Items.TryGetValue(FailureKey, out var recorded) && recorded is string reason)
        {
            return reason;
        }

        return StringValues.IsNullOrEmpty(context.Request.Headers.Authorization) ? NoToken : InvalidToken;
    }

    internal void Allowed(HttpContext context, string caller)
    {
        var door = DoorOf(context);
        _logger.LogInformation("Service door {Door} opened for caller {Caller}", door, caller);
        Requests.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "allowed"),
            new KeyValuePair<string, object?>("reason", "allowed"),
            new KeyValuePair<string, object?>("door", door),
            new KeyValuePair<string, object?>("caller", caller));
    }

    internal void Denied(HttpContext context, string reason, string? caller)
    {
        var door = DoorOf(context);
        if (caller is null)
        {
            _logger.LogWarning("Service door {Door} refused: {Reason}", door, reason);
            Requests.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "denied"),
                new KeyValuePair<string, object?>("reason", reason),
                new KeyValuePair<string, object?>("door", door));
            return;
        }

        _logger.LogWarning("Service door {Door} refused: {Reason}, caller {Caller}", door, reason, caller);
        Requests.Add(
            1,
            new KeyValuePair<string, object?>("outcome", "denied"),
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("door", door),
            new KeyValuePair<string, object?>("caller", caller));
    }

    /// <summary>The route pattern, never the path: the path is what the caller typed.</summary>
    private static string DoorOf(HttpContext context) =>
        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unknown";

    /// <summary>Hooked into the scheme: records the class of the failure, nothing else.</summary>
    internal static Task RecordFailure(AuthenticationFailedContext context)
    {
        context.HttpContext.Items[FailureKey] = Classify(context.Exception);
        return Task.CompletedTask;
    }
}
