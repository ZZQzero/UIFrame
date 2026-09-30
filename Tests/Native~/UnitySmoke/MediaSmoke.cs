using System;
using System.IO;
using Cysharp.Threading.Tasks;
using Game.Media;
using Game.Media.Backup;
using UnityEngine;

public static class MediaSmoke
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Start(){Run().Forget(Debug.LogException);}
    static async UniTask Run()
    {
        string root=Path.Combine(Application.persistentDataPath,"MediaSmoke",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string path=Path.Combine(root,"fixture.png");var texture=new Texture2D(8,4);File.WriteAllBytes(path,texture.EncodeToPNG());UnityEngine.Object.Destroy(texture);
        var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"library.sqlite"));var cache=new ImageThumbnailCache();ImageBackupService service=null;
        try
        {
            var scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,root);await library.RefreshAsync(scope);var page=await library.QueryAsync(scope);
            using(var a=await cache.AcquireAsync(page.Items[0]))using(var b=await cache.AcquireAsync(page.Items[0]))if(a.Texture!=b.Texture)throw new Exception("Cache did not share resource");
            bool native=Application.platform==RuntimePlatform.Android || Application.platform==RuntimePlatform.IPhonePlayer;
            service=await ImageBackupService.CreateAsync(new BackupConfiguration {ServerUrl="https://validation.invalid",Account="smoke",AccessToken=()=>"unused-test-credential",EnableNativeBackgroundTransfer=native,
                StorageDirectory=native?ImageBackupService.NativeBackupStorageDirectory:Path.Combine(root,"backup")});
            var ids=await service.EnqueueAsync(new[]{ImageReference.FromFile(path)});await service.PauseAsync();await service.ResumeAsync();
            string operation=await service.SubmitOperationAsync(new BackupOperationCommand {OperationId=Guid.NewGuid().ToString("N"),Action=BackupAction.Cancel,TaskIds=ids});await service.WaitOperationAsync(operation);
            await new BackupMaintenance(service).RunAsync(new BackupRetentionPolicy {HistoryAge=TimeSpan.Zero,KeepHistoryCount=0,TimeSlice=TimeSpan.FromSeconds(5)});
            if((await service.GetSummaryAsync())[BackupState.Canceled]!=0)throw new Exception("Cleanup did not remove terminal task");
            var automatic=await AutomaticImageBackup.CreateAsync(service,library,()=>true);await automatic.ConfigureAsync(new AutomaticBackupPolicy());
            Debug.Log("UIFRAME_MEDIA_DEVICE_SMOKE_PASSED");
        }
        finally{if(service!=null)await service.ShutdownAsync();await cache.ShutdownAsync();await library.ShutdownAsync();}
    }
}
