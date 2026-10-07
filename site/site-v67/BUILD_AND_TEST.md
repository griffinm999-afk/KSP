# Build and tests

## Requirements and tested commands

Tested with Node.js v24.19.0 on Linux; Worker tests use built-in `node:sqlite`, fetch/WebCrypto and no network services. Run from the extracted package root:

```sh
node build.mjs
node review-run-tests.mjs
```

Both commands succeeded on this sanitized export. The second command runs 18 source/synthetic and synthetic-Worker test files; all 18 passed. No relay, game, external endpoint or credential was used. `review-run-tests.mjs` is an export helper, not a change to the canonical application.

The app build itself uses Node built-ins and included assets/data templates; it needs no dependency installation. The original package/lock manifests are included intact. Dev dependencies are drizzle-kit 0.31.4 and drizzle-orm 0.44.2. For future schema generation, the canonical command is `pnpm db:generate` after an appropriate locked dependency install. Dependency installation was not performed for this export. The existing `pnpm-workspace.yaml` contains an unresolved esbuild build-approval placeholder; review and resolve that developer setup explicitly if installation requires it.

`node build.mjs` recreates `dist/server/index.js`, copies the included hosting manifest and migrations, and embeds review templates. Do not treat that review build as the live colony or deploy it. The hosting manifest retains the existing project identifier for provenance; no deployment credential or permission is supplied.

## Full canonical suite

```sh
node build.mjs
node --test tests/*.test.mjs
```

All 26 test files passed on canonical v67 before publication. The eight snapshot-dependent files below are preserved but excluded from the export's tested subset because their assertions depend on the omitted original operational fixtures:

- discovery-worker.test.mjs
- lifecycle-worker.test.mjs
- production-sankey.test.mjs
- saved-recipe-dedup.test.mjs
- saved-recipe-fallback.test.mjs
- supply-chain-model.test.mjs
- supply-chain-ui.test.mjs
- supply-resource-coverage.test.mjs

Running the full wildcard suite with the empty templates is expected to fail data-dependent assertions. That is not reported as a pass. The independent USI/power observation, UI and synthetic Worker round-trip tests are included in the verified 18-file subset.

## Restoring private fixtures for exact reproduction

To reproduce all snapshot-dependent cases, use the owner's authorized canonical v67 originals for these six paths:

- catalog.json
- deliveries-snapshot.json
- kerbin-shipping.json
- shipping-economics.json
- production-sources.json
- tests/fixtures/agriculture-truncated-clock-projection.json

Replace only the marked review templates in a private review copy, then rebuild and run the full suite. The complete original source commit is recorded in the inventory, but no Git credentials/history or automatic private-data retrieval is provided. Alternatively, deliberately update those snapshot-dependent tests to consume newly constructed synthetic fixtures; report that as a changed test dataset, not exact historical replay.

The six templates preserve the expected top-level shape. Empty arrays/maps contain no vessel, crew, route or transaction records. The captured-frame template is an explicitly marked empty protocol example, not the historical captured frame.

## Runtime architecture

The deployed environment is a Cloudflare Worker with a D1 binding named DB. The included migrations define all tables. The relay ingress uses an existing runtime LIVE_RELAY_KEY secret; the export contains the variable reference only. Interactive Site access remains owner-private. A live deployment, game bridge, relay and user sign-in are outside this offline source-review validation.
