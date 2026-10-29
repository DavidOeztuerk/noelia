using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Noelia.Abstractions.Hosting;
using Noelia.Abstractions.Security.Checks;
using Noelia.Infrastructure.Security.ServiceTokens;

namespace Noelia.Infrastructure.Security.Checks;

/// <summary>
/// Proves that the policy a service door promises is the policy that runs.
/// </summary>
/// <remarks>
/// <c>Category == Composition</c>, so it runs whether or not the modules are composed: a
/// door mapped in a service that never called <c>UseServiceCallers</c> is exactly the
/// case it exists for. With nothing in use it answers NotApplicable, as every other
/// Composition check does when it has nothing to examine: a Pass would claim a property
/// it never looked at. A service that uses none of this sees one more line, not a finding.
/// <para>
/// Summaries name route patterns and module calls, never caller ids, policy names or
/// any configured value.
/// </para>
/// </remarks>
internal sealed class ServiceCallerPolicySecurityCheck(
    IServiceProvider services,
    NoeliaComposition composition) : SecurityCheckBase
{
    public override string Id => "noelia.m2m.caller-policy";
    public override NoeliaModule Module => NoeliaModule.Composition;
    public override SecurityCheckCategory Category => SecurityCheckCategory.Composition;
    public override SecurityCheckSeverity Severity => SecurityCheckSeverity.High;
    public override string Remediation =>
        "Compose UseServiceCallers(audience) and register nothing after it that replaces "
        + "IAuthorizationMiddlewareResultHandler; map MapServiceFallback() on every group of "
        + "doors and put rate limiting, output caching and CORS on the group, never on one door; "
        + "do not combine RequireServiceCaller or [ServiceCaller] with AllowAnonymous; map exactly "
        + "one MapServiceTokenEndpoint() with RequireRateLimiting(...) where tokens are issued.";

    public override Task<SecurityCheckResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var endpoints = services.GetService<EndpointDataSource>()?.Endpoints ?? [];

        var doors = endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<ServiceCallerAttribute>() is not null).ToArray();
        var fallbacks = endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<ServiceFallbackMarker>() is not null).ToArray();
        var tokenEndpoints = endpoints.Where(endpoint => endpoint.Metadata.GetMetadata<ServiceTokenEndpointMarker>() is not null).ToArray();

        var callersComposed = composition.Included.Contains(NoeliaModule.ServiceCallers);
        var issuerComposed = composition.Included.Contains(NoeliaModule.ServiceTokenIssuing);

        if (!callersComposed && !issuerComposed && doors.Length == 0)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.NotApplicable,
                "Service callers are not in use: no module composed and no service door mapped."));
        }

        var failures = new List<string>();
        var warnings = new List<string>();

        // 1. Anonymous access beats the policy: the authorization middleware skips an
        //    endpoint that allows it, so the door would open for everyone, unaudited.
        foreach (var endpoint in doors.Concat(fallbacks))
        {
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                failures.Add($"{Pattern(endpoint)} allows anonymous access, which switches its caller policy off");
            }
        }

        // 2. The resolved handler, not the registered one: a later registration wins.
        if (callersComposed || doors.Length > 0)
        {
            using var scope = services.CreateScope();
            var handler = scope.ServiceProvider.GetService<IAuthorizationMiddlewareResultHandler>();
            if (handler is not ServiceCallerAuthorizationResultHandler)
            {
                failures.Add(
                    "The resolved IAuthorizationMiddlewareResultHandler is not the service-caller decorator "
                    + (callersComposed
                        ? "(another registration replaced it), so refusals are not the uniform 404"
                        : "(UseServiceCallers is not composed), so a refused caller gets the framework's answer"));
            }
        }

        // 3. Everything endpoint-driven that a door carries and its fallback does not would
        //    answer the door differently from a path that is not there.
        foreach (var door in doors)
        {
            var fallback = FallbackOf(door, fallbacks);
            if (fallback is null)
            {
                warnings.Add($"{Pattern(door)} has no MapServiceFallback() over it");
                continue;
            }

            var missing = Behaviours(door).Except(Behaviours(fallback)).Select(Describe).Distinct().ToArray();
            if (missing.Length > 0)
            {
                failures.Add(
                    $"{Pattern(door)} carries {string.Join(", ", missing)} that its fallback "
                    + $"{Pattern(fallback)} does not; move it to the group");
            }
        }

        // 4. The token endpoint, where this service issues.
        if (issuerComposed)
        {
            if (tokenEndpoints.Length != 1)
            {
                failures.Add(
                    $"The issuer is composed but {tokenEndpoints.Length} token endpoints are mapped; "
                    + "exactly one MapServiceTokenEndpoint() is expected");
            }
            else if (!Behaviours(tokenEndpoints[0]).Any(b => b.StartsWith("rate-limit:", StringComparison.Ordinal)
                                                             && b != "rate-limit:disabled"))
            {
                warnings.Add($"The token endpoint {Pattern(tokenEndpoints[0])} has no rate limit");
            }
        }

        if (failures.Count > 0)
        {
            return Task.FromResult(Result(SecurityCheckStatus.Fail, string.Join("; ", failures.Concat(warnings)) + "."));
        }

        if (warnings.Count > 0)
        {
            return Task.FromResult(Result(
                SecurityCheckStatus.Warning, string.Join("; ", warnings) + ".", SecurityCheckSeverity.Medium));
        }

        return Task.FromResult(Result(
            SecurityCheckStatus.Pass,
            $"{doors.Length} service door(s) are covered by a fallback, and the refusal handler is in place."));
    }

    private static string Pattern(Endpoint endpoint) =>
        "/" + ((endpoint as RouteEndpoint)?.RoutePattern.RawText ?? "unknown").TrimStart('/');

    /// <summary>
    /// The fallback with the longest prefix that is a path-segment prefix of the door.
    /// </summary>
    /// <remarks>
    /// Read off the final route patterns rather than stored by <c>RequireServiceCaller</c>:
    /// the pattern is what the router matches on, so it cannot disagree with it, and the
    /// door does not have to know which fallback it belongs to.
    /// </remarks>
    private static Endpoint? FallbackOf(Endpoint door, IEnumerable<Endpoint> fallbacks)
    {
        var path = Pattern(door);
        return fallbacks
            .Select(fallback => (Fallback: fallback, Prefix: PrefixOf(fallback)))
            .Where(candidate => path.Equals(candidate.Prefix, StringComparison.Ordinal)
                                || path.StartsWith(candidate.Prefix + "/", StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Prefix.Length)
            .Select(candidate => candidate.Fallback)
            .FirstOrDefault();
    }

    private static string PrefixOf(Endpoint fallback)
    {
        var raw = (fallback as RouteEndpoint)?.RoutePattern.RawText ?? string.Empty;
        var catchAll = raw.LastIndexOf("{**", StringComparison.Ordinal);
        var prefix = catchAll < 0 ? raw : raw[..catchAll];
        return "/" + prefix.Trim('/');
    }

    /// <summary>
    /// The endpoint-driven behaviours a request meets before or beside the handler, as
    /// type and policy name. Names are compared, never shown.
    /// </summary>
    private static IEnumerable<string> Behaviours(Endpoint endpoint) =>
        endpoint.Metadata.Select(item => item switch
        {
            EnableRateLimitingAttribute attribute => $"rate-limit:{attribute.PolicyName}",
            DisableRateLimitingAttribute => "rate-limit:disabled",
            OutputCacheAttribute attribute => $"output-cache:{attribute.PolicyName}",
            IOutputCachePolicy policy => $"output-cache:{policy.GetType().FullName}",
            IEnableCorsAttribute attribute => $"cors:{attribute.PolicyName}",
            IDisableCorsAttribute => "cors:disabled",
            ICorsPolicyMetadata policy => $"cors:{policy.GetType().FullName}",
            _ => null
        }).Where(signature => signature is not null).Select(signature => signature!).Distinct();

    private static string Describe(string behaviour) => behaviour.Split(':')[0] switch
    {
        "rate-limit" => "rate limiting",
        "output-cache" => "output caching",
        _ => "CORS"
    };
}
