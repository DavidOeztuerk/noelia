## Befund

Gefunden in G-0016, zur Laufzeit geprüft: `PermissionMiddleware` entscheidet „authentifiziert oder nicht“ allein an der ersten Identity (`context.User.Identity`, `src/Noelia.Infrastructure/Middleware/PermissionMiddleware.cs:64`).

Führt der `PolicyEvaluator` zwei Schemes zusammen, kann eine Identity vorn stehen, die niemand authentifiziert hat. Dann antwortet dieser Schritt mit `401 User is not authenticated`, obwohl eine authentifizierte Identity im Principal steht.

## Wann das eintritt

Beides muss zusammenkommen:

- Ein fremdes Scheme meldet `AuthenticateResult.Success` mit einer Identity ohne `AuthenticationType`. Das ist ein Fehler in diesem Scheme.
- Eine Tür nennt dieses Scheme neben `NoeliaService`.

## Beleg

`An_identity_nobody_authenticated_next_to_a_service_is_not_a_person` in `ServiceCallerFullChainTests`: Mit eingeschaltetem `PermissionEnforcement` und dem Dienst-Scheme zuerst kam `401` mit dem Text oben. Der Test läuft deshalb bewusst ohne dieses Modul; ADR-0003 nennt die Grenze.

## Einordnung

- Das scheitert geschlossen, es öffnet nichts.
- Es blockiert 7.1.1 nicht.
- Eine Änderung träfe die Standardkette aller Konsumenten. Deshalb steht sie nicht in einem Patch.

Zu entscheiden: ob die Middleware `Identities.Any(i => i.IsAuthenticated)` prüft. Danach kann der Test wieder mit dem Modul laufen.
