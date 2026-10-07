"""Prepare eight exact native product probes; no installation or save writes.

The installed candidates remain uncertified. Final plots require the real
adapter's loaded terrain, geometry, stability and hardware checks.
"""
import argparse
import hashlib
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
    parser.add_argument('--test-authorization', required=True)
    parser.add_argument('--template-root', required=True)
    parser.add_argument('--output-directory', required=True)
    parser.add_argument('--cold-source-name')
    parser.add_argument('--cold-source-sha256')
    parser.add_argument('--cold-prior-process', type=int)
    parser.add_argument('--cold-existing-review')
    parser.add_argument('--body', choices=['Minmus', 'Mun', 'Duna'], default='Minmus')
    parser.add_argument('--body-radius', type=float)
    parser.add_argument('--reference-latitude', type=float)
    parser.add_argument('--reference-longitude', type=float)
    args = parser.parse_args()
    workspace = Path(__file__).resolve().parents[2]
    output = Path(args.output_directory).resolve()
    if not output.is_relative_to(workspace) or any(p.lower() == 'saves' for p in output.parts):
        raise ValueError('Review output must stay in workspace outside native saves')
    output.mkdir(parents=True, exist_ok=True)
    if args.cold_existing_review:
        prepare_cold(args, output)
        return
    template_root = Path(args.template_root).resolve()
    if args.body != 'Minmus':
        if not args.body_radius or args.body_radius < 1000 or args.reference_latitude is None or args.reference_longitude is None or abs(args.reference_latitude) >= 80 or not -180 <= args.reference_longitude <= 180:
            raise ValueError('Body street requires measured native radius and exact reference coordinates outside polar bounds')
        radius = args.body_radius
        reference_latitude, reference_longitude = args.reference_latitude, args.reference_longitude
    else:
        radius, reference_latitude, reference_longitude = 60000, 3.0001389310000124, 3.0011102545953903
    packages = ['housing-kpbs-v1', 'service-kpbs-v1', 'storage-kpbs-v1', 'lamp-stock-v1',
                'power-duna-v1', 'wolf-hoppers-v1', 'fertilizer-tundra-v1', 'agriculture-duna-v1']
    cases, config_cases = [], []
    for index, package in enumerate(packages):
        operation, plot = str(uuid.uuid4()), str(uuid.uuid4())
        manifest_path = template_root / (package + '.manifest.json')
        manifest_bytes = manifest_path.read_bytes()
        manifest = json.loads(manifest_bytes)
        if manifest['Id'] != package or manifest['RuntimeCertified']:
            raise ValueError('Exact uncertified installed candidate required')
        if hashlib.sha256((template_root / manifest['CraftRelativePath']).read_bytes()).hexdigest() != manifest['CraftSha256']:
            raise ValueError('Installed actual craft hash differs from candidate manifest')
        if args.body == 'Minmus':
            latitude, longitude = 3.08 + .06 * (index // 4), 2.90 + .05 * (index % 4)
        else:
            east, north = (index % 4 - 1.5) * 45, (2 * (index // 4) - 1) * 35
            latitude = reference_latitude + math.degrees(north / radius)
            longitude = (reference_longitude + math.degrees(east / (radius * math.cos(math.radians(reference_latitude)))) + 540) % 360 - 180
        request_path = output / ('colony-placement-request-package-' + operation + '.cfg')
        command = [sys.executable, str(workspace / 'ExpansePlatform/dev/ColonyPlacementWriteProbe.py'),
                   '--manifest', str(manifest_path), '--world', str(uuid.UUID(args.world)),
                   '--colony', str(uuid.UUID(args.colony)), '--plot', plot, '--operation', operation,
                   '--body', args.body, '--latitude', repr(latitude), '--longitude', repr(longitude),
                   '--heading', '0', '--survey', 'native-package-footprint-qualification-required',
                   '--test-authorization', args.test_authorization, '--output', str(request_path)]
        completed = subprocess.run(command, cwd=workspace, check=True, capture_output=True, text=True)
        metadata = json.loads(completed.stdout)
        save_name = 'colony-placement-package-' + package + '-' + operation
        fields = dict(operationId=operation, saveName=save_name, enqueueRequestName=request_path.name,
                      enqueueRequestSha256=metadata['RequestSha256'])
        config_cases.append(fields)
        cases.append(dict(PackageId=package, PlotId=plot, Latitude=latitude, Longitude=longitude,
                          Request=metadata, Fields=fields, ManifestSha256=hashlib.sha256(manifest_bytes).hexdigest(),
                          MinX=manifest['MinX'], MaxX=manifest['MaxX'], MinZ=manifest['MinZ'], MaxZ=manifest['MaxZ'],
                          Clearance=manifest['ClearanceMetres']))
    # Conservative great-circle lower bound for these near-equatorial parallel
    # rows, evaluated against the complete declared envelopes + access margins.
    clearances = []
    maximum_latitude = max(abs(c['Latitude']) for c in cases) + .001
    for i, left in enumerate(cases):
        for right in cases[i + 1:]:
            dx = radius * math.radians(abs(left['Longitude'] - right['Longitude'])) * math.cos(math.radians(maximum_latitude))
            dz = radius * math.radians(abs(left['Latitude'] - right['Latitude']))
            edge_x = dx - max(abs(left['MinX']), abs(left['MaxX'])) - max(abs(right['MinX']), abs(right['MaxX'])) - left['Clearance'] - right['Clearance']
            edge_z = dz - max(abs(left['MinZ']), abs(left['MaxZ'])) - max(abs(right['MinZ']), abs(right['MaxZ'])) - left['Clearance'] - right['Clearance']
            lower = max(edge_x, edge_z)
            if lower < 8:
                raise ValueError('Declared deployed access envelopes leave less than 8 m between packages')
            clearances.append(dict(Left=left['PackageId'], Right=right['PackageId'], ConservativeClearGapMeters=lower))
    for candidate in cases:
        dx = radius * math.radians(candidate['Longitude'] - reference_longitude) * math.cos(math.radians(maximum_latitude))
        dz = radius * math.radians(candidate['Latitude'] - reference_latitude)
        envelope_radius = math.hypot(max(abs(candidate['MinX']), abs(candidate['MaxX'])) + candidate['Clearance'],
                            max(abs(candidate['MinZ']), abs(candidate['MaxZ'])) + candidate['Clearance'])
        if math.hypot(dx, dz) + envelope_radius + 5 >= 200:
            raise ValueError('Complete candidate envelope is outside current native stock200m surface unpack window')
    batch_name = 'colony-placement-batch-' + str(uuid.uuid4()) + '.cfg'
    lines = ['BATCH', '{']
    for fields in config_cases:
        lines += ['\tCASE', '\t{'] + ['\t\t' + name + ' = ' + value for name, value in fields.items()] + ['\t}']
    data = ('\n'.join(lines + ['}']) + '\n').encode('utf-8')
    batch_path = output / batch_name
    batch_path.write_bytes(data)
    unloaded_name = 'colony-test-packages-unloaded-' + str(uuid.uuid4())
    watcher = dict(authorization='verified-isolated-development-only', saveFolder='', operationId=cases[0]['Fields']['operationId'],
                   saveName=cases[-1]['Fields']['saveName'], mode='native-package-batch',
                   batchAuthorization='eight-exact-package-probes-no-economic-certification',
                   batchRequestName=batch_name, batchRequestSha256=hashlib.sha256(data).hexdigest(),
                   unloadedAuthorization='stock-scene-exit-native-proto-readback', unloadedSaveName=unloaded_name)
    report = dict(Authority='Eight exact isolated product probes only; no economy or runtime certification',
                  Body=args.body, BodyRadiusMetres=radius, ReferenceLatitude=reference_latitude, ReferenceLongitude=reference_longitude,
                  BatchPath=str(batch_path), BatchSha256=hashlib.sha256(data).hexdigest(), WatcherFields=watcher,
                  Cases=cases, DeclaredAccessEnvelopeClearances=clearances,
                  FinalNativeSaveName=cases[-1]['Fields']['saveName'], UnloadedNativeSaveName=unloaded_name,
                  NativeEvidence='Pending actual loaded geometry, stock scene exit, native unloaded save and cold process reload')
    path = output / 'colony-placement-package-batch.json'
    path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(Review=str(path), BatchSha256=report['BatchSha256'], Packages=8,
                         MinimumDeclaredAccessGapMeters=min(r['ConservativeClearGapMeters'] for r in clearances)), indent=2))


def prepare_cold(args, output):
    original = json.loads(Path(args.cold_existing_review).read_text(encoding='utf-8'))
    if len(original['Cases']) != 8 or not args.cold_source_name or not args.cold_source_sha256 or not args.cold_prior_process:
        raise ValueError('Eight original witnessed operations and exact independent native source/process required')
    if len(args.cold_source_sha256) != 64 or any(c not in '0123456789abcdef' for c in args.cold_source_sha256.lower()):
        raise ValueError('Exact source SHA256 required')
    cases, lines = [], ['BATCH', '{']
    for original_case in original['Cases']:
        operation = original_case['Fields']['operationId']
        fields = dict(operationId=operation, saveName='colony-placement-package-reload-' + operation,
                      reloadSourceName=args.cold_source_name, reloadSourceSha256=args.cold_source_sha256.lower(),
                      reloadPriorProcessId=str(args.cold_prior_process))
        lines += ['\tCASE', '\t{'] + ['\t\t' + name + ' = ' + value for name, value in fields.items()] + ['\t}']
        cases.append(dict(PackageId=original_case['PackageId'], Fields=fields, Request=original_case['Request']))
    data = ('\n'.join(lines + ['}']) + '\n').encode('utf-8')
    batch_name = 'colony-placement-batch-' + str(uuid.uuid4()) + '.cfg'
    batch_path = output / batch_name
    batch_path.write_bytes(data)
    unloaded_name = 'colony-test-packages-cold-unloaded-' + str(uuid.uuid4())
    watcher = dict(authorization='verified-isolated-development-only', saveFolder='', operationId=cases[0]['Fields']['operationId'],
                   saveName=cases[-1]['Fields']['saveName'], mode='cold-package-batch',
                   batchAuthorization='eight-exact-package-probes-no-economic-certification',
                   batchRequestName=batch_name, batchRequestSha256=hashlib.sha256(data).hexdigest(),
                   unloadedAuthorization='stock-scene-exit-native-proto-readback', unloadedSaveName=unloaded_name)
    report = dict(Authority='Eight original actual native cold reloads; no new enqueue or assembly',
                  BatchPath=str(batch_path), BatchSha256=hashlib.sha256(data).hexdigest(), WatcherFields=watcher, Cases=cases,
                  ColdSourceName=args.cold_source_name, ColdSourceSha256=args.cold_source_sha256.lower(),
                  PriorProcessId=args.cold_prior_process, OriginalReview=str(Path(args.cold_existing_review).resolve()),
                  FinalNativeSaveName=cases[-1]['Fields']['saveName'], UnloadedNativeSaveName=unloaded_name,
                  NativeEvidence='Pending independently loaded original vessel/part/marker/Foundation readback')
    path = output / 'colony-placement-package-cold-batch.json'
    path.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(dict(Review=str(path), BatchSha256=report['BatchSha256'], OriginalOperations=8), indent=2))


if __name__ == '__main__':
    main()
