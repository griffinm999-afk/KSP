"""Read-only native terrain fit inside separately measured hardware bounds.

The reservation and hypothetical candidate fit do not certify actual contacts.
No terrain, craft, certificate, save or native state is changed.
"""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

from ColonyPlacementTemplates import children, parse, val


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--evidence', required=True)
    p.add_argument('--row-template', required=True)
    p.add_argument('--hardware-bounds', required=True, nargs=4, type=float,
                   metavar=('MINX', 'MAXX', 'MINZ', 'MAXZ'))
    p.add_argument('--bounds-provenance', required=True)
    p.add_argument('--output', required=True)
    a = p.parse_args()
    evidence, provenance = Path(a.evidence), Path(a.bounds_provenance)
    root = children(parse(evidence.read_text(encoding='utf-8-sig')),
                    'COLONY_BODY_REFERENCE_WITNESS')[0]
    row = [x for x in children(root, 'ACTUAL_LOADED_SURVEY')
           if val(x, 'templateId') == a.row_template]
    if len(row) != 1:
        raise ValueError('One exact native surveyed row required')
    row = row[0]
    raw = children(row, 'RAW_LOADED_TERRAIN_DIAGNOSTIC')[0]
    if val(raw, 'complete') != 'True':
        raise ValueError('Incomplete actual terrain cannot qualify the fit')
    minimum_x, maximum_x, minimum_z, maximum_z = a.hardware_bounds
    if minimum_x >= maximum_x or minimum_z >= maximum_z:
        raise ValueError('Finite positive measured hardware bounds required')
    samples = []
    for point in children(raw, 'TERRAIN'):
        x, z = float(val(point, 'x')), float(val(point, 'z'))
        if minimum_x <= x <= maximum_x and minimum_z <= z <= maximum_z:
            samples.append([x, z, float(val(point, 'heightAboveCenterPlane')),
                            float(val(point, 'independentSlopeDegrees'))])
    points = np.array(samples)
    if len(points) < 9 or not np.isfinite(points).all():
        raise ValueError('At least9 finite spatially distributed native samples required')
    design = np.c_[points[:, 0], points[:, 1], np.ones(len(points))]
    plane, _, rank, _ = np.linalg.lstsq(design, points[:, 2], rcond=None)
    if rank != 3:
        raise ValueError('Native sample design cannot fit a plane')
    residual = points[:, 2] - design @ plane
    result = dict(Authority='Read-only candidate fit inside previously measured hardware bounds; actual new deployed contact/support qualification still required',
                  EvidencePath=str(evidence.resolve()), EvidenceSha256=hashlib.sha256(evidence.read_bytes()).hexdigest(),
                  BoundsProvenance=str(provenance.resolve()), BoundsProvenanceSha256=hashlib.sha256(provenance.read_bytes()).hexdigest(),
                  OperationId=val(root, 'operationId'), Body=val(root, 'body'),
                  RowTemplate=val(row, 'templateId'), Latitude=float(val(row, 'latitude')),
                  Longitude=float(val(row, 'longitude')), OriginalSurveyClear=val(row, 'clear'),
                  OriginalSurveyReason=val(row, 'reason'), HardwareBounds=a.hardware_bounds,
                  NativeSampleCount=len(points), BestFitPlaneCoefficients=plane.tolist(),
                  BestFitResidualRangeMetres=float(np.ptp(residual)),
                  BestFitResidualRmsMetres=float(np.sqrt(np.mean(residual**2))),
                  MaximumIndependentSlopeDegrees=float(points[:, 3].max()),
                  RawCenterPlaneHeightRangeMetres=float(np.ptp(points[:, 2])),
                  Samples=samples, NativeContactsObserved=False, RuntimeCertified=False)
    output = Path(a.output).resolve()
    workspace = Path(__file__).resolve().parents[2]
    if not output.is_relative_to(workspace / 'outputs'):
        raise ValueError('Review output must remain in workspace outputs')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in result.items() if k != 'Samples'}, indent=2))


if __name__ == '__main__':
    main()
