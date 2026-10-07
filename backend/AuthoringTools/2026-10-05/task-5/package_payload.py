from pathlib import Path
import hashlib, json, shutil, zipfile, difflib

w=Path(__file__).resolve().parent
stage=w/'performance-work'
name='ExpansePlatform-payload-diagnostics-20261006'
package=w/name
archive=w/(name+'.zip')
assert not package.exists() and not archive.exists(), 'Refusing to overwrite deliverable'
def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def copy(p,rel):
    out=package/rel;out.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,out)
changes=[];patches=[]
with zipfile.ZipFile(w/'ExpansePlatform-reconciled-performance-20261006.zip') as old:
    source={n[7:]:old.read(n) for n in old.namelist() if n.startswith('Source/') and not n.endswith('/')}
for rel in ['src/Expanse.Clock.Core/ProductionFrameBudget.cs','src/Expanse.WorldBridge/ProductionFrameBudget.cs',
            'dev/ActualFrameChecks/ActualFrameChecks.csproj','dev/ActualFrameChecks/Program.cs']:
    source[rel]=None
for rel,before in sorted(source.items()):
    p=stage/rel;after=p.read_bytes();copy(p,Path('Source')/rel)
    if before!=after:
        changes.append({'path':rel,'before':None if before is None else hashlib.sha256(before).hexdigest(),'after':digest(p)})
        patches.extend(difflib.unified_diff([] if before is None else before.decode('utf-8-sig').splitlines(True),after.decode('utf-8-sig').splitlines(True),fromfile='before/'+rel,tofile='candidate/'+rel))
(w/'payload-changes.json').write_text(json.dumps(changes,indent=2))
(w/'payload.diff').write_text(''.join(patches))
for dll,project,framework,mod in [('Expanse.WorldBridge.dll','Expanse.WorldBridge','net472','ExpanseWorldBridge'),
    ('Expanse.Domain.dll','Expanse.WorldBridge','net472','ExpanseWorldBridge'),
    ('Expanse.BrpColony.dll','Expanse.BrpColony','net481','ExpanseWorldBridge'),
    ('Expanse.TrackingStation.dll','Expanse.TrackingStation','net472','ExpanseTrackingStation')]:
    copy(stage/'src'/project/'bin/Release'/framework/dll,Path('GameData')/mod/'Plugins'/dll)
for project,framework,dest in [('Expanse.Clock.Host','net8.0','Host'),('Expanse.Clock.Manager','net8.0-windows','Manager')]:
    folder=stage/'src'/project/'bin/Release'/framework
    for p in folder.rglob('*'):
        if p.is_file() and p.suffix!='.pdb':copy(p,Path(dest)/p.relative_to(folder))
for dll in ['Expanse.Clock.Core.dll','Expanse.Domain.dll']:
    assert digest(package/'Host'/dll)==digest(package/'Manager'/dll), 'Runtime mismatch: '+dll
installed=json.loads((w/'performance-install-result.json').read_text())
for rel,sha in installed['installedHashes'].items():
    path=Path(r'C:\Kerbal Space Program')/rel if rel.startswith('GameData/') else Path(installed['versionRoot'])/rel
    assert digest(path).lower()==sha.lower(), 'Installed runtime changed: '+rel
for p in ['payload-validation.log','payload-changes.json','payload.diff','live-clock-qualification.json','payload-validation.ps1']:
    copy(w/p,Path('Evidence')/p)
copy(stage/'tests/TestResults/payload-clock.trx','Evidence/payload-clock.trx')
copy(stage/'dev/Expanse.ProductionTelemetry.Tests/TestResults/payload-production.trx','Evidence/payload-production.trx')
copy(stage/'docs/COLONY-OBSERVATION-PERFORMANCE.md','PERFORMANCE-NOTES.md')
(package/'README.txt').write_text('''ExpansePlatform payload and diagnostics candidate - STAGED, UNINSTALLED

Coherent successor to installed version 20261006-153230. Includes matching KSP
plugins and complete Host/Manager runtime outputs, with all existing roster,
census, production/harvester provenance and settlement-journal features.

Production fitting uses actual frame space, whole-row priority and rotating
vessel fairness. Crowded frames remain explicitly incomplete. All frames retain
the 65,536-byte limit. No rate, input, output, power or game resource is changed.
Timing diagnostics use the KSP Logs directory plus bounded main-thread KSP log
handoff for summaries and rate-limited writer failures.

Five clean Release builds; 611 clock tests; 29 production tests; roster/census,
ownership/handoff, detached native allocation and logging checks passed.
Replay of captured physical frame plus synthetic rates: <=65,528-byte bodies,
>=13 whole module rows per frame, all 18 facilities covered across rotation,
62-ID census and roster flags preserved. These are synthetic rate fixtures,
not live output or performance measurements.

No runtime installation, game/app restart, relay launch, save/settings/funds
edit or ledger initialization occurred. All 40 installed runtime hashes checked
unchanged. Primary source was not overwritten; Source contains the staged
candidate and Evidence/payload.diff records changes from the installed package.
Install only through the next explicitly authorized closed-game workflow. Use
these runtimes together; do not mix individual DLLs with older sets.
''')
manifest={str(p.relative_to(package)).replace('\\','/'):digest(p) for p in sorted(package.rglob('*')) if p.is_file()}
(package/'SHA256SUMS.txt').write_text(''.join(sha+'  '+rel+'\n' for rel,sha in manifest.items()))
with zipfile.ZipFile(archive,'x',compression=zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for p in sorted(package.rglob('*')):
        if p.is_file():z.write(p,str(p.relative_to(package)).replace('\\','/'))
result={'status':'staged-uninstalled','archive':str(archive),'sha256':digest(archive),'bytes':archive.stat().st_size,
    'sourceFiles':len(source),'changedSourceFiles':len(changes),'installedRuntimeFilesUnchanged':len(installed['installedHashes']),
    'runtimeHashes':{rel:sha for rel,sha in manifest.items() if rel.startswith(('GameData/','Host/','Manager/'))}}
(w/'payload-package-result.json').write_text(json.dumps(result,indent=2))
(w/(name+'.zip.sha256')).write_text(result['sha256']+'  '+archive.name+'\n')
print(json.dumps({k:v for k,v in result.items() if k!='runtimeHashes'},indent=2))
