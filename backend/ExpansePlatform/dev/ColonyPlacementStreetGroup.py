"""Review five real product street requests from actual loaded surveys; no native writes."""
import argparse
import hashlib
import json
import math
from pathlib import Path
import subprocess
import sys
import uuid
from ColonyPlacementTemplates import parse, children, val
from ColonyPlacementUnloadedReview import witness

PACKAGES = ['housing-kpbs-v1', 'service-kpbs-v1', 'storage-kpbs-v1', 'lamp-stock-v1', 'power-duna-v1']
NATIVE = Path('C:/Users/griff/Documents/Codex/KSP-Colony-Demo').resolve()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--plots', help='Workspace JSON array of five exact {PackageId, SurveyFile} rows')
    p.add_argument('--template-root', required=True)
    p.add_argument('--world', required=True)
    p.add_argument('--colony', required=True)
    p.add_argument('--source', required=True)
    p.add_argument('--source-sha256', required=True)
    p.add_argument('--prior-process', type=int, required=True)
    p.add_argument('--existing-housing-operation')
    p.add_argument('--cold-existing-review')
    p.add_argument('--reference-latitude', type=float, required=True)
    p.add_argument('--reference-longitude', type=float, required=True)
    p.add_argument('--output-directory', required=True)
    a = p.parse_args()
    workspace = Path(__file__).resolve().parents[2]
    output = Path(a.output_directory).resolve()
    source = Path(a.source).resolve()
    if not output.is_relative_to(workspace / 'outputs') or not source.is_relative_to(NATIVE / 'saves') or not source.parent.name.startswith('ColonyBuild-') or source.suffix != '.sfs':
        raise ValueError('Exact private native source and workspace review output required')
    if sha(source) != a.source_sha256.lower() or a.prior_process <= 0:
        raise ValueError('Actual native source hash and independently witnessed prior process required')
    output.mkdir(parents=True, exist_ok=True)
    source_name = source.stem
    cases = []
    body = None
    if a.cold_existing_review:
        original = json.loads(Path(a.cold_existing_review).read_text('utf-8-sig'))
        if [c['PackageId'] for c in original['Cases']] != PACKAGES or original['Body'] not in ['Mun', 'Duna']:
            raise ValueError('Original exact five-package native street review required')
        body = original['Body']
        for entry in original['Cases']:
            operation = str(uuid.UUID(entry['Fields']['operationId']))
            witness(source, operation)  # Requires actual one-vessel anchored receipt and full native map.
            fields = dict(operationId=operation, saveName='colony-placement-street-cold-' + operation,
                          reloadSourceName=source_name, reloadSourceSha256=a.source_sha256.lower(), reloadPriorProcessId=str(a.prior_process))
            cases.append(dict(entry, Fields=fields))
    else:
        rows = json.loads(Path(a.plots).read_text('utf-8-sig'))
        if [r['PackageId'] for r in rows] != PACKAGES:
            raise ValueError('Exactly the ordered five independent loaded survey rows required')
        for index, row in enumerate(rows):
            survey_path = Path(row['SurveyFile']).resolve()
            if not survey_path.is_relative_to(NATIVE) and not survey_path.is_relative_to(workspace / 'outputs'):
                raise ValueError('Actual native or archived native loaded survey witness required')
            evidence = children(parse(survey_path.read_text('utf-8-sig')), 'COLONY_BODY_REFERENCE_WITNESS')[0]
            if body is None:
                body = val(evidence, 'body')
            if body not in ['Mun', 'Duna'] or val(evidence, 'body') != body or val(evidence, 'worldId') != str(uuid.UUID(a.world)):
                raise ValueError('Actual loaded body/world survey differs')
            survey = [s for s in children(evidence, 'ACTUAL_LOADED_SURVEY') if val(s, 'templateId') == row['PackageId']]
            if len(survey) != 1 or val(survey[0], 'clear') != 'True' or not val(survey[0], 'trustedSurveyHash'):
                raise ValueError('Exact complete loaded footprint was not clear')
            survey = survey[0]
            manifest_path = Path(a.template_root).resolve() / (row['PackageId'] + '.manifest.json')
            m = json.loads(manifest_path.read_text('utf-8-sig'))
            if m['RuntimeCertified'] or m['Id'] != row['PackageId'] or m['Hash'] != val(survey, 'templateHash') or m['CraftSha256'] != val(survey, 'craftSha256') or sha(manifest_path.parent / m['CraftRelativePath']) != m['CraftSha256']:
                raise ValueError('Survey and candidate manifest/craft identity differ')
            latitude, longitude = float(val(survey, 'latitude')), float(val(survey, 'longitude'))
            operation, plot = str(uuid.uuid4()), str(uuid.uuid4())
            request = None
            if index == 0 and a.existing_housing_operation:
                operation = str(uuid.UUID(a.existing_housing_operation))
                original = witness(source, operation)
                game = children(parse(source.read_text('utf-8-sig')), 'GAME')[0]
                native_request = [children(n, 'REQUEST')[0] for s in children(game, 'SCENARIO') if val(s, 'name') == 'ColonyPlacementScenario' for n in children(s, 'PLACEMENT') if val(children(n, 'REQUEST')[0], 'OperationId') == operation][0]
                if val(native_request, 'BodyName') != body or val(native_request, 'TemplateSha256') != m['CraftSha256'] or abs(float(val(native_request, 'Latitude'))-latitude) > 1e-8 or abs(float(val(native_request, 'Longitude'))-longitude) > 1e-8:
                    raise ValueError('Existing original housing does not occupy the exact surveyed package/plot')
                plot = val(native_request, 'PlotId')
                fields = dict(operationId=operation, saveName='colony-placement-street-original-' + operation,
                              reloadSourceName=source_name, reloadSourceSha256=a.source_sha256.lower(), reloadPriorProcessId=str(a.prior_process),
                              existingAuthorization='original-anchored-housing-no-new-enqueue')
            else:
                request_path = output / ('colony-placement-request-street-' + operation + '.cfg')
                command = [sys.executable, str(workspace / 'ExpansePlatform/dev/ColonyPlacementWriteProbe.py'), '--manifest', str(manifest_path),
                           '--world', str(uuid.UUID(a.world)), '--colony', str(uuid.UUID(a.colony)), '--plot', plot, '--operation', operation,
                           '--body', body, '--latitude', repr(latitude), '--longitude', repr(longitude), '--survey', val(survey, 'trustedSurveyHash'),
                           '--test-authorization', 'actual-native-five-package-uneven-body-street-no-economics', '--output', str(request_path)]
                request = json.loads(subprocess.run(command, cwd=workspace, check=True, capture_output=True, text=True).stdout)
                fields = dict(operationId=operation, saveName='colony-placement-street-' + operation,
                              enqueueRequestName=request_path.name, enqueueRequestSha256=request['RequestSha256'])
            cases.append(dict(PackageId=m['Id'], Fields=fields, Request=request, Latitude=latitude, Longitude=longitude,
                              PlotId=plot, ManifestSha256=sha(manifest_path), SurveyFile=str(survey_path), SurveyFileSha256=sha(survey_path),
                              SurveyHash=val(survey, 'trustedSurveyHash'), ActualPreviewSlopeDegrees=float(val(survey, 'actualMaximumSlopeDegrees')),
                              ActualPreviewResidualMetres=float(val(survey, 'actualSupportGapMetres')), MinX=m['MinX'], MaxX=m['MaxX'],
                              MinZ=m['MinZ'], MaxZ=m['MaxZ'], Clearance=m['ClearanceMetres']))
    radius = {'Mun': 200000, 'Duna': 320000}[body]
    largest_lat = max(abs(c['Latitude']) for c in cases) + .001
    gaps = []
    for i, left in enumerate(cases):
        for right in cases[i+1:]:
            dx = radius * math.radians(abs((left['Longitude']-right['Longitude']+180)%360-180)) * math.cos(math.radians(largest_lat))
            dz = radius * math.radians(abs(left['Latitude']-right['Latitude']))
            ex = dx-max(abs(left['MinX']),abs(left['MaxX']))-max(abs(right['MinX']),abs(right['MaxX']))-left['Clearance']-right['Clearance']
            ez = dz-max(abs(left['MinZ']),abs(left['MaxZ']))-max(abs(right['MinZ']),abs(right['MaxZ']))-left['Clearance']-right['Clearance']
            gap = max(ex, ez)
            if gap < 8:
                raise ValueError('Full deployment/access envelopes leave less than8m street')
            gaps.append(dict(Left=left['PackageId'], Right=right['PackageId'], ConservativeClearGapMetres=gap))
    for c in cases:
        dx=radius*math.radians((c['Longitude']-a.reference_longitude+180)%360-180)*math.cos(math.radians(largest_lat))
        dz=radius*math.radians(c['Latitude']-a.reference_latitude)
        envelope=math.hypot(max(abs(c['MinX']),abs(c['MaxX']))+c['Clearance'],max(abs(c['MinZ']),abs(c['MaxZ']))+c['Clearance'])+5
        if math.hypot(dx,dz)+envelope >= 200:
            raise ValueError('Complete street package leaves known stock200m landed window')
    lines=['BATCH','{']
    for c in cases:
        lines += ['\tCASE','\t{']+['\t\t'+k+' = '+v for k,v in c['Fields'].items()]+['\t}']
    data=('\n'.join(lines+['}'])+'\n').encode()
    name='colony-placement-batch-'+str(uuid.uuid4())+'.cfg'; batch=output/name; batch.write_bytes(data)
    unloaded='colony-test-street-unloaded-'+str(uuid.uuid4())
    watcher=dict(authorization='verified-isolated-development-only',saveFolder='',operationId=cases[0]['Fields']['operationId'],saveName=cases[-1]['Fields']['saveName'],
                 mode='cold-street-group' if a.cold_existing_review else 'native-street-group',batchAuthorization='five-exact-street-packages-no-economic-certification',
                 batchRequestName=name,batchRequestSha256=hashlib.sha256(data).hexdigest(),unloadedAuthorization='stock-scene-exit-native-proto-readback',unloadedSaveName=unloaded)
    review=dict(Authority='Five exact isolated native street packages; no economy or runtime certification',Body=body,BodyRadiusMetres=radius,Source=str(source),SourceSha256=a.source_sha256.lower(),
                PriorProcessId=a.prior_process,BatchPath=str(batch),BatchSha256=sha(batch),WatcherFields=watcher,Cases=cases,DeclaredAccessEnvelopeClearances=gaps,
                FinalNativeSaveName=cases[-1]['Fields']['saveName'],UnloadedNativeSaveName=unloaded)
    path=output/'colony-placement-street-group.json'; path.write_text(json.dumps(review,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(dict(Review=str(path),Body=body,BatchSha256=sha(batch),Packages=5,MinimumDeclaredClearGapMetres=min(g['ConservativeClearGapMetres'] for g in gaps)),indent=2))


if __name__ == '__main__':
    main()
