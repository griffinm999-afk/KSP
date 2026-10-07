from pathlib import Path
import hashlib,json,shutil,zipfile
w=Path(__file__).resolve().parent
stage=w/'usils-work'
primary=Path(r'C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform')
name='ExpansePlatform-passive-usils-minute-power-20261006'
package=w/name
archive=w/(name+'.zip')
assert not package.exists() and not archive.exists()
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
changes={}
for area in ('src','dev','tests'):
    for p in (stage/area).rglob('*'):
        if not p.is_file() or p.suffix not in ('.cs','.csproj') or any(s in ('bin','obj','TestResults','__pycache__') for s in p.relative_to(stage).parts):continue
        rel=p.relative_to(stage);old=primary/rel
        if not old.exists() or sha(old)!=sha(p):changes[rel.as_posix()]={'originalSha256':sha(old) if old.exists() else None,'candidateSha256':sha(p)}
def copy(p,rel):
    dest=package/rel;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,dest);assert sha(p)==sha(dest)
for rel,expected in changes.items():copy(stage/rel,Path('Source')/rel)
for f in ('USI-EC-WIRE-CONTRACT.md','USI-BACKEND-UPDATE-README.md','verify-usils-wire.ps1','usils-wire-result.json','usils-native-tests.log','usils-hook-registration-tests.log','usils-production-tests.log','usils-clock-tests.log','usils-roster-tests.log','usils-performance-tests.log','usils-bridge-build.log','usils-Expanse.Clock.Host-build.log','usils-Expanse.Clock.Manager-build.log','usils-Expanse.BrpColony-build.log','usils-Expanse.TrackingStation-build.log'):
    copy(w/f,Path('Evidence')/f)
copy(stage/'src/Expanse.WorldBridge/bin/Release/net472/Expanse.WorldBridge.dll',Path('GameData/ExpanseWorldBridge/Plugins/Expanse.WorldBridge.dll'))
for project,framework,dest in [('Expanse.Clock.Host','net8.0','Host'),('Expanse.Clock.Manager','net8.0-windows','Manager')]:
    for p in (stage/'src'/project/'bin/Release'/framework).rglob('*'):
        if p.is_file() and p.suffix!='.pdb':copy(p,Path(dest)/p.relative_to(stage/'src'/project/'bin/Release'/framework))
assert sha(package/'Host/Expanse.Clock.Core.dll')==sha(package/'Manager/Expanse.Clock.Core.dll')
for rel,expected in changes.items():
    old=primary/rel
    assert (sha(old) if old.exists() else None)==expected['originalSha256'],'Primary source changed during packaging'
    assert sha(stage/rel)==expected['candidateSha256'],'Staged source changed during packaging'
unchanged={}
for rel in ('src/Expanse.WorldBridge/ColonyRuntime.ProductionObserver.cs','src/Expanse.WorldBridge/ColonyProductionTelemetryMath.cs','src/Expanse.WorldBridge/WorldBridgeAddon.cs','src/Expanse.WorldBridge/VesselCensusTracker.cs','src/Expanse.WorldBridge/SettlementJournal.cs'):
    p=stage/rel
    if p.exists():assert sha(p)==sha(primary/rel);unchanged[rel]=sha(p)
(package/'Source/changes.json').write_text(json.dumps(changes,indent=2))
(package/'Evidence/preserved-source.json').write_text(json.dumps(unchanged,indent=2))
manifest={p.relative_to(package).as_posix():sha(p) for p in package.rglob('*') if p.is_file()}
(package/'SHA256-MANIFEST.json').write_text(json.dumps(manifest,indent=2))
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
    for p in sorted(package.rglob('*')):
        if p.is_file():z.write(p,p.relative_to(package).as_posix())
result={'archive':str(archive),'sha256':sha(archive),'bytes':archive.stat().st_size,'changedSourceFiles':list(changes),'bridgeSha256':sha(package/'GameData/ExpanseWorldBridge/Plugins/Expanse.WorldBridge.dll'),'coreSha256':sha(package/'Host/Expanse.Clock.Core.dll'),'manifestEntries':len(manifest),'installed':False,'primarySourceModified':False,'privateFixturesIncluded':False}
(w/'usils-package-result.json').write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))
