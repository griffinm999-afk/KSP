"""Separate, finite, paid Ranger bank candidate. Never install or certify it."""
from pathlib import Path
import argparse, collections, copy, hashlib, json, math
import ColonyPlacementTemplates as t
from ColonyRangerBankGeometry import Models, bounds, corners, intersect

ID='power-ranger-bank-v1'
PARTS={100:'Ranger.AnchorHub',101:'Ranger.PowerPack',102:'Ranger.PowerPack',103:'Ranger.PowerPack',104:'Ranger.PowerPack',105:'crewCabin',106:'batteryBankLarge',107:'batteryBankLarge',108:'batteryBankLarge',109:'structuralPanel2',110:'strutOcto',111:'structuralPanel1',112:'strutOcto',113:'structuralPanel1',114:'strutOcto',115:'structuralPanel1',116:'strutOcto',117:'structuralPanel1'}
PARTS[118]='adapterSmallMiniTall'

def digest(data): return hashlib.sha256(data).hexdigest()
def terms_hash(manifest):
    x=copy.deepcopy(manifest); x['Hash']=''; return digest(json.dumps(x,sort_keys=True,separators=(',',':')).encode())

def craft(b):
    ps=[b.new_part('Ranger_AnchorHub')];t.setv(ps[0],'part','Ranger_AnchorHub_100')
    for node in ['pod01','pod02','pod03','pod04']: b.attach(ps,0,node,b.new_part('Ranger_PowerPack'),'pod')
    spacer=b.attach(ps,0,'top',b.new_part('adapterSmallMiniTall'),'bottom',[0,0,0,1])
    cabin=b.attach(ps,spacer,'top',b.new_part('crewCabin'),'bottom',[0,0,0,1])
    upper=b.attach(ps,cabin,'top',b.new_part('batteryBankLarge'),'bottom',[0,0,0,1])
    for _ in range(2):upper=b.attach(ps,upper,'top',b.new_part('batteryBankLarge'),'bottom',[0,0,0,1])
    bed=b.attach(ps,0,'bottom2',b.new_part('structuralPanel2'),'top',[0,0,0,1])
    for node in ['bottomSW','bottomSE','bottomNW','bottomNE']:
        leg=b.attach(ps,bed,node,b.new_part('strutOcto'),'top',[0,0,0,1])
        b.attach(ps,leg,'bottom',b.new_part('structuralPanel1'),'top',[0,0,0,1])
    # Stable paid identity: cabin105, extra real spacer118. Tree serialization order
    # remains parent-before-child; craft ID is an identity, not an array index.
    mapping={t.val(p,'part'):b.part_name(p)+'_'+str(i if i<=104 else 118 if i==105 else i-1) for i,p in enumerate(ps,100)}
    for p in ps:
        t.setv(p,'part',mapping.get(t.val(p,'part'),t.val(p,'part')))
        for key in ['link','attN']:
            if key not in p['v']: continue
            result=[]
            for value in p['v'][key]:
                if key=='link': value=mapping.get(value,value)
                else:
                    for old,new in mapping.items(): value=value.replace(','+old+'_',','+new+'_')
                result.append(value)
            p['v'][key]=result
    c=b.header('Expanse Ranger bank v1: four fully paid finite packs',ps)
    t.setv(c,'description','Uncertified finite Ranger bank: four paid20Pu packs, actual Engineer cabin,16kEC; loaded normal-time500m transfer only. No proven unloaded remote grid, refill or replacement service.')
    return c

def validate(b,c,m,manifest=None):
    b.validate(c)
    ps=t.children(c,'PART'); byid={int(t.val(p,'part').rsplit('_',1)[1]):p for p in ps}
    assert {i:b.part_name(p) for i,p in byid.items()}==PARTS,'Exact19-part identity changed'
    for p in ps:
        names=[r.split(',',1)[0] for r in p['v'].get('attN',[])]
        assert len(names)==len(set(names)),'Repeated stack node'
        assert not p['v'].get('srfN'),'No fabricated surface mounts permitted'
        assert t.vec(t.val(b.cfg(b.part_name(p)),'attachRules'))[0]==1,'Stack attach rules disallow part'
        if p['v'].get('link'):assert t.vec(t.val(b.cfg(b.part_name(p)),'attachRules'))[2]==1,'Parent disallows stack children'
        for record in p['v'].get('attN',[]):
            node,payload=record.split(',',1); peer=next(t.val(z,'part') for z in ps if payload.startswith(t.val(z,'part')+'_'))
            vectors=payload[len(peer)+1:].split('_'); actual=b.node(p,node)
            for index,expected in enumerate([actual[:3],actual[3:6],actual[:3],actual[3:6]]):
                assert math.dist([float(x) for x in vectors[index].split('|')],expected)<.002,'Serialized attachment normal/offset differs from native node'
        assert not any(x in p['v'] for x in ['crew','uid','flightID','missionID','launchID']),'Native identity leaked'
        mods=t.children(p,'MODULE')
        assert [t.val(x,'name') for x in mods]==[t.val(x,'name') for x in t.children(b.cfg(b.part_name(p)),'MODULE')],'Craft module owner set changed'
        assert len([x for x in mods if t.val(x,'name')=='ColonyPlacementMarker'])==1
        assert not any('SystemHeat' in t.val(x,'name') or 'Fission' in t.val(x,'name') or 'SolarPanel' in t.val(x,'name') for x in mods),'Mixed legacy/solar source introduced'
        scale=next((x for x in mods if t.val(x,'name')=='TweakScale'),None)
        if scale: assert t.val(scale,'currentScale')==t.val(scale,'defaultScale'),'Rescaled support/pack'
    root=byid[100]
    for i,node in zip(range(101,105),['pod01','pod02','pod03','pod04']):
        p=byid[i]; assert t.val(p,'part') in root['v']['link']
        assert root['v']['attN'][i-101].startswith(node+','+t.val(p,'part')+'_')
        cfg=b.cfg('Ranger_PowerPack')
        for source in [cfg,p]:
            cv=[x for x in t.children(source,'MODULE') if t.val(x,'name')=='USI_Converter']; assert len(cv)==1
            assert t.val(cv[0],'IsStandaloneConverter')=='true'
            assert [(t.val(x,'ResourceName'),float(t.val(x,'Ratio'))) for x in t.children(cv[0],'INPUT_RESOURCE')]==[('Plutonium-238',1e-6)]
            assert [(t.val(x,'ResourceName'),float(t.val(x,'Ratio')),t.val(x,'DumpExcess')) for x in t.children(cv[0],'OUTPUT_RESOURCE')]==[('ElectricCharge',50.,'True')]
            assert [(t.val(x,'ResourceName'),float(t.val(x,'Ratio'))) for x in t.children(cv[0],'REQUIRED_RESOURCE')]==[('Plutonium-238',20.)]
        assert float(t.val(next(x for x in t.children(cfg,'MODULE') if t.val(x,'name')=='ModulePowerDistributor'),'PowerDistributionRange'))==500
    world={i:m.world(p) for i,p in byid.items()}
    # Conservative whole-part AABBs reject overlap between separately mounted
    # packs/cabin/batteries. Shared native attachment seams are not collisions.
    checks=[(a,z) for a in range(101,105) for z in list(range(a+1,109))+[118]]
    for a,z in checks: assert not intersect(world[a]['Bounds'],world[z]['Bounds']),('Pack/cabin obstruction',a,z)
    cabin=world[105]; assert len(cabin['Airlocks'])==1,'Actual cabin airlock absent/ambiguous'
    air=cabin['Airlocks'][0]['Bounds']; corridor={'Min':[air['Min'][0]-.1,air['Min'][1]-.1,air['Min'][2]-1.5],'Max':[air['Max'][0]+.1,air['Max'][1]+.1,air['Min'][2]]}
    for i,row in world.items():
        if i!=105: assert not intersect(corridor,row['Bounds']),('Airlock outward corridor blocked',i)
    feet=[world[i] for i in [111,113,115,117]]
    assert all(len(x['Colliders'])==1 for x in feet),'Foot physical collider missing'
    floor=[x['Colliders'][0]['Bounds']['Min'][1] for x in feet]
    assert max(floor)-min(floor)<.002,'Noncoplanar support candidates'
    support={'Min':[min(x['Colliders'][0]['Bounds']['Min'][i] for x in feet) for i in range(3)],'Max':[max(x['Colliders'][0]['Bounds']['Max'][i] for x in feet) for i in range(3)]}
    # Every corner of independently empty/full four-pack fuel is exercised; no
    # assumption that asymmetric pack consumption preserves perfect balance.
    com=[]; density=float(t.val(b.resources['Plutonium-238'],'density'))
    for mask in range(16):
        rows=[]
        for i,p in byid.items():
            mass=float(t.val(b.cfg(b.part_name(p)),'mass')); pos=t.vec(t.val(p,'pos'))
            offset=t.vec(t.val(b.cfg(b.part_name(p)),'CoMOffset','0,0,0')); pos=t.add(pos,t.rotate(t.vec(t.val(p,'rot')),offset))
            if 101<=i<=104 and mask&(1<<(i-101)): mass+=20*density
            rows.append((mass,pos))
        # One tonne at cabin center conservatively envelopes one aboard operator
        # for this static sensitivity check; not a native crew-mass observation.
        rows.append((1.,t.vec(t.val(byid[105],'pos'))))
        mass=sum(r[0] for r in rows); center=[sum(r[0]*r[1][i] for r in rows)/mass for i in range(3)]
        slope=(center[1]-support['Min'][1])*math.tan(math.radians(3))
        margin=min(center[i]-support['Min'][i]-slope for i in [0,2]); margin=min(margin,*(support['Max'][i]-center[i]-slope for i in [0,2]))
        assert margin>.25,'Static fuel-corner CoM leaves support polygon'
        com.append({'FullFuelMask':mask,'Center':center,'Slope3DegreeMarginMeters':margin})
    if manifest is not None:
        assert manifest['Id']==ID and manifest['Version']==1 and not manifest['RuntimeCertified']
        assert manifest['Homes']==0 and manifest['HomeCraftPartIds']==[] and manifest['Workers']==1 and manifest['WorkerTrait']=='Engineer'
        assert manifest['ExpectedPartCount']==19 and manifest['Hash']==terms_hash(manifest)
        assert manifest['CraftSha256']==digest((t.serialize(c)+'\n').encode()),'Sealed craft changed'
        expected_startup=sorted([(i,'Plutonium-238',20000000) for i in range(101,105)]+[(i,'ElectricCharge',1000000000) for i in range(101,105)]+[(i,'ElectricCharge',4000000000) for i in range(106,109)])
        assert sorted((x['CraftPartId'],x['ResourceName'],x['Amount']) for x in manifest['StartupContents'])==expected_startup,'Startup stock not exact once on real tanks'
        pu=[x for x in manifest['StartupContents'] if x['ResourceName']=='Plutonium-238']
        assert sorted((x['CraftPartId'],x['Amount']) for x in pu)==[(i,20000000) for i in range(101,105)],'Exact paid full pack allocation changed'
        assert [x for x in manifest['EmbeddedContents'] if x['Resource']=='Plutonium-238']==[{'Resource':'Plutonium-238','Amount':80000000}]
        assert manifest['EmbeddedContents']==[{'Resource':'ElectricCharge','Amount':16000000000},{'Resource':'Plutonium-238','Amount':80000000}]
        for row in manifest['RawParts']:
            cfg=copy.deepcopy(b.cfg(row['PartName']));cfg['c']=[x for x in cfg['c'] if not(x['tag']=='MODULE' and t.val(x,'name')=='ColonyPlacementMarker')]
            assert row['PartConfigSha256']==digest(t.serialize(cfg).encode()),'Current selected part configuration changed'
        assert sum(x['Capacity'] for x in manifest['ResourceCapacities'] if x['ResourceName']=='ElectricCharge')==16000000000
        assert not manifest.get('DefaultPlanningRole') and manifest['OperatingLimits']['NativeReplacementServiceCertified'] is False
    allbounds=bounds([point for x in world.values() for point in corners([(x['Bounds']['Min'][i]+x['Bounds']['Max'][i])/2 for i in range(3)],[x['Bounds']['Max'][i]-x['Bounds']['Min'][i] for i in range(3)])])
    return {'ModelBounds':allbounds,'AirlockCorridor':corridor,'FeetPlaneY':floor[0],'SupportProjection':support,'CenterOfMassCases':com,'PackModelClearanceMeters':world[105]['Bounds']['Min'][1]-max(world[i]['Bounds']['Max'][1] for i in range(101,105)),
            'Authority':'Source model and node checks only; no actual contacts, terrain, load-bearing, EVA, loaded utility or native certification.'}

def build(install,out,reader,evidence,replace_existing=False):
    catalogpath=out/'colony-template-catalog.json';catalog=None
    if catalogpath.exists():
        catalog=json.loads(catalogpath.read_text(encoding='utf-8-sig'))
        owned=[x for x in catalog['Templates'] if x['Id']==ID]
        assert len(owned)<=1
        assert not owned or replace_existing and not owned[0]['RuntimeCertified'],'Explicit new-candidate replacement required before any write'
    b=t.Builder(install,out);m=Models(b,reader);c=craft(b);b.native_part_keys(c);b.clean(c)
    geometry=validate(b,c,m)
    ext=geometry['ModelBounds']; ground=geometry['FeetPlaneY']
    envelope=(math.floor(ext['Min'][0]-1),math.ceil(ext['Max'][0]+1),math.floor(ext['Min'][2]-2),math.ceil(ext['Max'][2]+1),math.ceil(ext['Max'][1]-ground+1),[0,0,0,1])
    b.emit(ID,'Ranger power bank',c,envelope,workers=1,trait='Engineer',purpose='power')
    manifest=b.manifests[-1]
    # Current owned marker patch is compiler instrumentation, excluded by the
    # exact production ConfigTerms contract; do not strip any other module.
    for row in manifest['RawParts']:
        cfg=copy.deepcopy(b.cfg(row['PartName'])); markers=[x for x in t.children(cfg,'MODULE') if t.val(x,'name')=='ColonyPlacementMarker']
        assert len(markers)==1 and markers[0]['v']=={'name':['ColonyPlacementMarker']} and not markers[0]['c']
        cfg['c']=[x for x in cfg['c'] if x not in markers]
        row['PartConfigSha256']=digest(t.serialize(cfg).encode())
    manifest['StaticModelInputs']=[x for model in m.results.values() for x in model['Assets']]
    for x in manifest['StartupContents']:
        if x['ResourceName']=='Plutonium-238': x['Amount']=20000000
    embedded=collections.Counter()
    for x in manifest['StartupContents']:embedded[x['ResourceName']]+=x['Amount']
    manifest['EmbeddedContents']=[{'Resource':r,'Amount':a} for r,a in sorted(embedded.items())]
    manifest['OperatingLimits']={'OperatorCabinCraftPartId':105,'PaidPackCraftPartIds':[101,102,103,104],'PuUnitsPerPack':20,'EcCapacity':16000,'SourceOwnDemandMaximumEcPerSecond':10,'LoadedReceiverDemandMaximumEcPerSecond':150,'LoadedOnlyDistributionRangeMeters':500,
        'GenerationFloorAtIllustrative151200s':198.4879999997,'HorizonRule':'Use current paid fuel and real charter/destination/import horizon, never nominal200EC/s forever. Illustrative floor is rounded down after the actual pure-bound floating guard.',
        'DistributionRule':'Actual Engineer aboard; current native normal-time conserved delivery; same body; source>=75% charged; receiver>=25% charged and capacity>=100*its own current EC/s at actual5s cadence. All reachable peers consume shared budget.',
        'ConservativeFullStockReplacementReviewKerbinDays':108,'ReviewCadenceKerbinDays':1,'RefillAuthorized':False,'NativeReplacementServiceCertified':False,
        'ServiceLimit':'Finite paid fuel. No native refill/replacement service has been demonstrated. Paid physical replacement and long-term modeled service require separate review and native proof.',
        'BackgroundLimit':'No unloaded remote-grid qualification; separate local self-contained paid packs remain required. Optional BRP absence/mismatch holds normally.',
        'StaffingLimit':'One paid Engineer workplace, actual cabin105 and existing fresh People ownership; no homes, specialist bonus or inherited unloaded bank workplace.'}
    manifest['EnvelopeProvenance']='Installed Mu source geometry and fixed mount/support sensitivity only; live colliders, contacts, slopes, boarding and load-bearing pending.'
    manifest['CertificationEvidence']='Candidate: finite plutonium; actual Engineer and loaded normal-time500m distribution required. No proven refill, replacement or unloaded remote grid. Cash is fabrication/labor only; materials and startup contents are charged once, shortage imports/freight separately. Native placement, support, access, utility and cold/BRP acceptance pending.'
    manifest['StaticValidation']['SourceModelGeometry']=True
    manifest['Hash']=terms_hash(manifest)
    path=out/(ID+'.manifest.json');path.write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    saved=t.parse((out/(ID+'.craft')).read_text());validate(b,saved,m,manifest)
    evidence.mkdir(parents=True,exist_ok=True)
    (evidence/'geometry.json').write_text(json.dumps(geometry,indent=2)+'\n')
    (evidence/'models.json').write_text(json.dumps(m.results,indent=2)+'\n')
    if catalog is not None:
        if catalog['PartConfigurationHash'].lower()!=b.cache_hash.lower():
            extra=catalog.setdefault('AdditionalSourcePartCacheHashes',[])
            assert isinstance(extra,list) and len(extra)<128
            if b.cache_hash.lower() not in [x.lower() for x in extra]:extra.append(b.cache_hash)
        index=next((i for i,x in enumerate(catalog['Templates']) if x['Id']==ID),None)
        if index is None:catalog['Templates'].append(manifest)
        else:catalog['Templates'][index]=manifest
        catalogpath.write_text(json.dumps(catalog,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'Id':ID,'CraftSha256':manifest['CraftSha256'],'ManifestHash':manifest['Hash'],'Parts':19,'Envelope':envelope,'StaticAuthority':geometry['Authority']},indent=2))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--install',required=True);p.add_argument('--output',required=True);p.add_argument('--mu-reader',required=True);p.add_argument('--evidence',required=True);p.add_argument('--replace-existing-candidate',action='store_true');a=p.parse_args()
    workspace=Path(__file__).resolve().parents[2];out=Path(a.output).resolve();evidence=Path(a.evidence).resolve()
    if not out.is_relative_to(workspace) or not evidence.is_relative_to(workspace/'outputs'):raise ValueError('Workspace candidate/evidence output only')
    build(Path(a.install),out,Path(a.mu_reader),evidence,a.replace_existing_candidate)
