using System.Security.Claims;
using Noelia.Infrastructure.Security.Authorization;

namespace Noelia.Infrastructure.Tests.Authorization;

/// <summary>
/// The principals of ADR-0007 (#59): a signed-in person without the right, and an
/// identity that carries it without ever having been signed in (no
/// <c>AuthenticationType</c>), such as one a claims transformation appended.
/// </summary>
internal static class AuthenticatedIdentityFixtures
{
    public const string Required = "orders:delete";
    public const string Resource = "42";

    /// <summary>The rights a test hands to the unauthenticated identity, one kind per row.</summary>
    public static readonly string[] Kinds = ["permission", "role", "wildcard", "category", "resource"];

    /// <summary>Catalogue in which the role "Clerk" carries the required permission.</summary>
    public static readonly IPermissionCatalog Catalog = new PermissionCatalogBuilder()
        .Role("Clerk", Required)
        .Build();

    /// <summary>A signed-in person who holds nothing.</summary>
    public static ClaimsIdentity Person(params Claim[] extra) =>
        new([new Claim(ClaimTypes.NameIdentifier, "alice"), .. extra], "Bearer");

    /// <summary>An identity without <c>AuthenticationType</c>: it is present, but nothing signed it in.</summary>
    public static ClaimsIdentity Unauthenticated(params Claim[] claims) => new(claims);

    /// <summary>The claims that would satisfy the requirement, by kind.</summary>
    public static Claim[] Granting(string kind) => kind switch
    {
        "permission" => [new Claim("permission", Required)],
        "role" => [new Claim(ClaimTypes.Role, "Clerk")],
        "wildcard" => [new Claim("permission", PermissionCatalog.Wildcard)],
        "category" => [new Claim("permission", "orders:*")],
        "resource" => [new Claim("permission", $"{Required}:{Resource}")],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>The unauthenticated identity carries a foreign id as well, so that log and alert reads can be checked.</summary>
    public static ClaimsIdentity Mallory(string kind) =>
        Unauthenticated([new Claim(ClaimTypes.NameIdentifier, "mallory"), .. Granting(kind)]);

    public static ClaimsPrincipal Of(bool unauthenticatedFirst, ClaimsIdentity person, ClaimsIdentity other) =>
        unauthenticatedFirst ? new ClaimsPrincipal([other, person]) : new ClaimsPrincipal([person, other]);
}
