from pathlib import Path
import hashlib, json, shutil

workspace = Path(__file__).resolve().parent
root = Path(r'C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform').resolve()
stage = workspace / 'reconciliation-work'
baseline = json.loads((workspace / 'reconciliation-baseline.json').read_text())
changes = json.loads((workspace / 'reconciliation-changes.json').read_text())
def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

# Check the entire original source snapshot before any destination write.
for rel, expected in baseline.items():
    assert digest(root / rel) == expected, ('Source changed since inspection', rel)
assert len(changes) == 10
for change in changes:
    rel = change['path']
    target = (root / rel).resolve()
    assert target.is_relative_to(root) and target.relative_to(root).parts[0] in ('src', 'dev', 'tests')
    assert digest(stage / rel) == change['after'], ('Candidate changed', rel)
    assert (digest(target) if target.exists() else None) == change['before'], ('Destination changed', rel)

backup = workspace / 'reconciliation-backup'
for change in changes:
    rel = change['path']
    target = root / rel
    if target.exists():
        saved = backup / rel
        saved.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(target, saved)
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(stage / rel, target)

expected = dict(baseline)
expected.update({c['path']: c['after'] for c in changes})
for rel, sha in expected.items():
    assert digest(root / rel) == sha, ('Post-integration mismatch', rel)
installed = Path(r'C:\Kerbal Space Program\GameData\ExpanseWorldBridge\Plugins\Expanse.WorldBridge.dll')
installed_hash = digest(installed)
assert installed_hash.upper() == 'B9093F1BB532B7D20B79C809A3BE8F0DEBB19D42157F538EDDFE5FF4F6AF6753'
result = {'integrated_files': len(changes), 'verified_source_files': len(expected), 'installed_bridge_sha256': installed_hash, 'changes': changes}
(workspace / 'reconciliation-integration-result.json').write_text(json.dumps(result, indent=2))
print(json.dumps({k:v for k,v in result.items() if k != 'changes'}, indent=2))
