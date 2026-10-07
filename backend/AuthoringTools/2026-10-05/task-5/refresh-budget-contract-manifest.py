from pathlib import Path
import hashlib,json,zipfile,difflib
w=Path(__file__).resolve().parent
stage=w/'performance-work'
with zipfile.ZipFile(w/'ExpansePlatform-reconciled-performance-20261006.zip') as z:
    source={n[7:]:z.read(n) for n in z.namelist() if n.startswith('Source/') and not n.endswith('/')}
for rel in ['src/Expanse.Clock.Core/ProductionFrameBudget.cs','src/Expanse.WorldBridge/ProductionFrameBudget.cs',
    'dev/ActualFrameChecks/ActualFrameChecks.csproj','dev/ActualFrameChecks/Program.cs','docs/PRODUCTION-BUDGET-CONTRACT.md']:
    source[rel]=None
changes=[];patch=[]
for rel,before in sorted(source.items()):
    after=(stage/rel).read_bytes()
    if before!=after:
        changes.append({'path':rel,'before':None if before is None else hashlib.sha256(before).hexdigest(),'after':hashlib.sha256(after).hexdigest()})
        patch.extend(difflib.unified_diff([] if before is None else before.decode('utf-8-sig').splitlines(True),after.decode('utf-8-sig').splitlines(True),fromfile='installed-source/'+rel,tofile='staged-contract/'+rel))
(w/'budget-contract-changes.json').write_text(json.dumps(changes,indent=2))
(w/'budget-contract.diff').write_text(''.join(patch))
(w/'budget-contract-source-manifest.json').write_text(json.dumps({rel:hashlib.sha256((stage/rel).read_bytes()).hexdigest() for rel in source},indent=2))
print(json.dumps({'status':'staged-contract-unpackaged','sourceFiles':len(source),'changedSourceFiles':len(changes),'stage':str(stage)}))
