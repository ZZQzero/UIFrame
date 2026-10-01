"""Build the shared photo repository against the separately built SQLite core."""
import argparse
import hashlib
import json
import os
import platform
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SQLITE = ROOT.parent.parent / 'Sqlite' / 'Native~'


def sources():
    paths = [ROOT / 'CMakeLists.txt', *sorted((ROOT/'src').glob('*')), *sorted((ROOT/'include').glob('*.h')),
             ROOT/'schema/catalog.sql', *sorted((ROOT/'build').glob('*.py')), SQLITE/'include/ufsqlite_client.hpp', SQLITE/'include/ufsqlite.h']
    return {str(p.relative_to(ROOT)) if p.is_relative_to(ROOT) else '../../Sqlite/Native~/include/'+p.name:
            hashlib.sha256(p.read_bytes()).hexdigest() for p in paths if p.is_file()}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--cmake', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--core-build', type=Path, required=True)
    parser.add_argument('--target', choices=['macos', 'android', 'ios', 'windows-x64'], required=True)
    parser.add_argument('--ndk', type=Path)
    parser.add_argument('--llvm-mingw', type=Path)
    parser.add_argument('--sanitize', action='store_true')
    args = parser.parse_args()
    args.output = args.output.resolve()
    core = json.loads((args.core_build/'build-manifest.json').read_text())
    if core['target'] != args.target or core['abi'] != 3:
        raise ValueError('SQLite core target/ABI mismatch')
    names = dict(macos='libuiframe_sqlite.dylib', android='libuiframe_sqlite.so', ios='libuiframe_sqlite.a', **{'windows-x64':'uiframe_sqlite.dll'})
    binary = args.core_build.resolve()/names[args.target]
    if hashlib.sha256(binary.read_bytes()).hexdigest() != core['artifacts'][binary.name]:
        raise ValueError('SQLite core artifact checksum mismatch')
    windows_native = args.target == 'windows-x64' and platform.system() == 'Windows' and not args.llvm_mingw
    library = binary if args.target != 'windows-x64' else args.core_build.resolve()/('uiframe_sqlite.lib' if windows_native else 'libuiframe_sqlite.dll.a')
    host_tests = args.target == 'macos' or windows_native
    flags = ['-DCMAKE_BUILD_TYPE=RelWithDebInfo', '-DUFSQLITE_LIBRARY='+str(library),
             '-DUFBACKUP_TESTS='+('ON' if host_tests else 'OFF'), '-DUFBACKUP_SANITIZE='+('ON' if args.sanitize else 'OFF')]
    if args.target == 'macos':
        flags += ['-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64', '-DCMAKE_OSX_DEPLOYMENT_TARGET=11.0']
    elif args.target == 'ios':
        flags += ['-DCMAKE_SYSTEM_NAME=iOS', '-DCMAKE_OSX_SYSROOT=iphoneos', '-DCMAKE_OSX_ARCHITECTURES=arm64', '-DCMAKE_OSX_DEPLOYMENT_TARGET=15.0']
    elif args.target == 'android':
        if not args.ndk: parser.error('Android requires --ndk')
        flags += ['-DCMAKE_TOOLCHAIN_FILE='+str(args.ndk/'build/cmake/android.toolchain.cmake'), '-DANDROID_ABI=arm64-v8a', '-DANDROID_PLATFORM=android-25', '-DANDROID_STL=c++_static']
    elif windows_native:
        flags += ['-G', 'Visual Studio 17 2022', '-A', 'x64']
    else:
        if not args.llvm_mingw: parser.error('Windows cross build requires --llvm-mingw')
        flags += ['-DCMAKE_TOOLCHAIN_FILE='+str(SQLITE/'build/windows-llvm-mingw.cmake'), '-DUFSQLITE_LLVM_MINGW='+str(args.llvm_mingw)]
    original = sources()
    subprocess.run([args.cmake, '-S', str(ROOT), '-B', str(args.output), *flags], check=True)
    subprocess.run([args.cmake, '--build', str(args.output), '--config', 'RelWithDebInfo', '--parallel', '2'], check=True)
    if host_tests:
        environment=dict(os.environ)
        if windows_native: environment['PATH']=str(args.core_build.resolve())+os.pathsep+environment['PATH']
        subprocess.run([str(Path(args.cmake).with_name('ctest.exe' if windows_native else 'ctest')), '--test-dir', str(args.output), '-C', 'RelWithDebInfo', '--output-on-failure'], check=True,env=environment)
    if args.target == 'macos':
        subprocess.run(['xcrun','dsymutil',str(args.output/'libuiframe_backup.dylib')],check=True)
    if sources() != original: raise ValueError('Sources changed during build')
    artifacts = {p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in args.output.glob('*uiframe_backup*') if p.is_file()}
    build_id = hashlib.sha256(json.dumps(original,sort_keys=True).encode()).hexdigest()
    manifest = dict(target=args.target, abi=1, sqlite_abi=3, sqlite_build_id=core['build_id'], build_id=build_id,
                    sources=original, sanitized=args.sanitize, artifacts=artifacts, flags=flags,
                    native_tests='passed' if host_tests else 'not-run',
                    compiler_cache=[x for x in (args.output/'CMakeCache.txt').read_text().splitlines() if x.startswith(('CMAKE_CXX_COMPILER:', 'CMAKE_OSX_SYSROOT:', 'CMAKE_ANDROID_NDK:'))])
    (args.output/'build-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
    print('Backup repository build: '+build_id)

if __name__ == '__main__': main()
