from pathlib import Path
import hashlib
import json
import shutil
import zipfile

root = Path(__file__).parent / 'manager-ui-release-20261005-001323'
source = root / 'version' / 'Manager'
target = root / 'manager-only'
assert not target.exists()
shutil.copytree(source, target)
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest().upper()
files = sorted(p for p in target.iterdir() if p.is_file())
assert len(files) == 6
manifest = {'version': '20261005-001323', 'baseVersion': '20261004-214449',
            'scope': 'Manager only', 'files': {p.name: sha(p) for p in files}}
(target / 'MANAGER-MANIFEST.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
archive = root / 'expanse-manager-only-20261005-001323.zip'
with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED) as z:
    for p in sorted(target.iterdir()): z.write(p, p.name)
with zipfile.ZipFile(archive) as z:
    assert sorted(z.namelist()) == sorted(p.name for p in target.iterdir())
    for p in target.iterdir(): assert hashlib.sha256(z.read(p.name)).hexdigest().upper() == sha(p)
receipt = {'version': manifest['version'], 'baseVersion': manifest['baseVersion'],
           'archive': archive.name, 'archiveSha256': sha(archive),
           'manifestSha256': sha(target / 'MANAGER-MANIFEST.json'),
           'fileCount': len(files), 'installed': False}
(root / 'MANAGER-ONLY-RECEIPT.json').write_text(json.dumps(receipt, indent=2) + '\n', encoding='utf-8')
print(json.dumps(receipt, indent=2))
