"""Repeatable standalone run of the same managed fixtures used by Unity EditMode."""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

ROOT=Path(__file__).resolve().parents[2]

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--mono-bin',type=Path,required=True)
    parser.add_argument('--nunit',type=Path,required=True)
    parser.add_argument('--native-directory',type=Path,required=True)
    args=parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='uiframe-managed-validation-') as directory:
        assembly=Path(directory)/'UIFrame.Sqlite.dll'
        runner=Path(directory)/'runner.exe'
        sources=list(ROOT.glob('*.cs'))+list((ROOT/'Internal').glob('*.cs'))
        subprocess.run([str(args.mono_bin/'mcs'),'-langversion:latest','-target:library','-out:'+str(assembly),
                        *map(str,sources)],check=True)
        subprocess.run([str(args.mono_bin/'mcs'),'-langversion:latest','-r:'+str(assembly),'-r:'+str(args.nunit),
                        '-out:'+str(runner),str(ROOT/'Tests/Editor/SqliteTests.cs'),
                        str(Path(__file__).with_name('managed_runner.cs'))],check=True)
        environment=dict(os.environ,MONO_PATH=str(args.nunit.parent),DYLD_LIBRARY_PATH=str(args.native_directory))
        subprocess.run([str(args.mono_bin/'mono'),str(runner)],check=True,env=environment)
        sample=Path(directory)/'sample.exe'
        subprocess.run([str(args.mono_bin/'mcs'),'-langversion:latest','-r:'+str(assembly),'-out:'+str(sample),
                        str(ROOT/'Samples~/GameRepositories.cs'),str(Path(__file__).with_name('sample_runner.cs'))],check=True)
        subprocess.run([str(args.mono_bin/'mono'),str(sample)],check=True,env=environment)

if __name__=='__main__':main()
