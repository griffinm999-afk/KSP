"""Review exact native held collider/frame diagnostics without native writes."""
import argparse
import hashlib
import json
from pathlib import Path
from ColonyPlacementTemplates import parse, children, val


def vector(text):
    return [float(x) for x in text.strip('()').split(',')]


def main():
    p = argparse.ArgumentParser(); p.add_argument('--evidence', required=True); p.add_argument('--output', required=True)
    a = p.parse_args(); source = Path(a.evidence).resolve(); output = Path(a.output).resolve()
    native = Path('C:/Users/griff/Documents/Codex/KSP-Colony-Demo').resolve()
    workspace = Path(__file__).resolve().parents[2]
    if not source.is_relative_to(native) or not source.name.startswith('colony-placement-body-evidence-') or not output.is_relative_to(workspace / 'outputs'):
        raise ValueError('Exact isolated native diagnostic and workspace output required')
    data = source.read_bytes(); roots = children(parse(data.decode('utf-8-sig')), 'COLONY_BODY_REFERENCE_WITNESS')
    if len(roots) != 1 or not val(roots[0], 'targetOperationId'):
        raise ValueError('Actual existing native operation diagnostic required')
    root = roots[0]; report = dict(Source=str(source), SourceSha256=hashlib.sha256(data).hexdigest(),
        TargetOperationId=val(root,'targetOperationId'), VesselId=val(root,'vesselId'), Body=val(root,'body'),
        OldFrameAngleErrorDegrees=float(val(root,'oldNorthSameHeightAngleErrorDegrees')),
        InstalledSettledEnvelope=val(root,'installedSettledEnvelope'), InstalledDeclaredDeploymentClearance=val(root,'installedDeclaredDeploymentClearance'),
        Authority='Read-only actual loaded held geometry; no recovery, deployment, anchor, economy or certification')
    colliders = children(root,'ACTUAL_COLLIDER'); summary = {}
    for frame in ['sameAltitudeFrame','oldAltitudeZeroFrame']:
        rows = [children(c,frame)[0] for c in colliders]
        summary[frame] = dict(ColliderCount=len(rows),
            ShapeOutside=[dict(Name=val(c,'name'),Owner=val(c,'ownerPersistentId'),Minimum=val(r,'shapeMinimum'),Maximum=val(r,'shapeMaximum'))
                for c,r in zip(colliders,rows) if val(r,'shapeWithinDeclared02m') != 'True'],
            WorldAabbOutside=[val(c,'name') for c,r in zip(colliders,rows) if val(r,'worldAabbWithinDeclared02m') != 'True'],
            ShapeMinimum=[min(vector(val(r,'shapeMinimum'))[axis] for r in rows) for axis in range(3)],
            ShapeMaximum=[max(vector(val(r,'shapeMaximum'))[axis] for r in rows) for axis in range(3)])
    report['MeasuredFrames']=summary; output.parent.mkdir(parents=True,exist_ok=True)
    output.write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8'); print(json.dumps(report,indent=2))


if __name__ == '__main__': main()
