from pathlib import Path
import hashlib, json, shutil, zipfile

w = Path(__file__).resolve().parent
stage = w / 'performance-work'
root = Path(r'C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform')
name = 'ExpansePlatform-reconciled-performance-20261006'
package = w / name
archive = w / (name + '.zip')
assert not package.exists() and not archive.exists(), 'Refusing to overwrite an existing deliverable'
def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def copy(source, relative):
    dest = package / relative
    dest.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, dest)

expected = json.loads((w / 'performance-baseline.json').read_text())
changes = json.loads((w / 'performance-changes.json').read_text())
expected.update({c['path']: c['after'] for c in changes})
for rel, sha in expected.items():
    assert digest(root / rel) == sha and digest(stage / rel) == sha, ('Source mismatch', rel)
    copy(stage / rel, Path('Source') / rel)

plugins = {
    'Expanse.WorldBridge.dll': ('Expanse.WorldBridge', 'net472', 'ExpanseWorldBridge'),
    'Expanse.Domain.dll': ('Expanse.WorldBridge', 'net472', 'ExpanseWorldBridge'),
    'Expanse.BrpColony.dll': ('Expanse.BrpColony', 'net481', 'ExpanseWorldBridge'),
    'Expanse.TrackingStation.dll': ('Expanse.TrackingStation', 'net472', 'ExpanseTrackingStation'),
}
for dll, (project, framework, mod) in plugins.items():
    copy(stage / 'src' / project / 'bin/Release' / framework / dll, Path('GameData') / mod / 'Plugins' / dll)
for project, framework, destination in [('Expanse.Clock.Host', 'net8.0', 'Host'), ('Expanse.Clock.Manager', 'net8.0-windows', 'Manager')]:
    output = stage / 'src' / project / 'bin/Release' / framework
    for source in output.rglob('*'):
        if source.is_file() and source.suffix != '.pdb': copy(source, Path(destination) / source.relative_to(output))
for required in ['Host/Expanse.Clock.Host.exe', 'Host/Expanse.Clock.Host.deps.json', 'Host/Expanse.Clock.Host.runtimeconfig.json',
                 'Host/runtimes/win-x64/native/e_sqlite3.dll', 'Manager/Expanse.Clock.Manager.exe', 'Manager/Expanse.Clock.Manager.runtimeconfig.json']:
    assert (package / required).is_file(), required
for dll in ['Expanse.Clock.Core.dll', 'Expanse.Domain.dll']:
    assert digest(package / 'Host' / dll) == digest(package / 'Manager' / dll), ('Runtime mismatch', dll)

for rel in ['performance-baseline.json', 'performance-changes.json', 'performance-integration-result.json',
            'performance.diff', 'performance-validation.log', 'reconciliation-integration-result.json']:
    copy(w / rel, Path('Evidence') / rel)
copy(stage / 'tests/TestResults/performance-clock-final.trx', 'Evidence/performance-clock-final.trx')
copy(stage / 'dev/Expanse.ProductionTelemetry.Tests/TestResults/performance-production-final.trx', 'Evidence/performance-production-final.trx')
copy(stage / 'docs/COLONY-OBSERVATION-PERFORMANCE.md', 'PERFORMANCE-NOTES.md')
(package / 'README.txt').write_text('''ExpansePlatform reconciled performance candidate — UNINSTALLED

This is the coherent successor to the earlier telemetry-only candidate. Use this
runtime set together; do not mix individual DLLs from the older package.

Includes crew rosters, physical seat capacities, whole-save vessel census,
production/harvester telemetry and provenance, settlement journal, and bounded
background observation derivation plus aggregate timing counters.

GameData: matching WorldBridge, .NET Framework Domain, BrpColony and TrackingStation
plugin DLLs. Host and Manager: complete framework-dependent .NET 8 runtime outputs,
including SQLite dependencies; Windows Manager requires the .NET 8 Desktop Runtime.
This is an overlay for the existing KSP/mod installation, not a standalone mod setup.
It supplies no replacement game settings, saves, templates or user databases.

Source: the complete reconciled source snapshot, including existing development
tools and reference configs. Source tools are archival; none were run to install
this candidate. Evidence: source diffs/hashes, build log and regression results.
SHA256SUMS.txt covers every other file in this directory.

Validation: five clean Release builds; 611 Clock tests and 27 production tests;
roster/census, pure worker, actual-DLL handoff/reset/backpressure, detached native
serializer and Tracking Station offline checks passed.

No installation/restart or live performance claim. PERFORMANCE-NOTES.md explains
what moved off the game thread, remaining main-thread work, bounded ownership,
reset handling, sampled timing semantics and the later live qualification needed.
''', encoding='utf-8')

installed = json.loads((w / 'performance-installed-baseline.json').read_text())
for rel, sha in installed.items():
    assert digest(Path(r'C:\Kerbal Space Program\GameData') / rel) == sha, ('Installed binary changed', rel)
(package / 'Evidence/installed-binaries-unchanged.json').write_text(json.dumps(installed, indent=2))
payload = {str(p.relative_to(package)).replace('\\', '/'): digest(p) for p in sorted(package.rglob('*')) if p.is_file()}
(package / 'SHA256SUMS.txt').write_text(''.join(sha + '  ' + rel + '\n' for rel, sha in payload.items()), encoding='utf-8')
with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as z:
    for p in sorted(package.rglob('*')):
        if p.is_file(): z.write(p, str(p.relative_to(package)).replace('\\', '/'))
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    assert len(z.namelist()) == len(payload) + 1
    for rel, sha in payload.items(): assert hashlib.sha256(z.read(rel)).hexdigest() == sha, rel
sha = digest(archive)
(w / (archive.name + '.sha256')).write_text(sha + '  ' + archive.name + '\n')
result = {'archive': str(archive), 'sha256': sha, 'bytes': archive.stat().st_size,
          'source_files_verified': len(expected), 'archive_files_verified': len(payload) + 1,
          'runtime_hashes': {k:v for k,v in payload.items() if not k.startswith(('Source/', 'Evidence/')) and k.endswith(('.dll', '.exe'))},
          'installed_binaries_unchanged': installed}
(w / 'performance-package-result.json').write_text(json.dumps(result, indent=2))
print(json.dumps({k:v for k,v in result.items() if k != 'runtime_hashes'}, indent=2))
