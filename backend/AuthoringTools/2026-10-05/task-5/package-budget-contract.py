from pathlib import Path
import hashlib,json,shutil,zipfile

w=Path(__file__).resolve().parent
primary=Path(r'C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform')
name='ExpansePlatform-budget-contract-20261006'
package=w/name; archive=w/(name+'.zip')
assert not package.exists() and not archive.exists(), 'Refusing to overwrite deliverable'
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def copy(p,relative):
    out=package/relative;out.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,out)
    assert sha(out)==sha(p), 'Copy verification failed: '+str(relative)
validation=json.loads((w/'primary-validation-result.json').read_text())
assert validation['status']=='primary-validation-passed' and validation['clockTests']==611 and validation['productionTests']==32
source=json.loads((w/'budget-contract-source-manifest.json').read_text())
for relative,expected in source.items():
    p=primary/relative
    assert sha(p)==expected, 'Primary source differs from validated candidate: '+relative
    copy(p,Path('Source')/relative)
for dll,project,framework,mod in [('Expanse.WorldBridge.dll','Expanse.WorldBridge','net472','ExpanseWorldBridge'),
    ('Expanse.Domain.dll','Expanse.WorldBridge','net472','ExpanseWorldBridge'),
    ('Expanse.BrpColony.dll','Expanse.BrpColony','net481','ExpanseWorldBridge'),
    ('Expanse.TrackingStation.dll','Expanse.TrackingStation','net472','ExpanseTrackingStation')]:
    copy(primary/'src'/project/'bin/Release'/framework/dll,Path('GameData')/mod/'Plugins'/dll)
for project,framework,destination in [('Expanse.Clock.Host','net8.0','Host'),('Expanse.Clock.Manager','net8.0-windows','Manager')]:
    folder=primary/'src'/project/'bin/Release'/framework
    for p in folder.rglob('*'):
        if p.is_file() and p.suffix!='.pdb':copy(p,Path(destination)/p.relative_to(folder))
for dll in ['Expanse.Clock.Core.dll','Expanse.Domain.dll']:
    assert sha(package/'Host'/dll)==sha(package/'Manager'/dll), 'Incoherent app dependencies: '+dll
for required in ['Host/Expanse.Clock.Host.exe','Host/Expanse.Clock.Host.runtimeconfig.json','Host/Expanse.Clock.Host.deps.json',
    'Host/runtimes/win-x64/native/e_sqlite3.dll','Manager/Expanse.Clock.Manager.exe','Manager/Expanse.Clock.Manager.runtimeconfig.json']:
    assert (package/required).is_file(), 'Missing runtime: '+required
installed=json.loads((w/'performance-install-result.json').read_text())
for relative,expected in installed['installedHashes'].items():
    p=Path(r'C:\Kerbal Space Program')/relative if relative.startswith('GameData/') else Path(installed['versionRoot'])/relative
    assert sha(p)==expected.lower(), 'Installed runtime changed: '+relative
for relative in ['primary-validation-result.json','payload-validation.log','budget-contract-source-manifest.json','budget-contract-changes.json',
    'budget-contract.diff','payload-source-integration-result.json','live-clock-qualification.json','payload-validation.ps1',
    'validate-primary-tail.ps1','integrate-payload-source.ps1']:
    copy(w/relative,Path('Evidence')/relative)
copy(primary/'tests/TestResults/payload-clock.trx','Evidence/primary-clock.trx')
copy(primary/'dev/Expanse.ProductionTelemetry.Tests/TestResults/budget-contract.trx','Evidence/primary-production.trx')
copy(primary/'docs/COLONY-OBSERVATION-PERFORMANCE.md','PERFORMANCE-NOTES.md')
copy(primary/'docs/PRODUCTION-BUDGET-CONTRACT.md','PRODUCTION-BUDGET-CONTRACT.md')
(package/'Evidence/site-compatibility-handoff.txt').write_text('Parent task reports Site compatibility GREEN: 17 tests covering independent per-vessel selection markers, server-to-browser rotation, stable catalog/focus, exact omission metadata, UTF-8 65536/65537 boundary, callback reuse, 10-game-second expiry, warp and hard invalidation. No Site publication is claimed by this package.\n')
(package/'Evidence/installed-runtimes-unchanged.json').write_text(json.dumps(installed['installedHashes'],indent=2))
(package/'README.txt').write_text('''ExpansePlatform budget contract replacement - INTEGRATED PRIMARY SOURCE, UNINSTALLED

Supersedes ExpansePlatform-payload-diagnostics-20261006.zip, SHA256
6e39ea7a04139efd02599f39aedc31d8c5b087a451d596b465cf513b32e25d8b.
That earlier archive lacks the coordinated budget-omission metadata.

Built from the integrated primary source. Complete source snapshot and matching
KSP plugins plus full framework-dependent Host/Manager runtime outputs. Preserves
crew rosters, physical seats, whole-save census, production/harvester provenance,
settlement journal and background observation performance changes. Adds fair,
whole-module frame budgeting, explicit cumulative budget omission count/selection
marker, deterministic diagnostics path and bounded main-thread log handoff.

Use these runtimes together. Publisher and Host each enforce the 65536-byte UTF-8
JSON body limit. No independent 24000-byte production-section limit remains.
Selection markers may differ across vessels. Missing rows are not delivered zero;
the Site contract retains historical identity while excluding omitted current rates.
Returning callback identity and original timestamps stay unchanged. 10-game-second
freshness remains; rotation cannot promise all facilities fresh concurrently at warp.

Validation from primary: five clean Release builds, 611 clock tests, 32 production
tests, roster/census, worker ownership/handoff, tracking, detached native allocation
and real diagnostic-writer/failure checks. Physical-frame replay with synthetic
rates: <=65253-byte bodies, >=12 whole module rows per frame, all 18 facilities
covered across rotation, 62-ID census and roster flags retained. Rates are synthetic;
no live output or speedup claim. Parent reports 17 green Site compatibility tests.

All 548 source hashes verified, 18 integrated changes verified, Host/Manager shared
dependencies match, and all 40 installed runtime hashes verified unchanged.
Source development tools are archival. This package supplies no replacement game
save, settings, credentials, ledger, templates or user database. No installation,
game/app restart, relay launch, funds/resource change or ledger initialization was
performed. Await explicit closed-game installation authorization.
''',encoding='utf-8')
manifest={str(p.relative_to(package)).replace('\\','/'):sha(p) for p in sorted(package.rglob('*')) if p.is_file()}
(package/'SHA256SUMS.txt').write_text(''.join(value+'  '+relative+'\n' for relative,value in manifest.items()),encoding='utf-8')
with zipfile.ZipFile(archive,'x',compression=zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for p in sorted(package.rglob('*')):
        if p.is_file():z.write(p,str(p.relative_to(package)).replace('\\','/'))
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None, 'Archive CRC failure'
    for relative,expected in manifest.items():
        assert hashlib.sha256(z.read(relative)).hexdigest()==expected, 'Archive hash mismatch: '+relative
for relative,expected in source.items():
    assert sha(primary/relative)==expected, 'Primary changed during packaging: '+relative
result={'status':'integrated-primary-uninstalled','archive':str(archive),'sha256':sha(archive),'bytes':archive.stat().st_size,
    'sourceFiles':len(source),'integratedChanges':18,'verifiedInstalledRuntimeFilesUnchanged':len(installed['installedHashes']),
    'archiveManifestVerified':True,'supersedesSha256':'6e39ea7a04139efd02599f39aedc31d8c5b087a451d596b465cf513b32e25d8b',
    'runtimeHashes':{relative:value for relative,value in manifest.items() if relative.startswith(('GameData/','Host/','Manager/'))}}
(w/'budget-contract-package-result.json').write_text(json.dumps(result,indent=2))
(w/(name+'.zip.sha256')).write_text(result['sha256']+'  '+archive.name+'\n')
print(json.dumps({key:value for key,value in result.items() if key!='runtimeHashes'},indent=2))
