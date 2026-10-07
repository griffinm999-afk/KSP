from pathlib import Path
import shutil,json,hashlib
source=Path(r'C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform')
stage=Path('usils-work')
assert not stage.exists()
shutil.copytree(source,stage,ignore=shutil.ignore_patterns('bin','obj','.git','node_modules','.aws','.codex','.agents'))
manifest={str(p.relative_to(stage)).replace('\\','/'):hashlib.sha256(p.read_bytes()).hexdigest() for p in stage.rglob('*') if p.is_file()}
Path('usils-baseline.json').write_text(json.dumps(manifest,indent=2))
print('Staged',len(manifest),'files')
