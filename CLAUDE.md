# ExpansePlatform

Colony-management system for Kerbal Space Program (save "The Expanse"; mods: USI Life Support, WOLF, BRP, Harmony).

## Layout
- `backend/` — source export of the Windows primary (not Git there). C# (.NET 8 + .NET Framework 4.7.2): WorldBridge KSP plugin, Clock Host/Manager, Domain, BrpColony, Launcher, TrackingStation. Start with `backend/README-HANDOFF.md`, `backend/ExpansePlatform/AGENTS.md`, `backend/ExpansePlatform/docs`.
- `site/` — review packet. `site/site-v67/` is the Colony Site (plain JS, Cloudflare Worker, D1) at published v67, commit `dea4cb2fe7cdb1332b7be3fcdd591dd9ebc0a007`. Read `site/README.md`, `site/REVIEW-CONTEXT.md` (invariants), `site/SOURCE-INVENTORY.md`, `site/CLAUDE-REVIEW-PROMPT.txt`.

## Ground rules
- Source-only snapshot. Do not run launchers, relay scripts, deployment or game-affecting tools; many can mutate saves/resources/credentials.
- Never substitute historical relay code for the installed telemetry-only SiteRelay. Never add credentials or live saves.
- Keep it simple (KISS, per AGENTS.md): one current build, targeted tests, no speculative abstractions.
- Distinguish source/test evidence from installed runtime behavior. The USI/minute-power update is source-integrated and tested but NOT installed or accepted in the live game.
- Key invariants (see REVIEW-CONTEXT.md): Actual throughput comes only from accepted transfers; measured zero != unknown != configured intent; completed minute-power windows are immutable, expire after 120 real s, and are never routed through the 10 game-s instantaneous expiry; ClockView body capped at 262,144 bytes (WOLF/Effects 65,536); 21,600 s/Kerbin day applied exactly once.

## Build / test (Windows; needs local KSP/Unity/Harmony/USI/BRP assemblies, HintPaths are absolute)
Run sequentially: `dotnet build` WorldBridge, Clock.Host (-c Release; Clock.Manager, the WPF app, is abandoned); `dotnet test backend/ExpansePlatform/tests/Expanse.Clock.Tests.csproj`; `dotnet test backend/ExpansePlatform/dev/Expanse.ProductionTelemetry.Tests/...`. `tests/recorded52.json` is excluded; dependent tests need a synthetic fixture.
Site: see `site/site-v67/BUILD_AND_TEST.md` (Node tests under `tests/`).
