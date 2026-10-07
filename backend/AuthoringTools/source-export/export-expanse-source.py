from pathlib import Path
import os,json,hashlib,zipfile,re,datetime,collections
w=Path(__file__).resolve().parent
base=Path(r'C:\Users\griff\Documents\Codex')
primary=base/'2026-09-20/c-kerbal-space-program-gamedata-using/ExpansePlatform'
name='ExpansePlatform-complete-source-20261007'
out=w/name;out.mkdir(exist_ok=True)
sha=lambda b:hashlib.sha256(b).hexdigest()
skip={'bin','obj','.git','node_modules','__pycache__','.agents','.codex','.aws','artifacts','outputs','run','package','TestResults','.venv','licenses'}
exts={'.cs','.csproj','.sln','.slnx','.props','.targets','.ps1','.psm1','.py','.cjs','.js','.ts','.md','.ks','.cmd','.vbs','.bat','.html','.tsx','.jsx','.css','.xaml','.resx','.config','.cfg','.json','.svg','.png','.ico','.draft'}
code={'.cs','.csproj','.sln','.ps1','.psm1','.py','.cjs','.js','.ts','.ks','.cmd','.vbs','.bat','.props','.targets'}
manifest={};origins={};excluded=[];duplicates=[];seen={}
def add(f,dest,canonical=False):
 rel=str(dest).replace('\\','/');low=f.name.lower()
 if f.suffix.lower() not in exts and f.name not in ('LICENSE','.gitignore','.editorconfig','NuGet.Config'):return
 if low=='.env' or low.startswith('.env.') or any(x in low for x in ('credential','secret','token','password')):excluded.append({'file':rel,'reason':'Sensitive configuration name'});return
 if f.name=='recorded52.json' or re.match(r'COLONY-(RESUME|PAUSE-CHECKPOINT|SHUTDOWN-TRACE)',f.name):excluded.append({'file':rel,'reason':'Recorded runtime fixture or personal operational log'});return
 data=f.read_bytes()
 if f.suffix.lower() not in ('.png','.ico'):
  text=data.decode('utf-8-sig',errors='replace')
  patterns=[r'(?i)(?:serviceToken|relayKey|apiKey|accessToken|password)\s*[:=]\s*[\"\x27]([^\"\x27\r\n]{12,})[\"\x27]',r'\bsk-[A-Za-z0-9_-]{20,}',r'\bgh[pousr]_[A-Za-z0-9]{20,}',r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----']
  if any(re.search(p,text) for p in patterns):excluded.append({'file':rel,'reason':'Possible embedded secret; withheld'});return
 h=sha(data)
 if not canonical and h in seen:duplicates.append({'file':rel,'sameAs':seen[h],'sha256':h});return
 target=out/dest;target.parent.mkdir(parents=True,exist_ok=True);target.write_bytes(data)
 manifest[rel]=h;origins[rel]={'originalPath':str(f),'sha256':h,'bytes':len(data)};seen.setdefault(h,rel)
def tree(root,dest):
 for home,dirs,files in os.walk(root):
  dirs[:]=[x for x in dirs if x not in skip]
  for fn in files:add(Path(home)/fn,dest/(Path(home)/fn).relative_to(root),True)
tree(primary,Path('ExpansePlatform'))
tree(primary.parent/'ExpanseFoundations',Path('Related/ExpanseFoundations'))
inventory=json.loads((w/'expanse-source-workspace-inventory.json').read_text())
for r in inventory:
 root=Path(r['root'])
 for rel in r['files']:
  f=root/rel
  if f.suffix.lower() not in code|{'.md'}:continue
  parts=Path(rel).parts
  if len(parts)==1 and root in (w,base/'2026-10-04/task',base/'2026-10-05/task-4'):
   if f.name in ('add-career-funds.py','inventory-expanse-source.py'):continue
   add(f,Path('AuthoringTools')/root.parent.name/root.name/rel)
  elif root==base/'2026-10-05/task-6' and f.name=='verify-native-serializer.ps1':
   add(f,Path('AuthoringTools/production-native-serializer')/f.name)
  elif root==base/'2026-10-01/what-x20' and f.name=='measure-cache.ps1':
   add(f,Path('AuthoringTools/performance')/f.name)
relay=Path(r'C:\Users\griff\AppData\Local\ExpanseFoundations\SiteRelay')
for fn in ('relay.cjs','start.vbs','start-telemetry-only.vbs'):add(relay/fn,Path('Relay/current')/fn,True)
add(Path(__file__),Path('AuthoringTools/source-export/export-expanse-source.py'),True)
projects=[x for x in manifest if x.endswith('.csproj')]
dependencies=[]
for rel in projects:
 text=(out/rel).read_text(encoding='utf-8-sig')
 dependencies.append({'project':rel,'frameworks':re.findall(r'<TargetFrameworks?>(.*?)</TargetFrameworks?>',text),'packages':[{'id':a,'version':b} for a,b in re.findall(r'<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"',text)],'hintPaths':re.findall(r'<HintPath>(.*?)</HintPath>',text)})
excluded.extend([{'file':x,'reason':y} for x,y in [('All bin/obj/caches/runtime artifacts','Generated binaries and personal runtime data'),('All source staging/backup/versioned package copies','Obsolete copies; current integrated primary is canonical; source identity dedup applies to tools'),('RMM-investigation, older work craft/flight scripts, decompiled game/mod reference source','Unrelated flight work or third-party source'),('Site stages and browser profiles','Website canonical v67 exported separately by Site owner'),('All .sfs/.craft/.dll/.exe/.zip/.dpapi/.env and runtime .log/.ndjson','Saves, binaries, credentials, caches or personal readings'),('Credential store and status/receipt files in SiteRelay','Excluded; only authored scripts included')]])
def generated(fn,value):
 data=(json.dumps(value,indent=2)+'\n').encode() if not isinstance(value,str) else value.encode()
 (out/fn).write_bytes(data);manifest[fn]=sha(data)
generated('SOURCE-ORIGINS.json',origins)
generated('PROJECTS-AND-DEPENDENCIES.json',dependencies)
generated('EXCLUSIONS.json',excluded)
generated('DEDUPLICATED-TOOLS.json',duplicates)
generated('WORKSPACE-INVENTORY.json',[{'root':r['root'],'authoredCandidateCount':len(r['files']),'note':'Relevant distinct source tools selected; generated/live data and obsolete product copies excluded.'} for r in inventory])
generated('README-HANDOFF.md',f'''Full source snapshot for review, generated {datetime.datetime.now(datetime.timezone.utc).isoformat()}.

ExpansePlatform/ is the complete current primary authored source, preserving project layout, all eight product projects, tests, tools, documentation and authored assets. Related/ExpanseFoundations is the original authored foundation component. AuthoringTools contains distinct external integration, packaging, telemetry collector, inspection and relay test scripts. Relay/current is the currently installed authored relay script; credentials are excluded. No included script has been executed as part of this export. Many deployment/fixture scripts can mutate resources if run: review source before executing. No external Claude edits, live game, settings, source files or running processes were changed.

Primary was a plain source directory, not Git. Integrated USI/minute-power 23-file candidate is present; source hashes are authoritative for this snapshot. SOURCE-ORIGINS records original locations and SHA256; SHA256-MANIFEST covers every included file except itself. Snapshot rechecks originals and fails if they change during capture. Duplicate tools are omitted only when byte-identical; distinct integration versions retain descriptive filenames. Stale staged product source and packaged build copies are omitted in favor of current primary.

Build: Windows, .NET 8 SDK and .NET Framework 4.7.2 references (NuGet package). Python 3 and Node.js are required for authored helper/relay scripts. See PROJECTS-AND-DEPENDENCIES for every framework, NuGet version and third-party HintPath; supply legitimately installed KSP/Unity, Harmony, USITools/WOLF/USI and BRP assemblies locally. No proprietary game/mod DLL or downloaded dependency is redistributed. Installed USI-LS guard was version112.0.1.0, assembly1.0.0.0, SHA2562aa072962e237d6a31f5f8defc93ad4ac5d03bfa51b0cadb7ddd00b84ffb427d. Project absolute HintPaths need adjustment on another PC. Assets under assets/ are authored UI build resources.

Run builds/tests sequentially to avoid shared Core output file locks:
dotnet build ExpansePlatform/src/Expanse.WorldBridge/Expanse.WorldBridge.csproj -c Release
dotnet build ExpansePlatform/src/Expanse.Clock.Host/Expanse.Clock.Host.csproj -c Release
dotnet build ExpansePlatform/src/Expanse.Clock.Manager/Expanse.Clock.Manager.csproj -c Release
dotnet test ExpansePlatform/tests/Expanse.Clock.Tests.csproj -c Release
dotnet test ExpansePlatform/dev/Expanse.ProductionTelemetry.Tests/Expanse.ProductionTelemetry.Tests.csproj -c Release

Prior integrated validation:625 Clock/domain/Host tests and65telemetry tests pass; Bridge/Host/Manager build;26detachednativeILcases,29Bridgequalification checks,5Frameworkregistration checks,roster/performance and10wirecases pass. Export deliberately excludes tests/recorded52.json, a recorded runtime fixture; tests consuming it require a separately supplied synthetic fixture and are not reproducible from this source-only export alone. All test source remains included. See EXCLUSIONS for other omissions. No runtime evidence logs/fixtures are exported.

Review focus: native observer passivity/exception preservation; exact owner/getter/broker provenance; independent supply/EC accepted transfer capture; shortage/dumped Mulch handling; original catch-up intervals; unioned durations/dedup; real cadence/expiry and mode/reset gaps; partial coverage aggregation; optional wire bounds/256KiB fallback; settlement journal correctness and economic/physical separation. Packed and unsupported owner coverage remains partial/unknown. Live USI callbacks and runtime cost remain unverified until authorized installation/relaunch. Website v67 will be supplied separately; no website is bundled here. No direct transmission to Claude.
''')
for rel,item in origins.items():assert sha(Path(item['originalPath']).read_bytes())==item['sha256'],'Source changed during snapshot: '+rel
generated('SHA256-MANIFEST.json',dict(manifest))
archive=w/(name+'.zip')
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
 for f in sorted(out.rglob('*')):
  if f.is_file():z.write(f,f.relative_to(out).as_posix())
with zipfile.ZipFile(archive) as z:
 assert z.testzip() is None
 for rel,h in manifest.items():assert sha(z.read(rel))==h,rel
result={'archive':str(archive),'sha256':sha(archive.read_bytes()),'bytes':archive.stat().st_size,'includedFiles':len(manifest),'projects':projects,'sourceFiles':len(origins),'excludedEntries':len(excluded),'deduplicatedTools':len(duplicates),'snapshotUt':datetime.datetime.now(datetime.timezone.utc).isoformat(),'sourceModified':False,'installed':False,'restarted':False}
(w/'complete-source-export-result.json').write_text(json.dumps(result,indent=2));print(json.dumps(result,indent=2))
