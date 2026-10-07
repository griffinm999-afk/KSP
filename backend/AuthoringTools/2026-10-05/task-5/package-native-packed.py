from pathlib import Path
import hashlib, json, shutil, zipfile, xml.etree.ElementTree as ET
w=Path(__file__).resolve().parent
primary=Path(r'C:\Users\griff\Documents\Codex\2026-09-20\c-kerbal-space-program-gamedata-using\ExpansePlatform')
name='ExpansePlatform-native-packed-20261006'
package=w/name; archive=w/(name+'.zip')
assert not package.exists() and not archive.exists(), 'Refusing to overwrite package'
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def copy(p,relative):
    dest=package/relative;dest.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,dest)
    assert sha(p)==sha(dest)
sources=json.loads((w/'native-packed-build-source-hashes.json').read_text(encoding='utf-8-sig'))
for relative,expected in sources.items():
    p=primary/relative;assert sha(p)==expected.lower(), 'Source changed during package'
    copy(p,Path('Source')/relative)
for dll,project,framework,mod in [('Expanse.WorldBridge.dll','Expanse.WorldBridge','net472','ExpanseWorldBridge'),('Expanse.Domain.dll','Expanse.WorldBridge','net472','ExpanseWorldBridge'),('Expanse.BrpColony.dll','Expanse.BrpColony','net481','ExpanseWorldBridge'),('Expanse.TrackingStation.dll','Expanse.TrackingStation','net472','ExpanseTrackingStation')]:
    copy(primary/'src'/project/'bin/Release'/framework/dll,Path('GameData')/mod/'Plugins'/dll)
for project,framework,dest in [('Expanse.Clock.Host','net8.0','Host'),('Expanse.Clock.Manager','net8.0-windows','Manager')]:
    folder=primary/'src'/project/'bin/Release'/framework
    for p in folder.rglob('*'):
        if p.is_file() and p.suffix!='.pdb':copy(p,Path(dest)/p.relative_to(folder))
for dll in ['Expanse.Clock.Core.dll','Expanse.Domain.dll']:
    assert sha(package/'Host'/dll)==sha(package/'Manager'/dll), 'Host/Manager dependency mismatch'
runtime=json.loads((w/'native-packed-runtime-before.json').read_text(encoding='utf-8-sig'))
for path,expected in runtime.items():assert sha(Path(path))==expected.lower(),'Installed runtime changed'
relay=Path(r'C:\Users\griff\AppData\Local\ExpanseFoundations\SiteRelay\relay.cjs')
assert sha(w/'relay-clock256.cjs')==sha(relay),'Relay source differs from installed telemetry-only relay'
copy(w/'relay-clock256.cjs','Relay/relay.cjs');copy(w/'start-telemetry-only.vbs','Relay/start-telemetry-only.vbs')
counts={}
for group,path,expected in [('clock','tests/TestResults/native-packed-clock.trx',621),('production','dev/Expanse.ProductionTelemetry.Tests/TestResults/native-packed-production.trx',41)]:
    p=primary/path;counter=ET.parse(p).getroot().find('.//{*}Counters')
    assert int(counter.attrib['passed'])==expected and int(counter.attrib['failed'])==0
    counts[group]=expected;copy(p,Path('Evidence')/(group+'.trx'))
for relative in ['native-packed-integration-result.json','native-packed-approved-hashes.json','native-packed-build-source-hashes.json','native-packed-runtime-before.json','native-packed-primary-wire-result.json']:
    copy(w/relative,Path('Evidence')/relative)
for relative in ['actual-mode-test-result.json','native-storage-test-result.json','native-mode-wire-result.json','final-mode-integration-hash-guard.json','mode-candidate-reviewed-hashes.json','FINAL-MODE-CANDIDATE-REVIEW.txt']:
    copy(w/'astra-missing-callbacks'/relative,Path('Evidence')/relative)
readme='''Native packed callback telemetry candidate - UNINSTALLED

Accepts actual supported native broker callbacks while packed, qualified by
session/load context, current recipe, enabled/activated state, accepted broker
quantities, 10-game-second freshness, and packing mode/transition epoch.
On-rails event precedes the packed flag; pending transitions block capture.
No loaded BRP fallback, no simulation/catch-up calls, no new wire fields.
Missing qualified callbacks remain unknown. Prepared rates originate only from
native callbacks. Explicit achieved zero requires normal native completion.

Four reviewed source files integrated with exact baseline and reviewed hashes.
Five Release components rebuilt; 621 Clock and 41 production tests passed.
Independent detached tests: 18 actual mode-helper checks, 14 installed-native
broker/storage cases, 8 full reconstructed-frame Bridge/Core cases. Detached
native tests isolate UI/Unity dependencies and use synthetic recipes/brokers;
they do not establish live Unity scheduling or actual colony output.

Preserves Clock 262144-byte cap, Effects/WOLF 65536-byte caps, prior Host
empty-recipe normalization and bounded diagnostics, roster/census handling,
and the exact existing telemetry-only relay. Host/Manager dependencies match.
No credentials, saves, runtime data or startup settings are packaged.

No install, restart or game/relay controls performed. After separately authorized
installation/relaunch, verify fresh genuine packed callbacks, accepted zeroes,
packing transition invalidation, identities and Site Actual/Prepared separation.
Until that live qualification, missing Atlas/Duna rates are not proven resolved.
'''
(package/'README.txt').write_text(readme,encoding='utf8')
manifest={str(p.relative_to(package)).replace('\\','/'):sha(p) for p in sorted(package.rglob('*')) if p.is_file()}
(package/'SHA256SUMS.txt').write_text(''.join(h+'  '+r+'\n' for r,h in manifest.items()),encoding='utf8')
with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
    for p in sorted(package.rglob('*')):
        if p.is_file():z.write(p,str(p.relative_to(package)).replace('\\','/'))
with zipfile.ZipFile(archive) as z:
    assert z.testzip() is None
    for r,h in manifest.items():assert hashlib.sha256(z.read(r)).hexdigest()==h
result={'status':'reviewed-integrated-packaged-uninstalled','archive':str(archive),'sha256':sha(archive),'bytes':archive.stat().st_size,'sourceFiles':len(sources),'manifestFiles':len(manifest),'tests':counts,'runtimeFilesUnchanged':len(runtime),'clockCap':262144,'effectsWolfCap':65536,'livePackedCallbackProof':'pending authorized install/relaunch','runtimeHashes':{r:h for r,h in manifest.items() if r.startswith(('Host/','Manager/','GameData/','Relay/'))}}
(w/'native-packed-package-result.json').write_text(json.dumps(result,indent=2),encoding='utf8')
print(json.dumps({k:v for k,v in result.items() if k!='runtimeHashes'},indent=2))
