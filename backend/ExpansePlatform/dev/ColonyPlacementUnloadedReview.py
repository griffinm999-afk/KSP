"""Compare exact actual native loaded/unloaded saves; no native writes."""
import argparse
import hashlib
import json
from pathlib import Path
from ColonyPlacementTemplates import parse, children, val


def witness(path, operation):
    data=path.read_bytes(); game=children(parse(data.decode('utf-8-sig')),'GAME')[0]
    scenarios=children(game,'SCENARIO')
    queue=[x for s in scenarios if val(s,'name')=='ColonyPlacementScenario' for x in children(s,'PLACEMENT') if val(children(x,'REQUEST')[0],'OperationId')==operation]
    if len(queue)!=1: raise ValueError('Exact operation receipt missing/duplicated')
    status=children(queue[0],'WITNESS')[0]
    vessels=[v for v in children(children(game,'FLIGHTSTATE')[0],'VESSEL') if any(val(m,'name')=='ColonyPlacementMarker' and val(m,'operationId')==operation for p in children(v,'PART') for m in children(p,'MODULE'))]
    if len(vessels)!=1: raise ValueError('Original native marked vessel missing/duplicated')
    parts=children(vessels[0],'PART'); anchor_id=val(status,'FoundationId')
    anchors=[a for s in scenarios if val(s,'name')=='FoundationRegistry' for a in children(s,'ANCHOR') if val(a,'id')==anchor_id]
    if len(anchors)!=1 or val(status,'Stage')!='Anchored': raise ValueError('Actual native anchor/anchored receipt missing')
    maps=sorted((int(val(p,'persistentId')),int(val(p,'uid')),[sorted(m['v'].items()) for m in children(p,'MODULE') if val(m,'name')=='ColonyPlacementMarker']) for p in parts)
    return dict(Sha256=hashlib.sha256(data).hexdigest(),VesselId=val(vessels[0],'pid'),Body=val(vessels[0],'REF'),AnchorId=anchor_id,RequestFingerprint=val(status,'RequestFingerprint'),Parts=maps,Anchor=anchors[0],AfterWitness=val(status,'AfterWitness'),PartCount=len(parts))


def main():
    p=argparse.ArgumentParser();p.add_argument('--loaded',required=True);p.add_argument('--unloaded',required=True);p.add_argument('--operation',required=True);p.add_argument('--scene-result',required=True);p.add_argument('--output',required=True);a=p.parse_args()
    native=Path('C:/Users/griff/Documents/Codex/KSP-Colony-Demo').resolve(); workspace=Path(__file__).resolve().parents[2]
    paths=[Path(a.loaded).resolve(),Path(a.unloaded).resolve(),Path(a.scene_result).resolve()];output=Path(a.output).resolve()
    if not all(x.is_relative_to(native) for x in paths) or not output.is_relative_to(workspace/'outputs'):raise ValueError('Actual known private native sources/workspace output required')
    if 'scene=SPACECENTER' not in paths[2].read_text():raise ValueError('Actual stock unloaded scene witness absent')
    before,after=(witness(x,a.operation) for x in paths[:2]); fields=['VesselId','Body','AnchorId','RequestFingerprint','Parts','Anchor','AfterWitness','PartCount']
    equality={k:before[k]==after[k] for k in fields}
    if not all(equality.values()):raise ValueError('Actual loaded/unloaded native authority changed: '+repr(equality))
    report=dict(OperationId=a.operation,LoadedSource=str(paths[0]),LoadedSha256=before['Sha256'],UnloadedSource=str(paths[1]),UnloadedSha256=after['Sha256'],ExactNativeEqualities=equality,VesselId=before['VesselId'],AnchorId=before['AnchorId'],PartCount=before['PartCount'],Authority='Actual native GamePersistence saves plus exact stock SpaceCenter scene witness; identities/anchor fixed pose preserved; no fixture or certification')
    output.parent.mkdir(parents=True,exist_ok=True);output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8');print(json.dumps(report,indent=2))


if __name__=='__main__':main()
