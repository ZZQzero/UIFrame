"""Create an isolated smoke project with no UIFrame, UniTask or photo dependency."""
import argparse
import json
from pathlib import Path
import shutil

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--project',type=Path,required=True)
    parser.add_argument('--unity-version',required=True)
    args=parser.parse_args()
    args.project.mkdir(parents=True,exist_ok=False)
    for relative in ['Assets/Editor','Packages','ProjectSettings']:
        (args.project/relative).mkdir(parents=True)
    root=Path(__file__).resolve().parents[2]
    shutil.copytree(root,args.project/'Assets/Sqlite',ignore=shutil.ignore_patterns('Tests','Tests.meta'))
    (args.project/'Packages/manifest.json').write_text(json.dumps({'dependencies':{}},indent=2)+'\n')
    (args.project/'ProjectSettings/ProjectVersion.txt').write_text('m_EditorVersion: '+args.unity_version+'\n')
    source=Path(__file__).parent/'UnitySmoke'
    shutil.copyfile(source/'BuildSmoke.cs',args.project/'Assets/Editor/BuildSmoke.cs')
    shutil.copyfile(source/'Smoke.cs',args.project/'Assets/Smoke.cs')
    print(args.project.resolve())

if __name__=='__main__':main()
