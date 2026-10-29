# G-0014 Stand

- Q-0046 Design: ADR-0002 angenommen. Warden Runde 1 + Delta approve; **User-Freigabe 29.10.2026 über projects-74 („Ja“)**, vermerkt in ADR (53f5f08) und PR #50. Q-0046 submit → inreview.
- **Q-0047 in_progress** (seit ~08:40): Knight (Sonnet) implementiert per TDD (Ausstellung, typ-Sperre). Danach: Orchestrator prüft Bericht + Diff, Tests selbst laufen lassen, push, submit.
- **Q-0050** (CI-Trigger `branches: [main, develop]` für push und pull_request, Scope nur `.github/workflows/ci.yml`): angelegt, Entscheid projects-74 (a). Accept verweigert (eine aktive Quest je Projekt). **Reihenfolge bestätigt: Q-0047 submit → Q-0050 → Q-0048 → Q-0049.** Erste-Mal-Regel „develop vorher grün“: develop = main = dcafaca, CI auf main für dcafaca grün; erkennt gate merge das nicht → projects-74 melden, nicht umgehen.
- Danach: Warden Code-Phase (eine Runde, inkl. ci.yml), `monarch gate merge G-0014`, Quests clearen, Meldung an projects-74. Release 7.1.0 macht der User.
