"""Copy current media code into an isolated Unity validation project, using cached dependencies."""
import argparse
import json
from pathlib import Path
import shutil


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--project',type=Path,required=True);parser.add_argument('--host-project',type=Path,required=True);args=parser.parse_args()
    package=Path(__file__).resolve().parents[2];target=args.project.resolve();base=args.host_project.resolve()
    for folder in ['Assets/UIFrame/Runtime','Assets/UIFrame/Editor','Assets/UIFrame/Tests/Editor','Packages','ProjectSettings']:(target/folder).mkdir(parents=True,exist_ok=True)
    for folder in ['Sqlite','Media','MediaBackup','MediaStorage','Plugins','Resources']:
        shutil.copytree(package/'Runtime'/folder,target/'Assets/UIFrame/Runtime'/folder,dirs_exist_ok=True)
    shutil.copyfile(package/'Runtime/Core/CleanupFailure.cs',target/'Assets/UIFrame/Runtime/CleanupFailure.cs')
    (target/'Assets/UIFrame/Runtime/UIFrame.Runtime.asmdef').write_text(json.dumps({'name':'UIFrame.Runtime','references':['UniTask','UIFrame.Sqlite','Unity.ugui']}))
    # Compile the public virtual-list sample in its own consumer assembly.
    loop=target/'Assets/UIFrame/Runtime/LoopScroll';loop.mkdir(parents=True,exist_ok=True)
    for name in ['LoopScrollRectBase','LoopScrollRect','LoopVerticalScrollRect','LoopScrollDataSource','LoopScrollPrefabSource','LoopScrollSizeHelper','LoopScrollSizeUtils']:
        shutil.copyfile(package/('Runtime/LoopScroll/'+name+'.cs'),loop/(name+'.cs'))
    sample=target/'Assets/GalleryDemo';sample.mkdir(parents=True,exist_ok=True)
    for source in (package/'Samples~/GalleryDemo').glob('*.cs'):shutil.copyfile(source,sample/source.name)
    (sample/'GalleryDemo.Samples.asmdef').write_text(json.dumps({'name':'GalleryDemo.Samples','references':['UIFrame.Runtime','UniTask','Unity.ugui']}))
    shutil.copytree(package/'Editor/Media',target/'Assets/UIFrame/Editor/Media',dirs_exist_ok=True)
    shutil.copyfile(package/'Editor/UIFrame.Editor.asmdef',target/'Assets/UIFrame/Editor/UIFrame.Editor.asmdef')
    shutil.copyfile(package/'Tests/Editor/Media/MediaTests.cs',target/'Assets/UIFrame/Tests/Editor/MediaTests.cs')
    (target/'Assets/UIFrame/Tests/Editor/UIFrame.Regression.Editor.asmdef').write_text(json.dumps({'name':'UIFrame.Regression.Editor','references':['UIFrame.Runtime','UIFrame.Editor','UniTask','UIFrame.Sqlite'],'optionalUnityReferences':['TestAssemblies'],'includePlatforms':['Editor']}))
    shutil.copyfile(package/'Tests/Native~/UnitySmoke/MediaBuildSmoke.cs',target/'Assets/UIFrame/Editor/MediaBuildSmoke.cs')
    shutil.copyfile(package/'Tests/Native~/UnitySmoke/MediaSmoke.cs',target/'Assets/UIFrame/Runtime/MediaSmoke.cs')
    deps={}
    for name in ['com.unity.test-framework','com.unity.ext.nunit','com.cysharp.unitask','com.unity.ugui']:
        deps[name]='file:'+str(next((base/'Library/PackageCache').glob(name+'@*')))
    for name in ['imageconversion','imgui','jsonserialize','unitywebrequest','unitywebrequesttexture','androidjni','ui','uielements','physics','physics2d','audio','assetbundle','animation']:
        deps['com.unity.modules.'+name]='1.0.0'
    (target/'Packages/manifest.json').write_text(json.dumps({'dependencies':deps},indent=2))
    shutil.copyfile(base/'ProjectSettings/ProjectVersion.txt',target/'ProjectSettings/ProjectVersion.txt')
    print(target)


if __name__=='__main__':main()
