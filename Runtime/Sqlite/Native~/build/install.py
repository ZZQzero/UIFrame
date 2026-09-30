"""Publish a verified build into Unity using atomic file replacement, never a loaded inode."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import uuid

ROOT = Path(__file__).resolve().parents[2]
TARGETS = {
    'windows-x64': ('Windows/x86_64', 'uiframe_sqlite.dll', '''    Editor:
      enabled: 1
      settings: {CPU: x86_64, OS: Windows, DefaultValueInitialized: true}
    Win64:
      enabled: 1
      settings: {CPU: x86_64}
'''),
    'macos': ('macOS', 'libuiframe_sqlite.dylib', '''    Editor:
      enabled: 1
      settings: {CPU: AnyCPU, OS: OSX, DefaultValueInitialized: true}
    OSXUniversal:
      enabled: 1
      settings: {CPU: AnyCPU}
'''),
    'android': ('Android/arm64-v8a', 'libuiframe_sqlite.so', '''    Android:
      enabled: 1
      settings: {CPU: ARM64}
'''),
    'ios': ('iOS', 'libuiframe_sqlite.a', '''    iOS:
      enabled: 1
      settings: {CPU: ARM64, AddToEmbeddedBinaries: false, CompileFlags: '', FrameworkDependencies: ''}
'''),
}

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('build', type=Path)
    args = parser.parse_args()
    manifest = json.loads((args.build/'build-manifest.json').read_text(encoding="utf-8"))
    if manifest['sanitized']: raise ValueError('Sanitizer libraries must not be installed into Unity')
    directory, name, platforms = TARGETS[manifest['target']]
    for relative, digest in manifest['sources'].items():
        if hashlib.sha256((ROOT/'Native~'/relative).read_bytes()).hexdigest() != digest:
            raise ValueError('Build source changed: '+relative)
    binary = args.build/name
    if hashlib.sha256(binary.read_bytes()).hexdigest() != manifest['artifacts'][name]:
        raise ValueError('Build artifact checksum mismatch')
    destination = ROOT/'Plugins'/directory/name
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(destination.name+'.installing')
    shutil.copyfile(binary, temporary)
    os.replace(temporary, destination)
    meta = destination.with_name(name+'.meta')
    if not meta.exists():
        meta.write_text('fileFormatVersion: 2\nguid: '+uuid.uuid4().hex+'''
PluginImporter:
  externalObjects: {}
  serializedVersion: 3
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
    Any:
      enabled: 0
      settings: {}
'''+platforms+'  userData: \n  assetBundleName: \n  assetBundleVariant: \n')
    (destination.parent/'build-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n', encoding='utf-8')
    artifact = dict(abi=manifest['abi'], sanitized=False, binary=name, sha256=manifest['artifacts'][name],
                    build_id=manifest['build_id'], core_sha256=manifest['sources']['src/core.cpp'],
                    header_sha256=manifest['sources']['include/ufsqlite.h'],
                    cmake_sha256=manifest['sources']['CMakeLists.txt'])
    (destination.parent/'artifact.json').write_text(json.dumps(artifact,indent=2)+'\n', encoding='utf-8')
    print('Installed verified artifact: '+str(destination))
    print('Restart Unity before using a replaced native library.')

if __name__ == '__main__': main()
