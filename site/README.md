# ExpansePlatform complete authored source handoff

Prepared 7 October 2026 for independent Claude review. The complete handoff consists of two complementary ZIP attachments containing actual project source, tests, scripts, documentation, and Site assets. This replaces the earlier handoff that supplied only a Site review snapshot and pointers to Windows backend source. No access to the original Windows paths is needed to read the supplied source.

## Start here

1. Create a new review folder. Extract **each** of the two attached ZIPs into its own subfolder inside it, so their documentation cannot overwrite each other. Keep this separate from the working project and installed game.
2. In Claude Desktop, open **Code → Local**, select the parent review folder containing both extractions, choose **Opus 5.5**, and select **Plan** mode.
3. Paste `CLAUDE-REVIEW-PROMPT.txt`.
4. For source coverage and exact file identities, read `SOURCE-INVENTORY.md` and `PACKET-MANIFEST.json`. Read each component's export/build documentation before attempting a build.

This is a source handoff, not an installer. Review the code first. Do not execute launchers, relay scripts, deployment commands, or game-affecting tools as part of the initial review. Source files can describe operations that remain outside the review's authorization.

## Components

- **Separate companion attachment: `ExpansePlatform-complete-source-20261007.zip`.** Contains the full current Windows primary source plus authored tests, scripts, documentation, and external relay/collector/custom integrations. Its export covers 640 files and 46 projects, including 8 product projects. The original relative layout is preserved. This backend source is in that supplied ZIP, not inside the Site ZIP and not merely a path on the original computer.
- **This attachment, `site-v67/`:** the complete canonical Site source export at the published v67 checkpoint, including UI, Worker, database schema/migrations, build scripts, tests, dependencies' package metadata, and static assets. Export documentation identifies excluded operational data and any safe sample replacements.
- `REVIEW-CONTEXT.md`: architecture, recent changes, invariants, known limitations, and review priorities.
- `CLAUDE-REVIEW-PROMPT.txt`: a ready-to-paste request for a focused independent review.

The Windows primary folder is not a Git checkout. Its export uses an inventory and file hashes. The Site has a separate canonical source tree and commit history; this packet does not substitute an old Windows Site checkout for it.

## Version and runtime boundaries

- **Windows primary source:** the passive USI/minute-power update has been integrated and verified. The integration changed 23 approved source files, preserving 364 unrelated `.cs`/`.csproj` files. The source snapshot includes that integration.
- **Installed game runtime, last verified checkpoint:** the native-packed baseline is installed. Its native loaded/packed production observations passed runtime acceptance.
- **New USI/minute-power runtime:** source-integrated and tested, but the update has not been installed or accepted in the live game. Source tests do not establish live behavior.
- **Colony Site:** published v67, commit `dea4cb2fe7cdb1332b7be3fcdd591dd9ebc0a007`, published 6 October 2026 at 22:36:58 UTC. It includes the USI observation/flow adapters, completed-minute power handling, and same-UT reconnect invalidation. The complete checkout passed 26 test files plus SVG/DOM/Worker checks. Any export-specific verification is stated separately in its documentation.
- The owner's private Site is https://expanse-colony.griffinm999.chatgpt.site. Source review does not require signing in or contacting it.

## Included source versus external requirements

“Complete authored source” means the current project code and its authored supporting files, including integrations outside the primary folder, not just a selection of recent-change files. It does not mean a redistributed KSP installation, installed third-party mods, credentials, live save data, dependencies' downloaded source/binaries, generated build output, or historical backups.

The component inventories explain exclusions and substitutions. Required third-party references, package restore, local SDKs, and excluded private data must be obtained or configured as documented before a build or live run. Do not copy credentials into the review folder or send live saves to an external service merely to eliminate a test limitation.

The backend exporter verified its original-file hashes, manifest, and ZIP integrity. The Site file hashes and ZIP integrity were checked separately during assembly. The two archives have not been independently audited as one combined filesystem. These packaging checks do not claim that every exported application builds or runs without external dependencies. Distinguish recorded original-project test results from checks actually run on the exported copy. The backend export omits the live runtime fixture `recorded52.json`; tests depending on it need a safe replacement, as explained in the backend exclusions/build notes.
