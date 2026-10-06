using Noelia.Abstractions.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Noelia.Core.Identity;
using Noelia.Infrastructure.Models;
using Noelia.Infrastructure.Security.Authorization;
using Noelia.Infrastructure.Security.Identity;
using System.Text.RegularExpressions;

using Noelia.Infrastructure.Security.Keys;

namespace Noelia.Infrastructure.Security;

public class JwtService : IJwtService
{
    private readonly JwtSettings _jwtSettings;
    private readonly KeyRing _keys;
    private readonly ILogger<JwtService> _logger;
    private readonly ITokenRevocationEvaluator? _revocationEvaluator;
    private readonly ITokenRevocationWriter? _revocationWriter;
    private readonly IPermissionCatalog _permissions;
    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);

    public JwtService(
        IOptions<JwtSettings> jwtSettings,
        KeyRing keys,
        ILogger<JwtService> logger,
        IPermissionCatalog? permissions = null,
        ITokenRevocationEvaluator? revocationEvaluator = null,
        ITokenRevocationWriter? revocationWriter = null)
    {
        _jwtSettings = jwtSettings.Value;
        _keys = keys;
        _logger = logger;
        _revocationEvaluator = revocationEvaluator;
        _revocationWriter = revocationWriter;
        _permissions = permissions ?? PermissionCatalog.Empty;
        ValidateJwtSettings();
    }

    private void ValidateJwtSettings()
    {
        if (string.IsNullOrWhiteSpace(_jwtSettings.Issuer))
        {
            throw new InvalidOperationException("JWT Issuer is required");
        }

        if (string.IsNullOrWhiteSpace(_jwtSettings.Audience))
        {
            throw new InvalidOperationException("JWT Audience is required");
        }

        // Allow negative values for testing expired tokens
        if (_jwtSettings.ExpireMinutes == 0)
        {
            throw new InvalidOperationException("JWT ExpireMinutes cannot be 0");
        }
    }

    public async Task<TokenResult> GenerateTokenAsync(UserClaims user)
    {
        ValidateUserClaims(user);

        // Merge role permissions with explicit user permissions
        user.Permissions = [.. _permissions
            .PermissionsFor(user.Roles)
            .Union(user.Permissions ?? Enumerable.Empty<string>())
            .Distinct()];

        var jti = Guid.NewGuid().ToString();
        var accessToken = await GenerateAccessTokenAsync(user, jti);
        var expiresAt = DateTime.UtcNow.AddMinutes(_jwtSettings.ExpireMinutes);

        _logger.LogInformation("Generated tokens for user {UserId} with roles {Roles} and JTI {Jti}",
            user.UserId, string.Join(", ", user.Roles), jti);

        return new TokenResult
        {
            AccessToken = accessToken,
            ExpiresAt = expiresAt,
            TokenType = "Bearer"
        };
    }

    private void ValidateUserClaims(UserClaims user)
    {
        if (string.IsNullOrWhiteSpace(user.UserId))
        {
            throw new ArgumentException("UserId is required", nameof(user));
        }

        if (string.IsNullOrWhiteSpace(user.Email) || !EmailRegex.IsMatch(user.Email))
        {
            throw new ArgumentException("Valid email is required", nameof(user));
        }

    }

    private async Task<string> GenerateAccessTokenAsync(UserClaims user, string jti)
    {
        var signingCredentials = _keys.RequireSigningKey().SigningCredentials();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.UserId),

            // Duplicates `sub`, and stays. Inbound claim mapping is off
            // (`MapInboundClaims = false`), so the framework does not derive
            // NameIdentifier from `sub` the way it would by default — and
            // seventeen readers resolve the caller through it, two of them in
            // provider packages that cannot see this assembly. Dropping it saves
            // about seventy bytes a token and turns every one of those into a
            // silent null.
            new(ClaimTypes.NameIdentifier, user.UserId),

            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, jti),
            new(JwtRegisteredClaimNames.Iat,
                new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64)
        };

        // Written only when stated. Both used to be written unconditionally off a
        // default nobody chose, which made two policies constants: EmailVerified
        // refused everyone, ActiveAccount admitted everyone — including suspended
        // and deleted accounts, at the one gate meant to stop them.
        //
        // Both handlers already refuse a claim that is absent, so saying nothing
        // now means no.
        if (user.EmailVerified is { } emailVerified)
        {
            claims.Add(new Claim(
                "email_verified",
                emailVerified.ToString(),
                ClaimValueTypes.Boolean));
        }

        if (!string.IsNullOrWhiteSpace(user.AccountStatus))
        {
            claims.Add(new Claim("account_status", user.AccountStatus));
        }

        // Add session claim for concurrent session control
        if (!string.IsNullOrEmpty(user.SessionId))
        {
            claims.Add(new(SessionClaims.Issued, user.SessionId));
        }

        // Emitted only when acting for a company. Its absence is what marks a
        // token as belonging to a person acting for themselves.
        if (user.Acting is Capacity.ForCompany company)
        {
            claims.Add(new(NoeliaClaimTypes.Tenant, company.Tenant.ToString()));
        }

        // Roles the caller holds, plus every role those inherit.
        var rolesToAdd = new HashSet<string>(user.Roles);

        foreach (var role in user.Roles)
        {
            foreach (var inherited in _permissions.RolesInheritedBy(role))
            {
                rolesToAdd.Add(inherited);
            }
        }

        foreach (var role in rolesToAdd)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        // Add permission claims
        foreach (var permission in user.Permissions)
        {
            claims.Add(new Claim("permission", permission));
        }

        // Add custom claims
        if (user.CustomClaims != null)
        {
            foreach (var customClaim in user.CustomClaims)
            {
                claims.Add(new Claim(customClaim.Key, customClaim.Value));
            }
        }

        // Repeated claims under one name are what a JWT serialises as an array —
        // the same way roles and permissions above have always travelled.
        if (user.CustomClaimArrays != null)
        {
            foreach (var (name, values) in user.CustomClaimArrays)
            {
                foreach (var value in values)
                {
                    claims.Add(new Claim(name, value));
                }
            }
        }

        var securityToken = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            expires: DateTime.UtcNow.AddMinutes(_jwtSettings.ExpireMinutes),
            claims: claims,
            signingCredentials: signingCredentials);

        return await Task.FromResult(new JwtSecurityTokenHandler().WriteToken(securityToken));
    }

    public async Task<ClaimsPrincipal?> GetPrincipalFromExpiredTokenAsync(string token)
    {
        var tokenValidationParameters = _keys.ValidationParameters(_jwtSettings.Issuer, _jwtSettings.Audience, validateLifetime: false);

        var tokenHandler = new JwtSecurityTokenHandler();

        try
        {
            var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out var securityToken);

            // The algorithm was already checked against the ring's allowed set
            // during validation; re-checking it here against one hard-wired value
            // is what made ES256 impossible.
            if (securityToken is not JwtSecurityToken)
            {
                _logger.LogWarning("Invalid token format");
                return null;
            }

            return await Task.FromResult(principal);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract principal from expired token");
            return null;
        }
    }

    public async Task<ClaimsPrincipal?> ValidateTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var tokenValidationParameters = _keys.ValidationParameters(_jwtSettings.Issuer, _jwtSettings.Audience);

        var tokenHandler = new JwtSecurityTokenHandler();

        try
        {
            var principal = tokenHandler.ValidateToken(token, tokenValidationParameters, out var securityToken);

            var revocation = _revocationEvaluator is null ? null : ReadTokenIdentity(principal);
            if (revocation is { } identity)
            {
                var verdict = await _revocationEvaluator!.EvaluateAsync(identity);
                if (verdict.IsRevoked)
                {
                    _logger.LogWarning(
                        "Token {Jti} refused: {Reason}", identity.TokenId, verdict.Reason);
                    return null;
                }
            }

            // Additional security checks
            // The algorithm was already checked against the ring's allowed set
            // during validation; re-checking it here against one hard-wired value
            // is what made ES256 impossible.
            if (securityToken is not JwtSecurityToken)
            {
                _logger.LogWarning("Invalid token format");
                return null;
            }

            return principal;
        }
        catch (SecurityTokenExpiredException)
        {
            _logger.LogDebug("Token has expired");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Token validation failed");
            return null;
        }
    }

    /// <summary>
    /// Withdraws one token before it expires.
    /// </summary>
    /// <remarks>
    /// Needs a revocation store. Without one this throws rather than returning
    /// quietly: the caller believes it has withdrawn a token, and the path that
    /// tells a person "signed out everywhere" must not complete when nothing
    /// was withdrawn.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No revocation store is registered.</exception>
    public async Task RevokeTokenAsync(string jti, string userId)
    {
        if (_revocationWriter is null)
        {
            throw new InvalidOperationException(
                "This service revokes no tokens: nothing registered an ITokenRevocationWriter. "
                + "Call AddInMemoryTokenRevocation() or AddRedisTokenRevocation(maxTokenLifetime), "
                + "or end sessions through the refresh-token store instead.");
        }

        await _revocationWriter.RevokeTokenAsync(
            jti,
            DateTimeOffset.UtcNow.AddMinutes(_jwtSettings.ExpireMinutes),
            "user requested");

        _logger.LogInformation("Token with JTI {Jti} revoked for user {UserId}", jti, userId);
    }

    /// <summary>
    /// Reads the claims the revocation check needs. Returns null when the token
    /// carries no identity to check — the caller then treats it as unverifiable.
    /// </summary>
    private static TokenIdentity? ReadTokenIdentity(ClaimsPrincipal principal)
    {
        var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                  ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var iat = principal.FindFirst(JwtRegisteredClaimNames.Iat)?.Value;

        if (string.IsNullOrEmpty(jti) || string.IsNullOrEmpty(sub)
            || !long.TryParse(iat, out var issuedAtSeconds))
        {
            return null;
        }

        return new TokenIdentity
        {
            TokenId = jti,
            SubjectId = sub,
            IssuedAt = DateTimeOffset.FromUnixTimeSeconds(issuedAtSeconds),
            SessionId = SessionClaims.Read(principal)
        };
    }

}
