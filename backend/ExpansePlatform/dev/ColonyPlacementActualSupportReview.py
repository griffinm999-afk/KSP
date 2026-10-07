"""Read-only fit strictly inside this placement's actual sealed support hull."""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

from ColonyPlacementGridReview import hull, inside
from ColonyPlacementTemplates import children, parse, val


def main():
    p = argparse.ArgumentParser()
    for key in ['support-terrain', 'footing', 'output']:
        p.add_argument('--'+key, required=True)
    a = p.parse_args()
    support_path, footing_path = Path(a.support_terrain), Path(a.footing)
    support = children(parse(support_path.read_text(encoding='utf-8-sig')), 'COLONY_NATIVE_SUPPORT_TERRAIN_WITNESS')[0]
    footing = children(parse(footing_path.read_text(encoding='utf-8-sig')), 'COLONY_NATIVE_FOOTING_WITNESS')[0]
    for key in ['operationId', 'requestFingerprint', 'vesselId', 'body']:
        if val(support, key) != val(footing, key):
            raise ValueError('Exact actual native support/footing lineage mismatch')
    if val(support, 'complete') != 'True':
        raise ValueError('Actual support terrain observation incomplete')
    # ConfigNode.ToString is the product footing seal input, as written here.
    if val(support, 'footingSeal') != 'nativeFooting='+hashlib.sha256(footing_path.read_bytes()).hexdigest():
        raise ValueError('Actual footing seal differs')
    contacts = children(support, 'SEALED_CONTACT_CURRENT_TERRAIN')
    polygon = hull([(float(val(c, 'surfaceX')), float(val(c, 'surfaceZ'))) for c in contacts])
    if len(polygon) < 3:
        raise ValueError('Actual support hull degenerate')
    raw = children(support, 'RAW_LOADED_TERRAIN_DIAGNOSTIC')[0]
    if val(raw, 'complete') != 'True':
        raise ValueError('Actual complete native continuous terrain grid unavailable')
    samples = [[float(val(t, k)) for k in ['x', 'z', 'heightAboveCenterPlane', 'independentSlopeDegrees']]
               for t in children(raw, 'TERRAIN') if inside((float(val(t, 'x')), float(val(t, 'z'))), polygon)]
    points = np.array(samples)
    if len(points) < 9 or not np.isfinite(points).all():
        raise ValueError('Insufficient actual under-support native samples')
    design = np.c_[points[:, 0], points[:, 1], np.ones(len(points))]
    plane, _, rank, _ = np.linalg.lstsq(design, points[:, 2], rcond=None)
    if rank != 3:
        raise ValueError('Actual under-support fit degenerate')
    residual = points[:, 2] - design @ plane
    normals = sorted(set(val(t, 'independentNormal') for t in children(raw, 'TERRAIN')
                         if inside((float(val(t, 'x')), float(val(t, 'z'))), polygon)))
    result = dict(Authority='Actual sealed native contact hull, current anchored root and exact continuous PQS rays. This numerical observation alone is not package/economic/runtime certification.',
                  OperationId=val(support, 'operationId'), VesselId=val(support, 'vesselId'), Body=val(support, 'body'),
                  FootingSeal=val(support, 'footingSeal'), Cold=val(support, 'cold'), ObservedUt=val(support, 'observedUt'),
                  Evidence={str(q.resolve()): hashlib.sha256(q.read_bytes()).hexdigest() for q in [support_path, footing_path]},
                  ActualContactCount=len(contacts), SupportHull=polygon, ActualUnderSupportSampleCount=len(points),
                  BestFitPlaneCoefficients=plane.tolist(), BestFitResidualRangeMetres=float(np.ptp(residual)),
                  BestFitResidualRmsMetres=float(np.sqrt(np.mean(residual**2))),
                  ActualUnderSupportRawHeightRangeMetres=float(np.ptp(points[:, 2])),
                  ActualUnderSupportMaximumSlopeDegrees=float(points[:, 3].max()), IndependentNormals=normals,
                  MaximumTerrainToSealedContactGapMetres=max(abs(float(val(c, 'currentTerrainToSealedContactGapMetres'))) for c in contacts),
                  ActualSupportPolygonAreaSquareMetres=float(val(footing, 'supportPolygonAreaSquareMetres')),
                  ActualGravityProjectedCentreOfMassInteriorMarginMetres=float(val(footing, 'actualCentreOfMassSupportMarginMetres')),
                  Samples=samples, RuntimeCertified=False)
    output = Path(a.output).resolve()
    if not output.is_relative_to(Path(__file__).resolve().parents[2] / 'outputs'):
        raise ValueError('Review output leaves workspace evidence')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2)+'\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k not in ['Samples', 'IndependentNormals', 'SupportHull']}, indent=2))


if __name__ == '__main__':
    main()
