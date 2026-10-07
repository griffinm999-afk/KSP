# Complete Colony Site source export — v67

Canonical commit: `dea4cb2fe7cdb1332b7be3fcdd591dd9ebc0a007`
Published Site: https://expanse-colony.griffinm999.chatgpt.site (owner-private).
Source freeze: 2026-10-07 UTC. No pending Site edits at export time.

This replaces the earlier selected-file review slice. Every authored source, route, UI, server, database schema/migration, test, build/dependency manifest, script and static asset tracked by this commit is present. Of 133 tracked paths, 126 are unchanged; six operational-data files are represented by clearly labeled review templates; one generated Worker bundle is omitted because it embeds the private data. No authored code file is omitted.

## Package map

- `dist/index.html`: canonical authored HTML/CSS/inline UI template, despite its directory name.
- `build.mjs`: embeds that template, JavaScript modules, assets and JSON inputs into a Cloudflare Worker.
- `worker.js`, `live-server.js`, `discovery-*.js`, `lifecycle-*.js`, `wolf-server.js`: route, authenticated telemetry ingestion, registry, reversible lifecycle and WOLF request handling.
- `production-observation.js`, `demand-observation.js`: validation, exact context identity, capture qualification and expiry.
- `supply-chain-model.js`, `production-flow.js`, `demand-flow.js`, `production-sankey.js`, `supply-chain-ui.js`: resources, qualified actual/configured/model flows, USI and completed power windows, diagrams/UI.
- `crew-*.js`, remaining root UI modules, `supply-chain.css`: other preserved app functions.
- `db/`, `drizzle/`, `dist/.openai/drizzle/`: schema and complete migration history, including metadata. They contain schema definitions, not live database rows.
- `tests/`: all 26 authored test files and helpers. `production-fixture.mjs` and `demand-fixture.mjs` are synthetic. The captured telemetry JSON is replaced by a marked empty example.
- `assets/`: all tracked PNG/JPEG assets and their available provenance, including the masthead.
- `catalog-bodies.json`: static body/biome definitions. The review `catalog.json` also retains the canonical static body/biome/system definitions while removing vessels, balances and operational observations.
- `relay.cjs`, `import-*.cjs`: authored integration/import source. The Windows backend and externally authored integration source are supplied separately in the handoff.

## Explicit exclusions and retained literals

Read `EXCLUSIONS.json` for every affected path. Live D1 rows, raw game/save files, credentials, `.env`, runtime logs/caches, `node_modules` and Git history are absent. Operational snapshot JSON is replaced at the same relative path with empty/synthetic review data, never presented as actual colony state. Existing fixed fuel-price rules remain in the economic template; shipment/world identifiers and receipt terms do not.

Authored code and tests are unchanged, so they retain ordinary project/origin identifiers, fixed install/save-path guards, historical game IDs and numerical regression expectations. This is disclosed source/test content; the package does not claim zero user-specific literals. No credential values were found in retained text.

`relay.cjs` is the historical canonical relay source and can execute WOLF commands. It is not the installed telemetry-only relay. Review it as code; do not execute or reinstall it as part of this review. The protected runtime relay credential is external and is not included. Import scripts also read local game files and should not be run against a live game during review.

## v67 status

The published Site includes live-production default selection, proportional/colored partial-flow diagrams, unique saved/current recipe matching, and compatible passive-USI plus completed-minute-power adapters. Source/model/Worker/DOM tests passed before publication. Backend installation and real USI/minute-power acceptance were still pending at Site publication; source tests are not proof of live game behavior.

## Integrity

`SOURCE_INVENTORY.json` records the frozen version, original-path coverage, provenance and per-file SHA256. `PACKAGE_FILES.sha256` covers all packaged payload files except itself. The outer ZIP checksum is supplied separately. `BUILD_AND_TEST.md` explains exact tested commands, exclusions and restoration.
