namespace Demo.Monolith.Api;

/// <summary>Keeps the long-lived refresh token outside JavaScript.</summary>
public static class MonolithRefreshCookie
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
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Strict,
        Path = Path,
        Expires = expiresAt
    };
}
