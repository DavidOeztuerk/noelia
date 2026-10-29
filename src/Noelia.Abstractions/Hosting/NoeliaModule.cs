namespace Noelia.Abstractions.Hosting;

/// <summary>
/// The name of one thing Noelia can set up.
/// </summary>
/// <remarks>
/// A name and not an enumeration, so a package Noelia has never heard of can
/// declare a module of its own and hand it to <c>Use</c>. That is the property
/// that lets a provider extend the composition from outside, the way a database
/// provider extends Entity Framework's options builder.
/// </remarks>
/// <param name="Name">
/// Unique, and readable: it appears in the startup report and in the message a
/// missing provider produces. Prefix a package's own modules
/// (<c>Contoso.Billing</c>) so two packages cannot collide.
/// </param>
public readonly record struct NoeliaModule(string Name)
{
    /// <summary>The composition contract itself.</summary>
    public static NoeliaModule Composition => new(nameof(Composition));

    /// <summary>Serilog, configured from the service's own configuration.</summary>
    public static NoeliaModule Logging => new(nameof(Logging));

    /// <summary>Swagger, in development only.</summary>
    public static NoeliaModule ApiDocumentation => new(nameof(ApiDocumentation));

    /// <summary>Cross-origin rules, from configured origins.</summary>
    public static NoeliaModule Cors => new(nameof(Cors));

    /// <summary>camelCase in and out, indented while developing.</summary>
    public static NoeliaModule JsonOptions => new(nameof(JsonOptions));

    /// <summary>The ambient <c>HttpContext</c>, which several modules read.</summary>
    public static NoeliaModule HttpContextAccess => new(nameof(HttpContextAccess));

    /// <summary>Issuing and reading tokens, and the one-time-code service.</summary>
    public static NoeliaModule Jwt => new(nameof(Jwt));

    /// <summary>Failed sign-ins, lockouts, and what raises an alarm.</summary>
    public static NoeliaModule SecurityMonitoring => new(nameof(SecurityMonitoring));

    /// <summary>Circuit breakers and retry policies for outgoing calls.</summary>
    public static NoeliaModule Resilience => new(nameof(Resilience));

    /// <summary>Reading secrets, and rotating them.</summary>
    public static NoeliaModule SecretManagement => new(nameof(SecretManagement));

    /// <summary>The security audit trail.</summary>
    public static NoeliaModule Audit => new(nameof(Audit));

    /// <summary>Encrypting values at rest.</summary>
    public static NoeliaModule Encryption => new(nameof(Encryption));

    /// <summary>Rejecting or cleaning hostile input.</summary>
    public static NoeliaModule InputSanitization => new(nameof(InputSanitization));

    /// <summary>Counting requests, and refusing the ones over the line.</summary>
    public static NoeliaModule RateLimiting => new(nameof(RateLimiting));

    /// <summary>Liveness and readiness, and what they check.</summary>
    public static NoeliaModule HealthChecks => new(nameof(HealthChecks));

    /// <summary>Calling other services, with caching and deduplication.</summary>
    public static NoeliaModule Communication => new(nameof(Communication));

    /// <summary>The in-process caches other modules build on.</summary>
    public static NoeliaModule Caching => new(nameof(Caching));

    /// <summary>
    /// ETags and conditional responses. Needs a distributed cache: an ETag one
    /// instance issues has to be recognised by the next.
    /// </summary>
    public static NoeliaModule HttpResponseCaching => new(nameof(HttpResponseCaching));

    /// <summary>Traces, metrics and the telemetry pipeline.</summary>
    public static NoeliaModule Observability => new(nameof(Observability));

    /// <summary>The response headers a browser is told to enforce.</summary>
    public static NoeliaModule SecurityHeaders => new(nameof(SecurityHeaders));

    /// <summary>
    /// Permission policies: what answers <c>[RequirePermission]</c>.
    /// </summary>
    /// <remarks>
    /// Without it the framework rejects every call to an endpoint carrying that
    /// attribute as "policy not found" — fail-closed, and on exactly the
    /// endpoints the attribute was meant to protect.
    /// </remarks>
    public static NoeliaModule Authorization => new(nameof(Authorization));

    /// <summary>
    /// The pipeline step that refuses a request no permission covers.
    /// </summary>
    /// <remarks>
    /// It registers nothing, and that is the whole point of it existing.
    /// <see cref="Authorization"/> supplies the policy provider that answers
    /// <c>[RequirePermission]</c>; this names the middleware that acts on it. The
    /// two used to be one, so a service with a public surface — a sign-in page, a
    /// health probe, a door behind a shared secret rather than a token — had to
    /// choose between an authorization system that answers nothing and a pipeline
    /// that is fail-closed on every path it was not told about.
    /// <para>
    /// Left out, <c>[RequirePermission]</c> still decides on the endpoints that
    /// carry it; what goes away is the blanket refusal in front of everything
    /// else.
    /// </para>
    /// </remarks>
    public static NoeliaModule PermissionEnforcement => new(nameof(PermissionEnforcement));

    /// <summary>
    /// Resource and ownership policies — <c>ResourceRead</c>, <c>ResourceOwner</c>
    /// and the rest. Needs an <c>IResourceAuthorizationService</c>.
    /// </summary>
    public static NoeliaModule ResourceAuthorization => new(nameof(ResourceAuthorization));

    /// <summary>Carrying the correlation id out on every HTTP call.</summary>
    public static NoeliaModule CorrelationPropagation => new(nameof(CorrelationPropagation));

    /// <summary>Hashing and verifying passwords. Needs a provider package.</summary>
    public static NoeliaModule PasswordHashing => new(nameof(PasswordHashing));

    /// <summary>Refresh tokens and sessions. Needs a store.</summary>
    public static NoeliaModule TokenSessions => new(nameof(TokenSessions));

    /// <summary>The current principal, read from the request.</summary>
    public static NoeliaModule Principal => new(nameof(Principal));

    /// <summary>
    /// The sovereign bundle: a declared egress boundary, the sovereignty report,
    /// and a tamper-evident audit trail.
    /// </summary>
    /// <remarks>
    /// A module like any other, so it appears in <c>NoeliaComposition</c> and a
    /// service that deliberately calls outward can drop it with a reason.
    /// </remarks>
    public static NoeliaModule SovereignPlatform => new(nameof(SovereignPlatform));

    /// <summary>The read-only, operator-authorised Noelia dashboard.</summary>
    public static NoeliaModule Dashboard => new(nameof(Dashboard));

    /// <summary>
    /// Issuing short-lived tokens to other services that present a registered
    /// client secret. Bound by exactly one service, the one that signs people in.
    /// </summary>
    /// <remarks>
    /// Needs the signing half of an asymmetric key pair: with a shared secret
    /// every verifying service could mint service tokens for any caller.
    /// </remarks>
    public static NoeliaModule ServiceTokenIssuing => new(nameof(ServiceTokenIssuing));

    /// <summary>
    /// Letting an endpoint answer only the named calling services, and pretending
    /// not to exist for everyone else.
    /// </summary>
    public static NoeliaModule ServiceCallers => new(nameof(ServiceCallers));

    /// <summary>
    /// Fetching service tokens for outgoing calls with the client credentials
    /// this service was given.
    /// </summary>
    public static NoeliaModule ServiceTokenClient => new(nameof(ServiceTokenClient));

    /// <inheritdoc />
    public override string ToString() => Name;
}
