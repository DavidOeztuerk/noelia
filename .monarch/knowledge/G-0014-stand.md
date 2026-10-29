# G-0014 Stand (11:05, 29.10.)

Alle Bau-Quests inreview (Q-0046..Q-0050), Branch gepusht bis a4c1e15, PR #50 (Draft) nach develop.

**Rot auf PR #50 (Hinweis projects-74):** CI-Run 40845985332, Job `demo`, Schritt „Build and test the demo against it“: NU1603/NU1109 — Demo sucht Noelia 7.0.1, gepackt wird 7.1.0.
- Ursache belegt: `Directory.Build.props` VersionPrefix 7.1.0 (b69ef78, Q-0049), aber `demo/Directory.Packages.props:6` NoeliaVersion 7.0.1. Muster: 7.0.0 (fd8c20c) änderte beide im selben Commit; 7.0.1 (ffb8a2e + ab78281 in PR #33) zog Demo-Pin und README „Consuming Noelia“ (README.md:2523-2534, 5 Stellen) im selben PR mit. demo/README.md-Beispiele `7.0.1-security.*` blieben damals unverändert → jetzt auch.
- **Q-0051** per gate_plan angelegt (Scope: demo/Directory.Packages.props, README.md). Accept 11:03 vom Mana-Guard verweigert (5h 90 %) → RecapAndStop bis Reset 11:30, Weckruf 11:33.

**Nächster Schritt nach Reset:**
1. Mana prüfen; Q-0051 accept/start; NoeliaVersion 7.1.0 + README 2523-2534 auf 7.1.0; kein NoWarn, nichts aufweichen; gitleaks; commit; push; Q-0051 submit.
2. CI auf PR #50 komplett grün abwarten → kurz an projects-74 melden („wieder grün“).
3. Dann Warden Code-Phase (eine Runde) über dcafaca..HEAD — Hinweise siehe unten.
4. gate merge G-0014 (Erste-Mal-Regel develop=main=dcafaca; erkennt gate merge das nicht → projects-74), develop-Lauf nachher, clear, Meldung.

Warden-Hinweise: gitleaks-Umbenennung KnownVector/OtherVector (Q-0047, lokaler Commit 7aa6b34 zurückgenommen, nie gepusht); Konstruktor-Fix a4c1e15 (binäre Kompatibilität, CRLF); ci.yml nur Trigger; Knight-Abweichungen Q-0048 (Audience-Form beim Aufruf, Fallback nur auf Gruppen, Tür→Fallback per Routenmuster, Check-Modul Composition, Snapshot ignoriert X-RateLimit-Remaining/Reset, Harness ohne Logging-Modul) und Q-0049 (Manager wirft bei vorhandenem Authorization-Header im CC-Modus, synchrone Erneuerung mit 10-s-Backoff solange Token gültig, ServiceTokenCache public); Q-0051 Demo-Pin.
Selbstkritik: in Q-0049 zwei Vergleichsdateien unter $CLAUDE_JOB_DIR/tmp geschrieben (außerhalb Scope, Regel 1).
Mana 11:03: 5h 90 % (Reset 11:30 CET), 7d 52 % (Start 39 %, Richtwert ~49 %).
