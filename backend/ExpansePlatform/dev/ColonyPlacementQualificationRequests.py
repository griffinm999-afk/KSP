"""Prepare exact reviewable native negative/failure requests in this workspace.

No installation, game or save writes. Each run must start from a separately
qualified native source in a fresh isolated save folder chosen by root.
"""
import argparse
import json
import math
from pathlib import Path
import subprocess
import sys
import uuid


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--world', required=True)
    parser.add_argument('--colony', required=True)
    parser.add_argument('--body', required=True)
    parser.add_argument('--occupied-latitude', type=float, required=True)
    parser.add_argument('--occupied-longitude', type=float, required=True)
    parser.add_argument('--candidate-latitude', type=float, required=True)
    parser.add_argument('--candidate-longitude', type=float, required=True)
    parser.add_argument('--heading', type=float, default=0)
    parser.add_argument('--test-authorization', required=True)
    parser.add_argument('--output-directory', required=True)
    parser.add_argument('--manifest', default='ExpansePlatform/package/GameData/ExpanseWorldBridge/Templates/housing-kpbs-v1.manifest.json')
    args = parser.parse_args()
    workspace = Path(__file__).resolve().parents[2]
    output = Path(args.output_directory).resolve()
    if not output.is_relative_to(workspace) or any(p.lower() == 'saves' for p in output.parts):
        raise ValueError('Review outputs must stay in this workspace outside saves')
    world, colony = str(uuid.UUID(args.world)), str(uuid.UUID(args.colony))
    for lat, lon in [(args.occupied_latitude, args.occupied_longitude), (args.candidate_latitude, args.candidate_longitude)]:
        if not math.isfinite(lat) or not math.isfinite(lon) or not -89.9 <= lat <= 89.9 or not -180 <= lon <= 180:
            raise ValueError('Bounded geodetic coordinates required')
    if not math.isfinite(args.heading) or not 0 <= args.heading < 360:
        raise ValueError('Heading must be finite in [0,360)')
    output.mkdir(parents=True, exist_ok=True)
    writer = workspace / 'ExpansePlatform/dev/ColonyPlacementWriteProbe.py'
    cases = []
    for phase in ['clearance', 'before-assembly', 'after-assembly', 'before-anchor', 'after-anchor']:
        operation, plot = str(uuid.uuid4()), str(uuid.uuid4())
        latitude = args.occupied_latitude if phase == 'clearance' else args.candidate_latitude
        longitude = args.occupied_longitude if phase == 'clearance' else args.candidate_longitude
        path = output / ('colony-placement-request-' + phase + '-' + operation + '.cfg')
        command = [sys.executable, str(writer), '--manifest', args.manifest, '--world', world, '--colony', colony,
                   '--plot', plot, '--operation', operation, '--body', args.body,
                   '--latitude', repr(latitude), '--longitude', repr(longitude), '--heading', repr(args.heading),
                   '--survey', 'native-' + phase + '-qualification-required', '--test-authorization', args.test_authorization,
                   '--output', str(path)]
        completed = subprocess.run(command, cwd=workspace, check=True, capture_output=True, text=True)
        metadata = json.loads(completed.stdout)
        fields = dict(authorization='verified-isolated-development-only', saveFolder='', operationId=operation,
                      saveName='colony-placement-' + phase + '-' + operation,
                      enqueueAuthorization='adapter-probe-only-no-economic-certification',
                      enqueueRequestName=path.name, enqueueRequestSha256=metadata['RequestSha256'])
        if phase == 'clearance':
            fields.update(mode='native-clearance-rejection', expectedHoldAuthorization='clearance-rejection-no-new-spawn')
        else:
            fields.update(mode='native-failure-phase', failureAuthorization='catchable-native-failure-no-recovery', injectPhase=phase)
        cases.append(dict(Phase=phase, Request=metadata, PlotId=plot, Latitude=latitude, Longitude=longitude, WatcherFields=fields,
                          ExpectedMarkedBuildings=0 if phase in ['clearance', 'before-assembly'] else 1,
                          ExpectedAssemblyAttempted=phase not in ['clearance', 'before-assembly'], ExpectedPhysicalFoundation=phase == 'after-anchor'))
    report = dict(Authority='Exact isolated adapter qualification only; no economics, template certification or automatic recovery',
                  OperatorInstructions='Use a new qualified ColonyBuild save folder and unchanged native original for each case. Set saveFolder explicitly, review/copy this exact request to the isolated root, arm these fields, and load via the native development loader. Candidate coordinates still require the actual adapter terrain/clearance checks.',
                  Cases=cases)
    report_path = output / 'colony-placement-qualification-requests.json'
    report_path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(Review=str(report_path), Requests=len(cases), NativeEvidence='pending until each actual run'), indent=2))


if __name__ == '__main__':
    main()
