"""Read-only plane residuals inside a prior measured support polygon."""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

from ColonyPlacementGridReview import hull, inside
from ColonyPlacementTemplates import children, parse, rotate, val, vec


def main():
    p = argparse.ArgumentParser()
    for key in ['evidence', 'support-provenance', 'template-manifest', 'output']:
        p.add_argument('--'+key, required=True)
    a = p.parse_args()
    files = list(map(Path, [a.evidence, a.support_provenance, a.template_manifest]))
    root = children(parse(files[0].read_text(encoding='utf-8-sig')), 'COLONY_BODY_REFERENCE_WITNESS')[0]
    support = children(parse(files[1].read_text(encoding='utf-8-sig')), 'COLONY_NATIVE_FOOTING_WITNESS')[0]
    manifest = json.loads(files[2].read_text(encoding='utf-8-sig'))
    heading = float(val(root, 'headingDegrees') or '0')
    q = [manifest['TemplateRotation'+axis] for axis in 'XYZW']
    polygon = hull([(p[0], p[2]) for p in [rotate(q, vec(val(n, 'rootLocalPosition'))) for n in children(support, 'CONTACT')]])
    rows = []
    for native in children(root, 'ACTUAL_NATIVE_EDGE_SURVEY'):
        if val(native, 'templateId') != manifest['Id'] or val(native, 'templateHash') != manifest['Hash']:
            raise ValueError('Exact candidate manifest required')
        raw = children(native, 'RAW_LOADED_TERRAIN_DIAGNOSTIC')[0]
        if float(val(native, 'headingDegrees') or '0') != heading or float(val(raw, 'headingDegrees') or '0') != heading:
            raise ValueError('Exact candidate survey and terrain heading differ')
        if val(raw, 'complete') != 'True':
            raise ValueError('Incomplete native terrain fit')
        points = np.array([[float(val(n, k)) for k in ['x', 'z', 'heightAboveCenterPlane', 'independentSlopeDegrees']]
                           for n in children(raw, 'TERRAIN') if inside((float(val(n, 'x')), float(val(n, 'z'))), polygon)])
        if len(points) < 9 or not np.isfinite(points).all():
            raise ValueError('Insufficient finite native support candidates')
        design = np.c_[points[:, 0], points[:, 1], np.ones(len(points))]
        plane, _, rank, _ = np.linalg.lstsq(design, points[:, 2], rcond=None)
        if rank != 3:
            raise ValueError('Degenerate candidate fit')
        residual = points[:, 2] - design @ plane
        row = {k: val(native, k) for k in native['v']}
        row.update(CandidateSupportSampleCount=len(points), CandidateSupportPlaneResidualRangeMetres=float(np.ptp(residual)),
                   CandidateSupportPlaneResidualRmsMetres=float(np.sqrt(np.mean(residual**2))),
                   CandidateSupportRawHeightRangeMetres=float(np.ptp(points[:, 2])),
                   CandidateSupportMaximumSlopeDegrees=float(points[:, 3].max()), CandidateSupportSamples=points.tolist())
        rows.append(row)
    if len(rows) > 48:
        raise ValueError('Native edge candidate count exceeds48')
    result = dict(Authority='Candidate fit only inside previous native support hull. Product survey clearance is mandatory; actual new physical contacts and support geometry still unobserved.',
                  OperationId=val(root, 'operationId'), NativeQuadCount=val(root, 'nativeQuadCount'), NativeTrianglesObserved=val(root, 'nativeTrianglesObserved'),
                  HeadingDegrees=heading,
                  CandidateInteriorEdgeCount=val(root, 'candidateInteriorEdgeCount'), MissingNativeSeeds=len(children(root, 'INCOMPLETE_NATIVE_QUAD_SEED')),
                  NativeNewContactsObserved=False, RuntimeCertified=False,
                  Evidence={str(p.resolve()): hashlib.sha256(p.read_bytes()).hexdigest() for p in files},
                  PriorActualSupportOperation=val(support, 'operationId'), PriorActualSupportPolygon=polygon,
                  Rows=sorted(rows, key=lambda r: (r['clear'] != 'True', -r['CandidateSupportPlaneResidualRangeMetres'])))
    output = Path(a.output).resolve()
    if not output.is_relative_to(Path(__file__).resolve().parents[2] / 'outputs'):
        raise ValueError('Output leaves workspace evidence')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps([{k: v for k, v in r.items() if k not in ['CandidateSupportSamples']} for r in result['Rows'][:4]], indent=2))


if __name__ == '__main__':
    main()
