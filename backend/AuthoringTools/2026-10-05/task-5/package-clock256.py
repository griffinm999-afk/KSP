from pathlib import Path
import hashlib,json,zipfile,shutil,xml.etree.ElementTree as ET
w=Path(__file__).resolve().parent
primary=Path(r'C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform')
name='ExpansePlatform-clock256-20261006';package=w/name;archive=w/(name+'.zip')
assert not package.exists() and not archive.exists(),'Refusing to overwrite candidate'
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def copy(p,r):
    target=package/r;target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,target);assert sha(p)==sha(target)
source_paths=json.loads((w/'budget-contract-source-manifest.json').read_text())
sources={}
for relative in source_paths:
    p=primary/relative;sources[relative]=sha(p);copy(p,Path('Source')/relative)
for dll,project,framework,mod in [('Expanse.WorldBridge.dll','Expanse.WorldBridge','net472','ExpanseWorldBridge'),('Expanse.Domain.dll','Expanse.WorldBridge','net472','ExpanseWorldBridge'),('Expanse.BrpColony.dll','Expanse.BrpColony','net481','ExpanseWorldBridge'),('Expanse.TrackingStation.dll','Expanse.TrackingStation','net472','ExpanseTrackingStation')]:
    copy(primary/'src'/project/'bin/Release'/framework/dll,Path('GameData')/mod/'Plugins'/dll)
for project,framework,dest in [('Expanse.Clock.Host','net8.0','Host'),('Expanse.Clock.Manager','net8.0-windows','Manager')]:
    folder=primary/'src'/project/'bin/Release'/framework
    for p in folder.rglob('*'):
        if p.is_file() and p.suffix!='.pdb':copy(p,Path(dest)/p.relative_to(folder))
for dll in ['Expanse.Clock.Core.dll','Expanse.Domain.dll']:assert sha(package/'Host'/dll)==sha(package/'Manager'/dll),'Mismatched apps'
copy(w/'relay-clock256.cjs','Relay/relay.cjs');copy(w/'start-telemetry-only.vbs','Relay/start-telemetry-only.vbs')
for project,relative,total in [('clock','tests/TestResults/payload-clock.trx',611),('production','dev/Expanse.ProductionTelemetry.Tests/TestResults/payload-production.trx',32)]:
    p=primary/relative;root=ET.parse(p).getroot();counts=root.find('.//{*}Counters');assert int(counts.attrib['passed'])==total and int(counts.attrib['failed'])==0,'Test failure';copy(p,Path('Evidence')/(project+'.trx'))
for relative in ['clock256-source-integration-result.json','clock256-oneframe-result.json','unpruned-payload-measurement.json','test-clock256-boundaries.ps1','test-clock256-relay.cjs','test-telemetry-relay.cjs','payload-validation.log']:
    copy(w/relative,Path('Evidence')/relative)
copy(primary/'docs/PRODUCTION-BUDGET-CONTRACT.md','PRODUCTION-BUDGET-CONTRACT.md')
baseline=json.loads((w/'budget-install-result.json').read_text());runtime_hashes=json.loads((w/'budget-installed-runtime-hashes.json').read_text())
for relative,expected in runtime_hashes.items():
    p=Path(r'C:\Kerbal Space Program')/relative if relative.startswith('GameData/') else Path(baseline['versionRoot'])/relative
    assert sha(p)==expected.lower(),'Installed runtime changed'
relay=Path(r'C:\Users\griff\AppData\Local\ExpanseFoundations\SiteRelay\relay.cjs')
assert sha(relay)=='ab58fb6795ab2bb683869885da836f45b3622b03b9c0df71844854b3041a5578','Installed telemetry-only relay changed'
(package/'README.txt').write_text('''Clock telemetry cap candidate, UNINSTALLED

Clock JSON bodies: 262144 bytes. Effects/WOLF remain 65536 bytes.
No new protocol, compaction, chunking or rotation behavior. Preserves honest
overflow fallback, all field/vector bounds, callback identity and 10-game-second
freshness. Native capture cadence remains unchanged; high warp can still expire
callbacks. Relay is the existing telemetry-only implementation with only its
Clock.View reader cap changed. No credentials or startup settings included.

Reconstructed full wrapper: publisher 136481 bytes, Host 139331 bytes; 90 known
configured/captured rows versus 92 live inventory count. All 15 target identities
fit ONE frame without omissions. Only captured callbacks are measured; omitted
observations are not reconstructed as actual rates. Post-install full live capture
is required, including any recently installed external mods.

Five Release builds, 611 Clock tests, 32 production tests and boundary/relay-isolation
checks passed. Exact 262144 accepted, 262145 rejected. Overflow fixtures were enlarged
to keep testing genuine oversized cases. Complete matching Host/Manager and KSP
plugins are included. Eight source/test/document changes from the installed 90cf
archive; the rejected minutes-long rotation candidate is not included.

Await coordinated Site green and explicit installation go-ahead. Do not launch KSP
from installer. Preserve existing telemetry-only launcher, credentials and mode.
No save/funds/user settings/other mods/ledger changes.
''',encoding='utf8')
manifest={str(p.relative_to(package)).replace('\\','/'):sha(p) for p in sorted(package.rglob('*')) if p.is_file()}
(package/'SHA256SUMS.txt').write_text(''.join(h+'  '+r+'\n' for r,h in manifest.items()),encoding='utf8')
with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for p in sorted(package.rglob('*')):
        if p.is_file():z.write(p,str(p.relative_to(package)).replace('\\','/'))
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    for r,h in manifest.items():assert hashlib.sha256(z.read(r)).hexdigest()==h
(w/'clock256-source-manifest.json').write_text(json.dumps(sources,indent=2))
result={'status':'verified-coherent-uninstalled','archive':str(archive),'sha256':sha(archive),'bytes':archive.stat().st_size,'sourceFiles':len(sources),'manifestFiles':len(manifest),'installedRuntimeFilesUnchanged':len(runtime_hashes),'installedRelayUnchanged':True,'clockCap':262144,'effectsWolfCap':65536,'sourceRoot':str(primary),'runtimeHashes':{r:h for r,h in manifest.items() if r.startswith(('Host/','Manager/','GameData/','Relay/'))}}
(w/'clock256-package-result.json').write_text(json.dumps(result,indent=2));print(json.dumps({k:v for k,v in result.items() if k!='runtimeHashes'},indent=2))
