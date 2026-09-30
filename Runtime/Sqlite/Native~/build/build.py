"""Offline build from checked-in, hash-verified sources. No automatic SDK downloads."""
import argparse
import hashlib
import json
from pathlib import Path
import platform
import subprocess

ROOT = Path(__file__).resolve().parents[1]

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--cmake', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--target', choices=['macos', 'ios', 'ios-simulator', 'android', 'windows-x64'], required=True)
    parser.add_argument('--ndk', type=Path)
    parser.add_argument('--llvm-mingw', type=Path, help='Explicit LLVM-MinGW root for a Windows cross build')
    parser.add_argument('--sanitize', action='store_true')
    args = parser.parse_args()
    args.output = args.output.resolve()
    windows = args.target == 'windows-x64'
    host_tests = args.target == 'macos' or (windows and platform.system() == 'Windows')
    if args.llvm_mingw and not windows: parser.error('--llvm-mingw requires --target windows-x64')
    if windows and platform.system() != 'Windows' and not args.llvm_mingw:
        parser.error('A Windows cross build requires --llvm-mingw')
    source_paths = [ROOT/'src/core.cpp',ROOT/'include/ufsqlite.h',ROOT/'CMakeLists.txt',ROOT/'build/build.py']
    if windows: source_paths.append(ROOT/'build/verify_windows.py')
    if args.llvm_mingw: source_paths.append(ROOT/'build/windows-llvm-mingw.cmake')
    sources = {p.relative_to(ROOT).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in source_paths}
    lock = json.loads((ROOT / 'third_party/sqlite/source-lock.json').read_text(encoding="utf-8"))
    for name, digest in lock['files'].items():
        if hashlib.sha256((ROOT / 'third_party/sqlite' / name).read_bytes()).hexdigest() != digest:
            raise RuntimeError('SQLite source checksum mismatch: ' + name)
    args.output.mkdir(parents=True, exist_ok=True)
    if args.sanitize and args.target != 'macos': parser.error('--sanitize is a host validation configuration')
    flags = ['-DCMAKE_BUILD_TYPE=RelWithDebInfo', '-DUFSQLITE_TESTS=' + ('ON' if args.target == 'macos' or windows else 'OFF'),
             '-DUFSQLITE_SANITIZE=' + ('ON' if args.sanitize else 'OFF')]
    if args.target == 'macos': flags += ['-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64', '-DCMAKE_OSX_DEPLOYMENT_TARGET=11.0']
    elif args.target.startswith('ios'):
        flags += ['-DCMAKE_SYSTEM_NAME=iOS', '-DCMAKE_OSX_ARCHITECTURES=arm64', '-DCMAKE_OSX_DEPLOYMENT_TARGET=15.0',
                  '-DCMAKE_OSX_SYSROOT=' + ('iphonesimulator' if args.target == 'ios-simulator' else 'iphoneos')]
    elif windows:
        if args.llvm_mingw:
            flags += ['-G', 'Unix Makefiles' if platform.system() != 'Windows' else 'Ninja',
                      '-DCMAKE_TOOLCHAIN_FILE=' + str(ROOT/'build/windows-llvm-mingw.cmake'),
                      '-DUFSQLITE_LLVM_MINGW=' + args.llvm_mingw.resolve().as_posix()]
        else:
            flags += ['-G', 'Visual Studio 17 2022', '-A', 'x64']
    else:
        if args.ndk is None: parser.error('--ndk is required for Android')
        flags += ['-DCMAKE_TOOLCHAIN_FILE=' + str(args.ndk / 'build/cmake/android.toolchain.cmake'),
                  '-DANDROID_ABI=arm64-v8a', '-DANDROID_PLATFORM=android-25', '-DANDROID_STL=c++_static']
    subprocess.run([args.cmake, '-S', str(ROOT), '-B', str(args.output), *flags], check=True)
    subprocess.run([args.cmake, '--build', str(args.output), '--config', 'RelWithDebInfo', '--parallel', '2'], check=True)
    if host_tests:
        ctest = Path(args.cmake).with_name('ctest.exe' if platform.system() == 'Windows' else 'ctest')
        subprocess.run([str(ctest), '--test-dir', str(args.output), '-C', 'RelWithDebInfo', '--output-on-failure'], check=True)
    if args.target == 'macos':
        subprocess.run(['xcrun', 'dsymutil', str(args.output / 'libuiframe_sqlite.dylib')], check=True)
    artifacts = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in args.output.glob('*uiframe_sqlite*') if p.is_file()}
    if windows:
        # Validate architecture, exact C ABI exports and runtime dependencies before publication.
        from verify_windows import inspect
        windows_binary = inspect(args.output/'uiframe_sqlite.dll', ROOT/'include/ufsqlite.h')
    if sources != {p.relative_to(ROOT).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in source_paths}:
        raise RuntimeError('Sources changed during build; no artifact manifest was published')
    manifest = dict(target=args.target, sqlite=lock['version'], source_id=lock['source_id'], abi=3,
                    build_id=(args.output/'build-id.txt').read_text(encoding="utf-8").strip(), sanitized=args.sanitize,
                    native_tests='passed' if host_tests else ('built-not-run' if windows else 'not-built'),
                    sources=sources,
                    artifacts=artifacts, flags=flags,
                    compiler_info=(args.output/'compiler-info.txt').read_text(encoding="utf-8").splitlines(),
                    cmake=subprocess.check_output([args.cmake, '--version'], text=True).splitlines()[0],
                    compiler_cache=[line for line in (args.output / 'CMakeCache.txt').read_text(encoding="utf-8").splitlines() if line.startswith(('CMAKE_C_COMPILER:', 'CMAKE_CXX_COMPILER:', 'CMAKE_OSX_SYSROOT:', 'CMAKE_ANDROID_NDK:'))])
    if windows: manifest['windows_binary'] = windows_binary
    (args.output / 'build-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    print('Build published: ' + str(args.output) + '; native tests: ' + manifest['native_tests'])

if __name__ == '__main__': main()
