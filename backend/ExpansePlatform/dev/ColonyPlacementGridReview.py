"""Read-only review of bounded native site-grid evidence, never a certificate."""
import argparse
import collections
import hashlib
import json
from pathlib import Path

import numpy as np

from ColonyPlacementTemplates import children, parse, rotate, val, vec


def cross(a, b, c):
    return (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0])


def hull(points):
    points = sorted(set(map(tuple, points)))
    lower, upper = [], []
    for p in points:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(points):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]


def inside(p, polygon):
    return all(cross(polygon[i], polygon[(i+1) % len(polygon)], p) >= -1e-7
               for i in range(len(polygon)))


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--evidence', required=True)
    p.add_argument('--support-provenance', required=True)
    p.add_argument('--template-manifest', required=True)
    p.add_argument('--output', required=True)
    p.add_argument('--review-timing', required=True,
                   choices=['observed-before-explicit-source-review', 'observed-after-explicit-source-review'])
    a = p.parse_args()
    paths = list(map(Path, [a.evidence, a.support_provenance, a.template_manifest]))
    root = children(parse(paths[0].read_text(encoding='utf-8-sig')), 'COLONY_BODY_REFERENCE_WITNESS')[0]
    support = children(parse(paths[1].read_text(encoding='utf-8-sig')), 'COLONY_NATIVE_FOOTING_WITNESS')[0]
    manifest = json.loads(paths[2].read_text(encoding='utf-8-sig'))
    q = [manifest['TemplateRotation'+axis] for axis in 'XYZW']
    polygon = hull([(v[0], v[2]) for v in
                    [rotate(q, vec(val(n, 'rootLocalPosition'))) for n in children(support, 'CONTACT')]])
    if len(polygon) < 3:
        raise ValueError('Actual prior support polygon missing')
    rows, species = [], collections.defaultdict(list)
    for native in children(root, 'ACTUAL_LOADED_SURVEY'):
        if val(native, 'templateId') != manifest['Id'] or val(native, 'templateHash') != manifest['Hash']:
            raise ValueError('Exact surveyed template identity mismatch')
        row = {k: val(native, k) for k in ['latitude', 'longitude', 'clear', 'reason', 'contextKey', 'surfaceCollisionWitness']}
        for k in ['actualMaximumSlopeDegrees', 'actualSupportGapMetres']:
            row[k] = float(val(native, k))
        collision = children(native, 'NATIVE_LATENT_COLLIDER_DIAGNOSTIC')[0]
        row['ColliderDiagnosticComplete'] = val(collision, 'complete') == 'True'
        row['ProductReady'] = val(collision, 'productReady') == 'True'
        row['ProductClear'] = val(collision, 'productClear') == 'True'
        row['ColliderDiagnosticReason'] = val(collision, 'reason')
        row['IntersectingColliderCount'] = len(children(collision, 'INTERSECTING_NATIVE_COLLIDER'))
        row['ObservedPositions'] = val(collision, 'observedPositions')
        row['LargestConfiguredColliderRadiusMetres'] = val(collision, 'largestConfiguredColliderRadiusMetres')
        row['EnvelopeMinInSurfaceFrame'] = val(collision, 'envelopeMinInSurfaceFrame')
        row['EnvelopeMaxInSurfaceFrame'] = val(collision, 'envelopeMaxInSurfaceFrame')
        for rock in children(collision, 'INTERSECTING_NATIVE_COLLIDER'):
            species[val(rock, 'species')].append({k: val(rock, k) for k in rock['v']})
        raw = children(native, 'RAW_LOADED_TERRAIN_DIAGNOSTIC')[0]
        points = np.array([[float(val(n, k)) for k in ['x', 'z', 'heightAboveCenterPlane', 'independentSlopeDegrees']]
                           for n in children(raw, 'TERRAIN') if inside((float(val(n, 'x')), float(val(n, 'z'))), polygon)])
        if val(raw, 'complete') != 'True' or len(points) < 9 or not np.isfinite(points).all():
            row['CandidateSupportFitComplete'] = False
        else:
            design = np.c_[points[:, 0], points[:, 1], np.ones(len(points))]
            plane, _, rank, _ = np.linalg.lstsq(design, points[:, 2], rcond=None)
            if rank != 3:
                raise ValueError('Degenerate native fit')
            residual = points[:, 2] - design @ plane
            row.update(CandidateSupportFitComplete=True, CandidateSupportSampleCount=len(points),
                       CandidateSupportResidualRangeMetres=float(np.ptp(residual)),
                       CandidateSupportResidualRmsMetres=float(np.sqrt(np.mean(residual**2))),
                       CandidateSupportMaximumSlopeDegrees=float(points[:, 3].max()),
                       CandidateSupportRawHeightRangeMetres=float(np.ptp(points[:, 2])))
        rows.append(row)
    if len(rows) != 49:
        raise ValueError('Exactly49 native rows required')
    result = dict(Authority='Read-only candidate review. Product ready/clear is mandatory; prior contact polygon only filters candidates and never qualifies new physical supports.',
                  ReviewTiming=a.review_timing, OperationId=val(root, 'operationId'), Body=val(root, 'body'),
                  Evidence={str(path.resolve()): hashlib.sha256(path.read_bytes()).hexdigest() for path in paths},
                  SupportProvenanceOperationId=val(support, 'operationId'), PriorMeasuredSupportPolygon=polygon,
                  NativeContactsObservedHere=False, RuntimeCertified=False, Rows=rows,
                  ClearCount=sum(r['clear'] == 'True' and r['ProductReady'] and r['ProductClear'] for r in rows),
                  CompleteColliderDiagnostics=sum(r['ColliderDiagnosticComplete'] for r in rows),
                  IntersectionsBySpecies={name: len(v) for name, v in species.items()},
                  IntersectingNativeColliders=species)
    output = Path(a.output).resolve()
    if not output.is_relative_to(Path(__file__).resolve().parents[2] / 'outputs'):
        raise ValueError('Output leaves workspace evidence')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k not in ['Rows', 'IntersectingNativeColliders']}, indent=2))
    print(json.dumps(sorted(rows, key=lambda r: (not (r['clear'] == 'True'), r['IntersectingColliderCount'], -r.get('CandidateSupportResidualRangeMetres', 0)))[:8], indent=2))


if __name__ == '__main__':
    main()
