import json, collections
from pathlib import Path
p=Path(r'C:\Kerbal Space Program\saves\The Expanse\persistent.sfs')
root={'name':'root','v':{},'c':[]}; stack=[root]; pending=''
for raw in p.read_text(encoding='utf-8-sig').splitlines():
    s=raw.strip()
    if not s or s.startswith('//'): continue
    if s=='{':
        n={'name':pending,'v':{},'c':[]}; stack[-1]['c'].append(n); stack.append(n)
    elif s=='}': stack.pop()
    elif '=' in s:
        k,v=s.split('=',1);stack[-1]['v'].setdefault(k.strip(),[]).append(v.strip())
    else: pending=s
def nodes(n,name):
    for c in n['c']:
        if c['name']==name: yield c
def one(n,k):return n['v'].get(k,[''])[0]
game=next(nodes(root,'GAME'));flight=next(nodes(game,'FLIGHTSTATE'))
vessels=list(nodes(flight,'VESSEL'))
physical={one(v,'pid').replace('-','').lower():(v,[x for part in nodes(v,'PART') for x in part['v'].get('crew',[])]) for v in vessels}
scenario=next(s for s in nodes(game,'SCENARIO') if one(s,'name')=='LifeSupportScenario')
def walk(n):
    yield n
    for c in n['c']:yield from walk(c)
statuses=[n for n in walk(scenario) if n['name']=='VESSEL_DATA']
rows=[];cached_ids=set()
for s in statuses:
    key=one(s,'VesselId').replace('-','').lower();cached_ids.add(key)
    v,crew=physical.get(key,({'v':{},'c':[]},[]))
    rows.append({'cachedCrew':one(s,'NumCrew'),'physicalCrew':len(crew),'type':one(v,'type'),'situation':one(v,'sit'),'recycler':one(s,'RecyclerMultiplier')})
unmatched=[{'crew':len(crew),'type':one(v,'type'),'situation':one(v,'sit')} for key,(v,crew) in physical.items() if crew and key not in cached_ids]
allcrew=[x for v,crew in physical.values() for x in crew]
cfg=next(n for n in walk(scenario) if n['name']=='LIFE_SUPPORT_CONFIG')
print(json.dumps({'savedAtUtcEpoch':p.stat().st_mtime,'settings':{k:one(cfg,k) for k in ['SupplyAmount','WasteAmount','ECAmount','EnableRecyclers','HabRange']},'cachedVessels':rows,'unmatchedCrewVessels':unmatched,'physicalCrewRecords':len(allcrew),'uniqueCrew':len(set(allcrew)),'duplicateCrewRecords':len(allcrew)-len(set(allcrew))},indent=2))
