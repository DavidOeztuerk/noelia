namespace UserService.Api;

/// <summary>
/// The refresh token as an HttpOnly cookie.
/// </summary>
/// <remarks>
/// Not in <c>sessionStorage</c>, where the access token lives: script can read
/// that, and a refresh token is worth days rather than minutes. As an HttpOnly
/// cookie a cross-site scripting bug cannot exfiltrate it.
/// <para>
/// The path narrows it to the two endpoints that read it, so it is not attached
/// to every API call it has no business in.
/// </para>
/// </remarks>
public static class RefreshCookie
{
    public const string Name = "noelia.rt";

    private const string Path = "/api/auth";

    public static void Set(HttpContext http, string token, DateTimeOffset expiresAt) =>
        http.Response.Cookies.Append(Name, token, Options(http, expiresAt));

    public static void Clear(HttpContext http) =>
        http.Response.Cookies.Append(Name, string.Empty, Options(http, DateTimeOffset.UnixEpoch));

    private static CookieOptions Options(HttpContext http, DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,

        // Only over HTTPS, where there is HTTPS. This demo serves plain HTTP,
        // and a Secure cookie would simply never be sent — the flag has to
        // follow the transport rather than be asserted.
        Secure = http.Request.IsHttps,

        // The refresh endpoint changes state and needs no cross-site use, so
        // the cookie is not sent on any cross-site request at all.
        SameSite = SameSiteMode.Strict,
        Path = Path,
        Expires = expiresAt
    };
}
