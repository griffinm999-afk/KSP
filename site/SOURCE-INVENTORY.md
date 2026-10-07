# Complete handoff source inventory

The full project handoff consists of two source attachments. Extract each into a separate subfolder of one new review folder, then open that parent folder in Claude Code. The backend is supplied as actual source in its companion ZIP, not as references to files on the original Windows computer.

## Windows backend and authored integrations

Companion attachment: **ExpansePlatform-complete-source-20261007.zip**

- SHA256: `fbdcb18d516c33d078515029af57c9399052e9509c4625a3b726a5bfb290bf89`
- Exporter inventory: 640 files and 46 projects, including 8 product projects, tests, build tools, scripts, and documentation.
- Scope: current integrated primary source; passive USI/minute-power work; settlement and Foundations source; authored relay/collectors and custom integrations outside the primary tree.
- Export documentation includes per-file hashes, original locations, dependency/build information, source inventory, exclusions, and review instructions.
- The backend exporter verified original source hashes and archive integrity. This Site packet's assembly did not independently re-read or combine the backend ZIP; keep its separate provenance and validation records.
- Exclusions: credentials, live saves/runtime data, logs, compiled/third-party binaries, caches, obsolete duplicate copies, and unrelated files. The operational test fixture `recorded52.json` is excluded, so dependent tests need a safe replacement or separately authorized private reproduction.

The current source integration is complete; the corresponding USI/minute-power runtime package remains uninstalled at the last verified checkpoint. The integrated primary source is not a Git checkout. Do not assume Git worktree or history commands apply to it.

## Complete canonical Colony Site source

Directory in this attachment: **site-v67/**

- Published version: 67
- Frozen commit: `dea4cb2fe7cdb1332b7be3fcdd591dd9ebc0a007`
- Canonical tracked-path coverage: 133 paths = 126 unchanged files + 6 safe same-path data templates + 1 omitted, regenerable generated Worker.
- Original component export: 139 files. All authored code, tests, dependency/configuration manifests, build/import/relay scripts, schema/migration metadata, and tracked static assets are present. No authored code file was omitted.
- The generated Worker is excluded because it embeds private operational datasets. `node build.mjs` recreates it with the review templates.
- All 132 included source/template entries were checked against their recorded hashes and lengths. The component's package hash list verified 138 payload files, excluding the hash-list file itself. ZIP integrity passed.

Read these component documents:

- `site-v67/REVIEW_EXPORT_README.md`: component map, source provenance, runtime boundaries, and retained source literals.
- `site-v67/SOURCE_INVENTORY.json`: exact per-path provenance, sizes, and SHA256 hashes.
- `site-v67/EXCLUSIONS.json`: every replaced/omitted tracked path and excluded runtime material.
- `site-v67/BUILD_AND_TEST.md`: dependencies, verified commands, test limitations, and private-fixture restoration guidance.
- `site-v67/VALIDATION.json`: original-project versus sanitized-export validation.
- `site-v67/PACKAGE_FILES.sha256`: component payload hashes.

### Site build and test coverage

The exporter tested Node.js v24.19.0 on Linux. The build uses Node built-ins and included assets/templates; no dependency installation is required for that build. On the exported copy, `node build.mjs` succeeded and `node review-run-tests.mjs` passed 18 source/synthetic test files. These checks did not contact the game or an external endpoint.

The original canonical v67 suite passed all 26 test files before publication. All test source is included, but eight snapshot-dependent files need private originals or deliberate synthetic replacements; running the full wildcard suite with empty review data is expected to fail those assertions. This is a documented data-fixture limitation, not missing test code or a claim that all 26 tests passed on the export.

Original package/lock files are included. Schema-generation development dependencies are drizzle-kit 0.31.4 and drizzle-orm 0.44.2. `pnpm-workspace.yaml` contains an unresolved esbuild build-approval placeholder; resolve that developer setup explicitly before dependency installation when needed. No dependency installation or deployment is requested by this handoff.

### Data substitutions

The six marked review-template paths are:

- `catalog.json`
- `deliveries-snapshot.json`
- `kerbin-shipping.json`
- `shipping-economics.json`
- `production-sources.json`
- `tests/fixtures/agriculture-truncated-clock-projection.json`

Static body/biome/system metadata, fixed economic rules, images, schemas, code, and test assertions are retained. Live vessel, crew, shipment, balance, and captured-frame records are not included. Existing authored code/tests retain ordinary deployment identifiers, path guards, historical game IDs, and numerical regression expectations; the export does not claim to erase every user-specific literal.

## Combined review coverage and safety

Use both exports for backend-to-Site contract review. The Site's historical `relay.cjs` can issue WOLF operations and is not the installed telemetry-only relay; keep both implementations' provenance distinct. Do not execute either during source review.

No credentials, live game saves, runtime secrets, installed dependencies, or live databases are needed to read the code. Building or running later requires following each component's dependency and fixture notes. No files have been sent to Claude by preparing this handoff.
