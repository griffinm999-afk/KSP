from pathlib import Path
import json,hashlib
w=Path(__file__).resolve().parent
p=Path(r'C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform')
script=(w/'verify-usils-wire.ps1').read_text()
script=script.replace("$stage=Join-Path $PSScriptRoot 'usils-work'", "$stage='"+str(p)+"'").replace('usils-wire-result.json','usils-primary-wire-result.json').replace('actual staged Bridge','actual integrated primary Bridge')
(w/'verify-usils-primary-wire.ps1').write_text(script)
changes=json.loads((w/'ExpansePlatform-passive-usils-minute-power-20261006/Source/changes.json').read_text())
sha=lambda f:hashlib.sha256(f.read_bytes()).hexdigest()
for rel,h in changes.items():assert sha(p/rel)==h['candidateSha256'],rel
allsource={f.relative_to(p).as_posix():sha(f) for area in ('src','dev','tests') for f in (p/area).rglob('*') if f.is_file() and f.suffix in ('.cs','.csproj') and not any(x in ('bin','obj','TestResults','__pycache__') for x in f.relative_to(p).parts)}
(w/'usils-primary-final-source-hashes.json').write_text(json.dumps(allsource,indent=2))
print('Final source hashes verified and captured: '+str(len(allsource)))
