# G-0014 Scout (2026-10-29, read-only, Sonnet-Scout, vom Orchestrator gegengelesen wo markiert)

Pfade: N = Noelia-Worktree, WT = wt-G-0001, SS = ~/Projects/Skillswap.

## A) Noelia heute
- `IJwtService` (N/src/Noelia.Infrastructure/Security/IJwtService.cs:5-11): nur `GenerateTokenAsync(UserClaims)`, `GetPrincipalFromExpiredTokenAsync`, `ValidateTokenAsync`, `RevokeTokenAsync`.
- JwtService.cs:90-100 `ValidateUserClaims` verlangt UserId + gültige E-Mail. Audience/Issuer/Lifetime aus `JwtSettings` (je eine), nicht pro Aufruf (:204-209). CustomClaims ja (:184-202). → Service-Token über JwtService nur mit Pseudo-E-Mail und fester Audience: ungeeignet.
- KeyRing (Keys/KeyRing.cs ~86-104): `ValidationParameters(issuer, audience, validateLifetime)` mit genau EINER `ValidAudience`, ClockSkew 0, Algorithmen gepinnt, `RequireSigningKey()` wirft bei Verify-only. JwtOptions: SigningKey (null = nur verifizieren), ValidationKeys (Rotation), Authority.
- `AddJwtAuthentication` (Extensions/ServiceCollectionExtensions.cs ~400-470): Audience-Prüfung an, single-valued, `MapInboundClaims=false`, NameClaimType `sub`, RoleClaimType `ClaimTypes.Role`.
- `NoeliaAccessTokenIssuer` ist NICHT in der Library: demo/src/services/UserService/.../NoeliaAccessTokenIssuer.cs, WT-Kopie WT/src/identity-service/.../Security/NoeliaAccessTokenIssuer.cs:61-82.
- Autorisierung: Policies.cs:12-19 (keine Service-Policy); `Roles.Service` (Roles.cs:6-12) unbenutzt; Permission-Policies (AuthorizationExtensions.cs:18-50); ResourceAuthorizationMiddleware.cs:181-195 → 403 eigenes JSON. Kein `IAuthorizationMiddlewareResultHandler`, kein einheitliches 404.
- `IPasswordHasher` (N/src/Noelia.Abstractions/Security/Passwords/IPasswordHasher.cs:29-78): Hash/Verify string, Verify(null) macht Dummy-Arbeit. Impl. PBKDF2 (Infrastructure), Argon2, BCrypt; Modul `PasswordHashing`. Kein generischer Constant-Time-Helper.
- Modulkatalog: `NoeliaModule` record struct (Abstractions/Hosting/NoeliaModule.cs:20-140); Beispiel Requires/Provides TokenSessions (NoeliaModuleCatalogue.cs:197-201). Jwt-Modul registriert absichtlich kein IJwtService (:52-65). Communication (:175-185) Requires IEventBus + IDistributedCache.
- M2M-Client: `ServiceCommunicationOptions.SectionName="ServiceCommunication"`, `.M2M` = `M2MConfiguration` (Enabled Default **true**, Scopes Default api.read/api.write, TokenLifetime 1h). `AddServiceCommunication` → bei M2M.Enabled `AddHttpClient<IServiceTokenProvider, ServiceTokenProvider>()` (ServiceCommunicationExtensions.cs:52-58). Wire: POST JSON `{serviceName, servicePassword}` → `{data:{accessToken, expiresIn}}` (ServiceTokenProvider.cs:134-205), kein OAuth client_credentials, Scopes nicht gesendet. Cache-Key `TokenCacheKeyPrefix+ClientId` im IDistributedCache. `ServiceCommunicationManager` :372-405 setzt `Authorization: Bearer` direkt. UseMutualTLS/ClientCertificate* ohne Leser (OptionsConsumption?).
- SecurityChecks: `noelia.jwt.key-separation` (BuiltInSecurityChecks.cs:330); kein Check zu Audience/M2M.
- README: Env `NOELIA_JWT_KID/PRIVATE_KEY/PUBLIC_KEY`, `NOELIA_DEV_KEY_SEED` (README.md:551-578); M2M nur :1595-1596.
- ASP.NET-FrameworkReference in Infrastructure (csproj:45) und Http (csproj:42); kein `MapNoelia*`-Helfer (Grep). Provider-freie Projekte: kein ASP.NET (NOELIA0002).

## B) Workertransfer
- /internal: Header `X-Notify-Secret` gegen `NotifySettings.Secret`, FixedTimeEquals, Ablehnung 404 per ProblemDetailsMiddleware (WT/.../Identity.Api/InternalEndpoints.cs:36-40, 221-232; Dublette NotifyEndpoints.cs:73-84). Inventar/Rechtetabelle: WT-Notiz Q-0001-design.md §1, §2, §5.
- Aufrufer: `X-Notify-Secret` mit Env `Identity__Secret` (10 Dienste). ServiceTokenProvider wird in WT nicht genutzt.
- Infisical nur Prosa (.env.example:26-27). Schema: `WORKERTRANSFER_*` in .env/compose → .NET `Section__Key` via `${VAR:?missing}`; Helm `WORKER_*`.

## C) Skillswap
- `POST /auth/service-token` AllowAnonymous (UserService.Api/Extensions/AuthControllerExensions.cs:103-108), Body `{ServiceName, ServicePassword}`.
- Dienst = normaler User, BCrypt.Verify, Rolle Service Pflicht (AuthRepository.cs:724-766); "Not a service account" weicht als Fehlertext ab (:744) → Orakel.
- Token: UserClaims mit Rolle Service, normale Single-Audience, kein Aufrufer-Claim außer sub.
- Empfänger nur `RequireRole("Service")` (z. B. VideocallControllerExtensions.cs:22) → jeder Dienst darf jeden Service-Endpoint. **Genau diese Lücke darf Noelia nicht haben.**
- Env: `SERVICE_<NAME>_PASSWORD` (SS/.env.example:46-50); Client: Kopie ServiceTokenProvider, Compose `ServiceCommunication__M2M__ClientId/TokenEndpoint/ClientSecret`.

## Gaps
1. Kein Service-Token-Aussteller, kein Token-Endpoint-Baustein; JwtService ungeeignet (E-Mail-Pflicht, feste Audience/Lifetime).
2. Empfänger kennt nur eine Audience; keine Ziel-Audience je Dienst.
3. Kein Client-Register (Id, Hash, 2 Slots, erlaubte Audiences); kein Verify gegen 2 Hashes ohne frühen Abbruch.
4. Kein Aufrufer-Claim und kein Leser; Roles.Service ohne Policy.
5. Keine Aufrufer-Allowlist je Endpoint, kein einheitliches 404.
6. Client spricht Skillswap-Wire-Format, keine Ziel-Audience, M2M.Enabled Default true; keine Doku der Env-Namen, kein SecurityCheck.
