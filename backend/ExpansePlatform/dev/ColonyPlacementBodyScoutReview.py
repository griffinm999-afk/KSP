"""Read exact native scout evidence; estimates never grant physical clearance."""
import argparse
import hashlib
import json
from pathlib import Path
from ColonyPlacementTemplates import parse, children, val


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--evidence', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    source = Path(args.evidence).resolve()
    native = Path('C:/Users/griff/Documents/Codex/KSP-Colony-Demo').resolve()
    if not source.is_relative_to(native) or not source.name.startswith('colony-placement-body-evidence-'):
        raise ValueError('Exact known isolated native scout file required')
    output = Path(args.output).resolve()
    workspace = Path(__file__).resolve().parents[2]
    if not output.is_relative_to(workspace / 'outputs'):
        raise ValueError('Review output must stay in workspace outputs')
    data = source.read_bytes()
    root = children(parse(data.decode('utf-8-sig')), 'COLONY_BODY_REFERENCE_WITNESS')
    if len(root) != 1 or val(root[0], 'body') not in ['Mun', 'Duna']:
        raise ValueError('Native scout body authority missing')
    rows = []
    for row in children(root[0], 'PQS_ESTIMATED_CANDIDATE'):
        rows.append({key: float(val(row, key)) for key in ['latitude', 'longitude', 'height', 'centerSlopeDegrees',
                                                         'estimatedPlaneResidualVariationMetres', 'estimatedIndependentNormalVariationDegrees']})
    if len(rows) != 49:
        raise ValueError('Expected bounded 49 native estimated candidates')
    gentle = [row for row in rows if .25 <= row['centerSlopeDegrees'] <= 2.5]
    report = dict(Body=val(root[0], 'body'), Source=str(source), SourceSha256=hashlib.sha256(data).hexdigest(),
                  Authority='Actual native PQS estimates only; no loaded collider, footing, plot clearance or template certification',
                  MinimumEstimatedSlope=min(row['centerSlopeDegrees'] for row in rows),
                  MaximumEstimatedSlope=max(row['centerSlopeDegrees'] for row in rows),
                  GentleEstimatedCandidates=sorted(gentle, key=lambda row: row['estimatedPlaneResidualVariationMetres']),
                  UnsuitableEstimatedCandidates=sorted([row for row in rows if row['centerSlopeDegrees'] > 3 or row['estimatedPlaneResidualVariationMetres'] > .25],
                                                       key=lambda row: row['centerSlopeDegrees'], reverse=True),
                  AllCandidates=rows, LoadedQualification='Pending')
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({key: report[key] for key in ['Body', 'MinimumEstimatedSlope', 'MaximumEstimatedSlope']} | {'GentleCount': len(gentle), 'Review': str(output)}, indent=2))


if __name__ == '__main__':
    main()
