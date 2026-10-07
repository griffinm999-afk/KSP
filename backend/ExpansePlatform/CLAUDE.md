# Claude instructions for ExpansePlatform

@AGENTS.md

AGENTS.md above is the shared rulebook for Claude and Codex. Put rules that
apply to both agents there, not here. This file holds only Claude-specific notes.

## Working alongside Codex
- Claude and Codex share this folder and take turns. Before editing, check
  whether files were changed since your last session and read any new progress
  notes in docs/ before assuming the current state.
- Do not edit files another agent is actively working on.
- Record finished work the same way the project already does: a concise note in
  docs/, with what changed, what was tested, and what remains unverified.

## Project state
- Clock.Manager (the WPF Windows app) is abandoned. Do not build, test or extend
  it. Clock.Host remains: it is the headless process that carries telemetry from
  WorldBridge to the SiteRelay.
- The USI/minute-power update is source-integrated and tested but not installed
  or accepted in the live game. Source tests do not establish live behavior.
- Do not run launchers, relay scripts, deployment or game-affecting tools, or
  change saves, funds, resources, credentials or settings, without explicit
  approval for that specific action.
