using System;
using System.IO;
using Cysharp.Threading.Tasks;
using Game.Media;
using Game.Media.Backup;
using UnityEngine;

public static class MediaSmoke
{
    [Serializable] sealed class ServerSettings
    {
        public string serverUrl,account,token;
        public bool allowDevelopmentHttp,wifiOnly=true;
    }
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
            var configuration=Resources.Load<TextAsset>("BackupSmokeConfig");
            var settings=configuration==null?null:JsonUtility.FromJson<ServerSettings>(configuration.text);
            service=await ImageBackupService.CreateAsync(new BackupConfiguration {ServerUrl=settings?.serverUrl??"https://validation.invalid",Account=settings?.account??"smoke",AccessToken=()=>settings?.token??"unused-test-credential",EnableNativeBackgroundTransfer=native,
                AllowDevelopmentHttp=settings?.allowDevelopmentHttp??false,NativeWifiOnly=settings?.wifiOnly??true,
                StorageDirectory=native?ImageBackupService.NativeBackupStorageDirectory:Path.Combine(root,"backup")});
            var submission=await (await service.SubmitAsync(Guid.NewGuid().ToString("N"),new[]{ImageReference.FromFile(path)})).WaitAsync();
            if(submission.AcceptedTaskIds.Count!=1)throw new Exception("Native executor did not accept the photo");
            if(settings!=null)
            {
                Debug.Log("UIFRAME_MEDIA_DEVICE_ACCEPTED task="+submission.AcceptedTaskIds[0]);
                using var timeout=new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5));
                for(;;)
                {
                    var task=await service.GetTaskAsync(submission.AcceptedTaskIds[0],timeout.Token);
                    if(task.state==BackupState.Completed)break;
                    if(task.state==BackupState.Failed || task.state==BackupState.NeedsAttention || task.state==BackupState.Canceled)throw new Exception("Device backup ended: "+task.error);
                    await UniTask.Delay(500,ignoreTimeScale:true,cancellationToken:timeout.Token);
                }
                Debug.Log("UIFRAME_MEDIA_DEVICE_CONFIRMED_SMOKE_PASSED");
            }
            else await service.PauseAsync();
            var automatic=await AutomaticImageBackup.CreateAsync(service,library,()=>true);await automatic.ConfigureAsync(new AutomaticBackupPolicy());
            if(settings==null)Debug.Log("UIFRAME_MEDIA_DEVICE_ADMISSION_SMOKE_PASSED (server confirmation requires a configured reference service)");
        }
        finally{if(service!=null)await service.ShutdownAsync();await cache.ShutdownAsync();await library.ShutdownAsync();}
    }
}
