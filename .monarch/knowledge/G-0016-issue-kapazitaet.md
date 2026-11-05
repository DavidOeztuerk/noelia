## Befund

Gefunden in G-0016 (Warden-Runde zu ADR-0003), zur Laufzeit geprüft: Die Policies `NoeliaPolicies.ActingForCompany` und `NoeliaPolicies.ActingAsSelf` lehnen **jede** Person ab, wenn die Kette so aussieht, wie das README sie zeigt: `.UseAuth().UsePrincipal()`.

## Ursache

- `ActingAsSelfHandler` und `ActingForCompanyHandler` lesen `ICurrentPrincipal.Current`.
- `Current` steht erst fest, wenn `PrincipalMiddleware` gelaufen ist.
- `UseAuth()` enthält Authentifizierung **und** Autorisierung. `UsePrincipal()` läuft also danach, und während der Policy-Prüfung ist `Current` noch `null`.

## Beleg

Sonde in G-0016: Chain mit Standardkette, dann `UseTokenRevocation`, dann `UseNoeliaPrincipal`. Dieselbe gültige Person bekam an `RequireAuthorization()` 200 und an `RequireAuthorization(NoeliaPolicies.ActingAsSelf)` **403**.

Workertransfer ist schon darauf gestoßen und umgeht es: Der Kommentar in `MembershipPermission.cs` beschreibt genau diesen 403 und liest deshalb aus dem `ClaimsPrincipal`.

## Die Doku widerspricht sich

- `PrincipalMiddleware` und `UseNoeliaPrincipal`: „after authentication and before authorization“.
- `PrincipalModule.UsePrincipal`: „Belongs after UseAuth()“.
- Das README zeigt beide Policies direkt unter `.UseAuth().UsePrincipal()`.

Mit dem Builder gibt es keine Stelle zwischen den beiden Schritten, weil `UseAuth()` sie zusammenfasst.

## Zu entscheiden

Eine Option: Die Handler lesen über `IPrincipalFactory` aus `context.User`, statt aus `ICurrentPrincipal`. Das macht sie unabhängig von der Reihenfolge. Dabei ist ADR-0003 zu beachten: Eine Dienst-Identität gehört nicht in den Principal.

Das blockiert 7.1.1 nicht und hat nichts mit Dienst-Türen zu tun. Registriert, vorhanden, nicht wirksam: dieselbe Fehlerklasse wie in `docs/MASTERPLAN-5.0.md` §1.
