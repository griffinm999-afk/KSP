from pathlib import Path
import hashlib,json,shutil,zipfile,datetime
w=Path(__file__).resolve().parent
p=Path(r'C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform')
pkg=w/'ExpansePlatform-passive-usils-minute-power-20261006'
changes=json.loads((pkg/'Source/changes.json').read_text())
sha=lambda f:hashlib.sha256(f.read_bytes()).hexdigest() if f.exists() else None
manifest=json.loads((pkg/'SHA256-MANIFEST.json').read_text())
for rel,h in manifest.items():assert sha(pkg/rel)==h,rel
archive=w/(pkg.name+'.zip')
assert sha(archive)=='aeb41ff8471ef1200d75feeabdc864824903339cf0eb2cf4ff54c71a8a13ff40'
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    for rel,h in manifest.items():assert hashlib.sha256(z.read(rel)).hexdigest()==h,rel
before={f.relative_to(p).as_posix():sha(f) for area in ('src','dev','tests') for f in (p/area).rglob('*') if f.is_file() and f.suffix in ('.cs','.csproj') and not any(s in ('bin','obj','TestResults','__pycache__') for s in f.relative_to(p).parts)}
for rel,h in changes.items():
    assert sha(pkg/'Source'/rel)==h['candidateSha256'],rel
    assert sha(p/rel) in (h['originalSha256'],h['candidateSha256']),f'Primary conflict: {rel}'
backup=w/('usils-source-backup-'+datetime.datetime.now().strftime('%Y%m%d-%H%M%S'))
for rel,h in changes.items():
    target=p/rel
    if sha(target)==h['candidateSha256']:continue
    assert sha(target)==h['originalSha256'],rel
    if target.exists():
        b=backup/rel;b.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(target,b)
    target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(pkg/'Source'/rel,target)
    assert sha(target)==h['candidateSha256'],rel
for rel,h in before.items():
    if rel not in changes:assert sha(p/rel)==h,f'Unrelated changed: {rel}'
result={'primary':str(p),'backup':str(backup),'files':changes,'unrelatedSourceFilesPreserved':len(before.keys()-changes.keys()),'archiveSha256':sha(archive),'archiveCoherent':True,'installed':False,'restarted':False}
(w/'usils-source-integration-result.json').write_text(json.dumps(result,indent=2))
print(json.dumps({k:v for k,v in result.items() if k!='files'},indent=2))
