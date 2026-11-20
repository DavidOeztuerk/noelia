# Rechte und Rollen zählten aus Identitäten, die niemand angemeldet hatte

- **Noelia-Fassung:** bis einschließlich 7.1.1
- **Gefunden beim:** Warden-Probe zu #59, 20.11.2026
- **Art:** Sicherheitslücke (offen statt geschlossen scheiternd), dieselbe Fehlerklasse an weiteren Stellen
- **Einstufung:** hoch für Fall A (Rechte, die nie ausgestellt wurden), niedrig für Fall B (nur eine fälschliche Ablehnung)
- **Blockiert:** nein
- **Stand:** behoben in G-0018; Release 7.2.0 durch den Owner, die Version im Repository bleibt 7.1.1
- **Entscheidung:** [ADR-0007](../adr/0007-rechte-und-rollen-nur-aus-angemeldeten-identitaeten.md), Issue #59

## Was passiert

Ein `ClaimsPrincipal` kann mehrere Identitäten tragen. `PermissionMiddleware` und `PermissionAuthorizationHandler`
fragten `User.Identity.IsAuthenticated`, also nur die **erste** Identität. `permission` und Rollen lasen sie danach
aus **allen**, die Nutzer-ID per `FindFirst(NameIdentifier)` ebenfalls. Endpoint mit `[RequirePermission("orders:delete")]`:

| Fall | Identitäten | Middleware | Handler |
|---|---|---|---|
| A | angemeldet ohne Recht, dahinter unangemeldet mit `permission` | **200** | erfüllt |
| B | unangemeldet mit `permission` vorn, dahinter angemeldet ohne Recht | 401 | nicht erfüllt |
| C | eine Identität, angemeldet, ohne Recht | 403 | nicht erfüllt |

Fall A ist die Lücke: Das Recht stammt aus einer Identität, die kein Scheme geprüft hat. Fall B ist nur lästig.

**Der Zwillingspfad.** `PermissionAuthorizationHandler` trug denselben Fehler, die Entscheidung fiel an derselben Stelle
falsch. Ein Fix nur in der Middleware hätte ihn offen gelassen.

**Dieselbe Klasse an weiteren Stellen.** Die Katalog- und Rollen-Policies (`RequireClaim`, `RequireRole`, `AdminOnly`,
`SuperAdminOnly`) hatten kein `RequireAuthenticatedUser` und werteten alle Identitäten aus. Besitz-, Konto- und
Datensatzhandler (`ResourceAuthorizationHandler`, `OwnershipAuthorizationHandler`, `ActiveAccountHandler`,
`EmailVerifiedHandler`, `ResourceOwnerHandler`) lasen per `FindFirst` über alle. `PrincipalFactory` las `tenant` über alle
Identitäten. Audit und Alarm lasen die Nutzer-ID über alle. **Widerruf und Datensatzprüfung** entschieden an der ersten
Identität; bei zwei Personen-Schemes wurde nur ein Token geprüft, und `jti`, `sub`, `iat` konnten aus verschiedenen
Identitäten stammen.

Dass die Standardkette das zum Teil verdeckte, war der Grund, nicht nur die Middleware zu reparieren: Ihre 401 in Fall B
verdeckte die Handler-Entscheidung. Hätte man nur sie behoben, wären die anderen Wege frei geworden.

## Was es versprach

Die README beschreibt `[RequirePermission]` und die Katalog-Policies als Antwort auf „darf diese Person das“. Ein Recht,
das nur eine nicht angemeldete Identität trägt, ist keines, das jemand der Person ausgestellt hat. Die Prüfung sollte im
Zweifel schließen. Sie öffnete in Fall A.

## Warum es Noelias ist

Nur aus Noelias Quellen und Tests nachzuvollziehen, ohne Fremdwerte: ein `ClaimsPrincipal` aus zwei `ClaimsIdentity`
(eine mit `AuthenticationType`, eine ohne, mit `permission`) durch `PermissionMiddleware` bzw.
`PermissionAuthorizationHandler`. Die Testdateien aus Teil A und B von Q-0070 stellen das mit synthetischen Claims her:

- `tests/Noelia.Infrastructure.Tests/Middleware/PermissionMiddlewareAuthenticatedIdentityTests.cs`
- `tests/Noelia.Infrastructure.Tests/Authorization/PermissionHandlerAuthenticatedIdentityTests.cs`
- `tests/Noelia.Infrastructure.Tests/Authorization/CatalogAndRolePoliciesAuthenticatedIdentityTests.cs`
- `tests/Noelia.Infrastructure.Tests/Security/ServiceTokens/PermissionFullChainAuthenticatedIdentityTests.cs` (Standardkette, `IClaimsTransformation`)
- `tests/Noelia.Infrastructure.Tests/Security/PersonHandlersAuthenticatedIdentityTests.cs`
- `tests/Noelia.Infrastructure.Tests/Security/PersonChecksMiddlewareAuthenticatedIdentityTests.cs` (Widerruf, Datensatzprüfung)
- `tests/Noelia.Infrastructure.Tests/Security/ServiceTokens/PersonChainAuthenticatedIdentityTests.cs`
- `tests/Noelia.Infrastructure.Tests/Security/Identity/PrincipalAuthenticatedIdentityTests.cs` (Mandant, Kapazität, eigene `IPrincipalFactory`)
- `tests/Noelia.Infrastructure.Tests/Security/AuditAuthenticatedIdentityTests.cs`

Gemeinsame Bausteine: `tests/Noelia.Infrastructure.Tests/Authorization/AuthenticatedIdentityFixtures.cs` und
`tests/Noelia.Infrastructure.Tests/Security/PersonCheckRig.cs`. Die Fälle sind als Gegenprobe zum Verhalten bis 7.1.1
gedacht; der ADR verlangt je Zeile eine Mutationsprobe. Eine Testzahl steht hier bewusst nicht, maßgeblich ist der Lauf
dieser Dateien.

## Was es kostet

Ein Konsument, dessen `IClaimsTransformation` oder Fremd-Scheme eine unangemeldete Identität mit Rechten anhängt, bekam
Zugang, den ihm niemand gegeben hat (Fall A). Dasselbe Muster ließ ein unangemeldetes `account_status=Active` ein
gesperrtes Konto durch, ein unangemeldetes `tenant` eine Person als „handelt für Firma T“ auftreten und eine
unangemeldete Nutzer-ID das Prüfprotokoll fälschen (Befundtabelle in ADR-0007; einige Zeilen dort sind als „Code belegt“
geführt, nicht per Laufzeitprobe).

## Stand

- [x] Rechte, Rollen, Wildcards und Nutzer-ID nur aus angemeldeten Identitäten, in Middleware und Handler gleich (Teil A)
- [x] Katalog- und Rollen-Policies, Personen-Handler, Widerruf, Datensatzprüfung, `PrincipalFactory`, Audit (Teil B)
- [x] Datensatzprüfung lehnt bei einer Ausnahme im Provider ab, statt durchzulassen
- [x] Bruch und Migrationsweg in `MIGRATION.md` (7.1.2 → 7.2.0), Regel im README
- [ ] Release 7.2.0: durch den Owner, nicht in diesem Gate
- [ ] offen, **Entscheidung 8, Issue #70**: `ClaimsPrincipalExtensions`, `DistributedRateLimitingMiddleware`,
  `TelemetryMiddleware`, `ResponseCachingHeaderMiddleware`, `NoeliaDashboardMiddleware`, `InitCommand`, `LoggingBehavior`
  lesen weiter über alle Identitäten
- [ ] offen, unverändert: ob die Katalog-Policy die Rolle kennt (Roadmap-Frage im README)
