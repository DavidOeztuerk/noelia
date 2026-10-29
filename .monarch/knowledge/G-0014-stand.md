# G-0014 Stand (11:48, 29.10.)

Quests Q-0046..Q-0052 alle **inreview**. Branch gepusht bis 4985fbd. PR #50 (Draft → develop) **CI komplett grün** (Run 40859842598 auf 5d6dcc4: build, vulnerabilities, demo).
- Q-0051 (b557f60): Demo- und README-Pin 7.1.0 (Ursache CI-demo rot: Pin 7.0.1 vs VersionPrefix 7.1.0; Muster fd8c20c/ab78281).
- Q-0052 (5d6dcc4): Demo-Probe + demo/eng/security-check-expectations.json um noelia.m2m.caller-policy; Check ohne Nutzung NotApplicable statt Pass (Konvention), ADR/MIGRATION/README angepasst. Test vorher rot.
- projects-74 über „wieder grün“ informiert (11:45).

**Läuft:** Warden Code-Phase (eine Runde, Sonnet) über dcafaca..HEAD.
**Danach:** Befunde nach ADR-0011 einordnen; Blocker per gate_plan-Quest fixen (inreview-Quests sind nicht überschreibbar); PR #50 ready; `monarch gate merge G-0014` (Erste-Mal-Regel develop=main=dcafaca; erkennt gate merge das nicht → projects-74, nicht umgehen); develop-Lauf nachher abwarten; verify/clear; Meldung projects-74 (Release 7.1.0 = User; CLAUDE.md-Versionszeile = Release-Schritt).
Mana 11:46: 5h 7 %, 7d 53 % (Richtwert ~49 % → >5 pp drüber ab 54 % melden).
Selbstkritik: Q-0049 zwei Vergleichsdateien unter $CLAUDE_JOB_DIR/tmp (außerhalb Scope).
