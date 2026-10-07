"""Read-only native source audit; candidate tanks are not provider qualification."""
import argparse, collections, hashlib, json, math
from pathlib import Path

def parse(text):
    root={'tag':'ROOT','v':{},'c':[]}; stack=[root]; pending=None
    for raw in text.splitlines():
        s=raw.strip()
        if not s or s.startswith('//'): continue
        if s=='{':
            if not pending: raise ValueError('Opening brace without a node label')
            n={'tag':pending,'v':{},'c':[]}; stack[-1]['c'].append(n); stack.append(n); pending=None
            if len(stack)>256: raise ValueError('ConfigNode nesting exceeds bound')
        elif s=='}':
            if len(stack)==1: raise ValueError('Unmatched closing brace')
            stack.pop(); pending=None
        elif '=' in s:
            k,v=s.split('=',1); stack[-1]['v'].setdefault(k.strip(),[]).append(v.strip())
        else: pending=s
    if len(stack)!=1: raise ValueError('Unclosed ConfigNode')
    return root

def children(n,tag): return [c for c in n['c'] if c['tag']==tag]
def val(n,key,default=''): return n['v'].get(key,[default])[0]
def number(n,key):
    f=float(val(n,key,'0'))
    if not math.isfinite(f): raise ValueError('Non-finite native resource')
    return f

def audit(save_path,snapshot_path):
    data=save_path.read_bytes()
    if len(data)>128*1024*1024: raise ValueError('Native save exceeds read bound')
    tree=parse(data.decode('utf-8-sig')); game=children(tree,'GAME')[0]; flight=children(game,'FLIGHTSTATE')[0]
    snapshot=json.loads(snapshot_path.read_text(encoding='utf-8-sig'))['Snapshot']; state=snapshot['State']
    memberships={f['VesselId'].replace('-','').lower():(c,f) for c in state['Colonies'] for f in c['Facilities']}
    tanks=[]; crews=collections.Counter(); vessels=[]
    for vessel in children(flight,'VESSEL'):
        entry=memberships.get(val(vessel,'pid').replace('-','').lower())
        if not entry: continue
        colony,facility=entry; registered=set(facility['PartIds']); members=children(vessel,'PART')
        actual=set(int(val(p,'persistentId','0')) for p in members)
        vessels.append({'colonyId':colony['Id'],'facilityId':facility['Id'],'name':val(vessel,'name'),
            'vesselId':facility['VesselId'],'registeredParts':len(registered),'missingRegisteredPartIds':sorted(registered-actual),
            'situation':val(vessel,'sit'),'latitude':number(vessel,'lat'),'longitude':number(vessel,'lon')})
        for p in members:
            if int(val(p,'persistentId','0')) not in registered: continue
            crew=p['v'].get('crew',[]);crews['registeredCrew']+=len(crew)
            modules=children(p,'MODULE');names=[val(m,'name') for m in modules]
            warehouse=next((m for m in modules if val(m,'name')=='USI_ModuleResourceWarehouse'),None)
            for r in children(p,'RESOURCE'):
                if val(r,'name') not in ('Machinery','MaterialKits','Supplies','Ore'): continue
                tanks.append({'colonyId':colony['Id'],'facilityId':facility['Id'],'vesselId':facility['VesselId'],'vesselName':val(vessel,'name'),
                    'partId':int(val(p,'persistentId','0')),'partName':val(p,'name'),'resource':val(r,'name'),
                    'amount':number(r,'amount'),'capacity':number(r,'maxAmount'),'flowState':val(r,'flowState'),
                    'warehouseModule':warehouse is not None,'warehouseLocalTransferEnabled':val(warehouse,'localTransferEnabled') if warehouse else '',
                    'modules':names,'crewCount':len(crew),
                    'qualification':'Offline hardware candidate only; require fresh registry/provider/warehouse flow/range/worker/native recipe readback.'})
    return {'source':str(save_path.resolve()),'saveSha256':hashlib.sha256(data).hexdigest(),'sourceSnapshot':str(snapshot_path.resolve()),
        'worldId':state['WorldId'],'nativeUt':number(flight,'UT'),'vessels':vessels,'registeredCrewCount':crews['registeredCrew'],'candidateTanks':tanks}

if __name__=='__main__':
    cli=argparse.ArgumentParser(description=__doc__);cli.add_argument('--save',type=Path,required=True);cli.add_argument('--snapshot',type=Path,required=True);cli.add_argument('--output',type=Path,required=True);a=cli.parse_args()
    if a.output.exists(): raise ValueError('Audit evidence already exists; choose a new output path')
    report=audit(a.save,a.snapshot);a.output.parent.mkdir(parents=True,exist_ok=True);a.output.write_text(json.dumps(report,indent=2),encoding='utf8')
    print(json.dumps({'saveSha256':report['saveSha256'],'vessels':len(report['vessels']),'candidateTanks':len(report['candidateTanks']),
        'warehouseMachinery':[dict(partId=t['partId'],facilityId=t['facilityId'],amount=t['amount'],capacity=t['capacity'],warehouseEnabled=t['warehouseLocalTransferEnabled'],modules=t['modules']) for t in report['candidateTanks'] if t['resource']=='Machinery' and t['warehouseModule']]},indent=2))
