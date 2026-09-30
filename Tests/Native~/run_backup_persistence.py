"""Run the platform-independent native backup repository contract suite."""
import argparse
from pathlib import Path
import subprocess


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--cmake',required=True)
    parser.add_argument('--build',type=Path,required=True)
    parser.add_argument('--sqlite-library',type=Path,required=True)
    parser.add_argument('--sanitize',action='store_true')
    args=parser.parse_args()
    source=Path(__file__).resolve().parents[2]/'Runtime/MediaBackup/Native~'
    subprocess.run([args.cmake,'-S',str(source),'-B',str(args.build),'-DUFSQLITE_LIBRARY='+str(args.sqlite_library.resolve()),
                    '-DUFBACKUP_TESTS=ON','-DUFBACKUP_SANITIZE='+('ON' if args.sanitize else 'OFF')],check=True)
    subprocess.run([args.cmake,'--build',str(args.build),'--parallel','2'],check=True)
    subprocess.run([str(Path(args.cmake).with_name('ctest')),'--test-dir',str(args.build),'--output-on-failure'],check=True)


if __name__=='__main__':main()
