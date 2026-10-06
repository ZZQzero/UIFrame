"""Install only source-verified backup binaries; never overwrite a loaded inode."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import uuid
from build import ROOT, sources

TARGETS = {'macos':('macOS','libuiframe_backup.dylib'), 'ios':('iOS','libuiframe_backup.a'),
           'android':('Android/arm64-v8a','libuiframe_backup.so'), 'windows-x64':('Windows/x86_64','uiframe_backup.dll')}

def main():
    parser=argparse.ArgumentParser();parser.add_argument('build',type=Path);args=parser.parse_args()
    manifest=json.loads((args.build/'build-manifest.json').read_text())
    if manifest['sanitized'] or manifest['sources']!=sources():raise ValueError('Stale or sanitizer artifact')
    folder,name=TARGETS[manifest['target']]
    sqlite=ROOT.parent.parent/'Sqlite/Plugins'/folder
    core=json.loads((sqlite/'artifact.json').read_text())
    if manifest['sqlite_build_id']!=core['build_id']:raise ValueError('Installed SQLite dependency differs')
    binary=args.build/name
    if hashlib.sha256(binary.read_bytes()).hexdigest()!=manifest['artifacts'][name]:raise ValueError('Backup artifact checksum mismatch')
    destination=ROOT.parent/'Plugins'/folder/name;destination.parent.mkdir(parents=True,exist_ok=True)
    temporary=destination.with_suffix(destination.suffix+'.installing');shutil.copyfile(binary,temporary);os.replace(temporary,destination)
    meta=destination.with_name(name+'.meta')
    if not meta.exists():
        source=next(sqlite.glob('*uiframe_sqlite*.meta')).read_text().splitlines()
        meta.write_text('\n'.join('guid: '+uuid.uuid4().hex if x.startswith('guid:') else x for x in source)+'\n')
    (destination.parent/'build-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    artifact=dict(abi=2,sqlite_build_id=manifest['sqlite_build_id'],binary=name,sha256=manifest['artifacts'][name],sources=[dict(path=k,sha256=v) for k,v in manifest['sources'].items()])
    (destination.parent/'artifact.json').write_text(json.dumps(artifact,indent=2)+'\n')
    print('Installed: '+str(destination))

if __name__ == '__main__':main()
