"""Prepare one hash-bound isolated product Enqueue probe; no native writes."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import subprocess
import sys
import uuid


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--manifest', required=True)
    p.add_argument('--world', required=True)
    p.add_argument('--colony', required=True)
    p.add_argument('--body', choices=['Minmus', 'Mun', 'Duna'], required=True)
    p.add_argument('--latitude', type=float, required=True)
    p.add_argument('--longitude', type=float, required=True)
    p.add_argument('--heading', type=float, default=0)
    p.add_argument('--survey', required=True)
    p.add_argument('--test-authorization', required=True)
    p.add_argument('--output-directory', required=True)
    p.add_argument('--expected-terrain-rejection', choices=['slope', 'support-gap', 'latent-scatter'])
    a = p.parse_args()
    if not math.isfinite(a.heading) or not 0 <= a.heading < 360:
        raise ValueError('Finite placement heading0..360 required')
    workspace = Path(__file__).resolve().parents[2]
    output = Path(a.output_directory).resolve()
    if not output.is_relative_to(workspace) or any(x.lower() == 'saves' for x in output.parts):
        raise ValueError('Review output must stay in workspace, outside saves')
    manifest_path = Path(a.manifest).resolve()
    manifest_bytes = manifest_path.read_bytes()
    m = json.loads(manifest_bytes)
    if m['RuntimeCertified'] or hashlib.sha256((manifest_path.parent / m['CraftRelativePath']).read_bytes()).hexdigest() != m['CraftSha256']:
        raise ValueError('Exact uncertified candidate craft required')
    output.mkdir(parents=True, exist_ok=True)
    operation, plot = str(uuid.uuid4()), str(uuid.uuid4())
    request = output / ('colony-placement-request-body-' + operation + '.cfg')
    cmd = [sys.executable, str(workspace / 'ExpansePlatform/dev/ColonyPlacementWriteProbe.py'),
           '--manifest', str(manifest_path), '--world', str(uuid.UUID(a.world)),
           '--colony', str(uuid.UUID(a.colony)), '--plot', plot, '--operation', operation,
           '--body', a.body, '--latitude', repr(a.latitude), '--longitude', repr(a.longitude), '--heading', repr(a.heading),
           '--survey', a.survey, '--test-authorization', a.test_authorization, '--output', str(request)]
    metadata = json.loads(subprocess.run(cmd, cwd=workspace, check=True, capture_output=True, text=True).stdout)
    fields = dict(operationId=operation, saveName='colony-placement-body-' + a.body.lower() + '-' + operation,
                  enqueueRequestName=request.name, enqueueRequestSha256=metadata['RequestSha256'])
    watcher = dict(authorization='verified-isolated-development-only', saveFolder='',
                   mode='native-placement', enqueueAuthorization='adapter-probe-only-no-economic-certification', geometryAuthorization='actual-native-shape-sweep02m', **fields)
    if a.expected_terrain_rejection:
        watcher.update(mode='native-terrain-rejection', expectedHoldAuthorization='terrain-rejection-before-assembly-no-new-spawn', terrainRejectKind=a.expected_terrain_rejection)
    review = dict(Authority='One exact isolated product probe; no economy or runtime certification',
                  Body=a.body, SurveyWitness=a.survey, WatcherFields=watcher,
                  Cases=[dict(PackageId=m['Id'], PlotId=plot, Latitude=a.latitude, Longitude=a.longitude,
                              HeadingDegrees=a.heading, Request=metadata, Fields=fields, ManifestSha256=hashlib.sha256(manifest_bytes).hexdigest())])
    path = output / 'colony-placement-single-probe.json'
    path.write_text(json.dumps(review, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(Review=str(path), OperationId=operation, RequestSha256=metadata['RequestSha256']), indent=2))


if __name__ == '__main__':
    main()
