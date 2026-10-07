"""Write reviewable adapter-only ConfigNode terms, never install or edit a save.

The development watcher additionally requires root/save/IPC authorization and the
SHA printed here. This is a physical API probe, not an economic certificate.
"""
import argparse, hashlib, json
from pathlib import Path


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--manifest', default='ExpansePlatform/package/GameData/ExpanseWorldBridge/Templates/housing-kpbs-v1.manifest.json')
    p.add_argument('--world', required=True); p.add_argument('--colony', required=True)
    p.add_argument('--plot', required=True); p.add_argument('--operation', required=True)
    p.add_argument('--latitude', type=float, required=True); p.add_argument('--longitude', type=float, required=True)
    p.add_argument('--heading', type=float, default=0); p.add_argument('--body', default='Kerbin')
    p.add_argument('--survey', required=True); p.add_argument('--test-authorization', required=True)
    p.add_argument('--output', required=True)
    a = p.parse_args(); m = json.loads(Path(a.manifest).read_text(encoding='utf-8'))
    fields = dict(WorldId=a.world, ColonyId=a.colony, PlotId=a.plot, OperationId=a.operation,
                  FacilityName='Isolated adapter probe '+m['Name'], TemplateRelativePath=m['CraftRelativePath'],
                  TemplateSha256=m['CraftSha256'], CertificationId=m['CertificationId'], SurveyRevision=a.survey,
                  EscrowWitness='isolated-test-authorization:'+a.test_authorization, BodyName=a.body,
                  Latitude=a.latitude, Longitude=a.longitude, HeadingDegrees=a.heading)
    for k in ['MinX','MaxX','MinZ','MaxZ','MaximumHeight','MaximumSlopeDegrees','MaximumSupportGapMetres','ClearanceMetres',
              'TemplateRotationX','TemplateRotationY','TemplateRotationZ','TemplateRotationW']: fields[k] = m[k]
    fields.update(SettleSeconds=10, MaximumSettleSpeed=.08, MaximumAngularSpeedDegrees=.05, ExplicitSandboxUnlockOverride=True)
    def value(v):
        if isinstance(v,bool): return str(v)
        if isinstance(v,float): return repr(v)
        text=str(v)
        if not text or any(c in text for c in '\r\n{}='): raise ValueError('Unsafe ConfigNode field')
        return text
    lines = ['REQUEST','{'] + ['\t'+k+' = '+value(v) for k,v in fields.items()]
    for row in m['StartupContents']:
        lines += ['\tCONTENT','\t{','\t\tCraftPartId = '+str(row['CraftPartId']),
                  '\t\tResourceName = '+value(row['ResourceName']),'\t\tAmount = '+repr(row['Amount']/1_000_000), '\t}']
    data=('\n'.join(lines+['}'])+'\n').encode('utf-8'); output=Path(a.output).resolve()
    # Installation/save writes are the root operator's separate action.
    workspace=Path.cwd().resolve()
    if not output.is_relative_to(workspace) or any(x.lower()=='saves' for x in output.parts):
        raise ValueError('Probe source output must stay in this workspace, outside a saves directory')
    output.parent.mkdir(parents=True,exist_ok=True); output.write_bytes(data)
    print(json.dumps(dict(RequestPath=str(output),RequestSha256=hashlib.sha256(data).hexdigest(),
                         CraftSha256=m['CraftSha256'],ExpectedPartCount=m['ExpectedPartCount'],
                         Certification='candidate; physical adapter probe only'),indent=2))


if __name__=='__main__': main()
