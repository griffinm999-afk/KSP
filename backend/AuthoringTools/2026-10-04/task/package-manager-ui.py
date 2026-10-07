from pathlib import Path
import hashlib
import json
import shutil
import zipfile

root = Path(__file__).parent
version = '20261005-001323'
previous = '20261004-214449'
installed = Path(r'C:\Users\griff\AppData\Local\Programs\ExpanseFoundations\versions') / previous
build = root / 'manager-ui-work' / 'src' / 'Expanse.Clock.Manager' / 'bin' / 'Release' / 'net8.0-windows'
out = root / ('manager-ui-release-' + version)
staged = out / 'version'
assert not out.exists(), 'Release output already exists'
old_manifest = json.loads((installed / 'MANIFEST.json').read_text(encoding='utf-8-sig'))
assert old_manifest['Name'] == 'Expanse Minmus MVP' and len(old_manifest['Files']) == 60

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()

def relative(path):
    return Path(path[5:] if path.startswith('Apps/') else path)

for row in old_manifest['Files']:
    target = installed / relative(row['Path'])
    assert target.is_file() and digest(target) == row['Sha256'].upper(), row['Path']
shutil.copytree(installed, staged)
replaced = ['Expanse.Clock.Manager.dll', 'Expanse.Clock.Core.dll', 'Expanse.Domain.dll']
for name in replaced:
    shutil.copy2(build / name, staged / 'Manager' / name)
    key = 'Apps/Manager/' + name
    next_row = next(row for row in old_manifest['Files'] if row['Path'] == key)
    next_row['Sha256'] = digest(staged / 'Manager' / name)
old_manifest['UiUpdate'] = {'version': version, 'baseVersion': previous, 'scope': 'Manager UI only'}
(staged / 'MANIFEST.json').write_text(json.dumps(old_manifest, indent=2) + '\n', encoding='utf-8')
for row in old_manifest['Files']:
    assert digest(staged / relative(row['Path'])) == row['Sha256'].upper(), row['Path']
assert sorted(str(p.relative_to(staged)) for p in staged.rglob('*') if p.is_file()) == sorted(
    [str(relative(row['Path'])) for row in old_manifest['Files']] + ['MANIFEST.json'])
archive = out / ('expanse-manager-ui-' + version + '.zip')
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
    for path in sorted(staged.rglob('*')):
        if path.is_file():
            z.write(path, path.relative_to(staged).as_posix())
with zipfile.ZipFile(archive) as z:
    assert len(z.namelist()) == 61
    for row in old_manifest['Files']:
        assert hashlib.sha256(z.read(relative(row['Path']).as_posix())).hexdigest().upper() == row['Sha256'].upper()
receipt = {
    'version': version, 'baseVersion': previous, 'scope': 'Manager UI only',
    'archive': archive.name, 'archiveSha256': digest(archive),
    'manifestSha256': digest(staged / 'MANIFEST.json'), 'changedFiles': {
        'Manager/' + name: digest(staged / 'Manager' / name) for name in replaced
    }, 'fileCount': 60, 'gameDataChanged': False, 'installed': False,
}
(out / 'RECEIPT.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
print(json.dumps(receipt, indent=2))
