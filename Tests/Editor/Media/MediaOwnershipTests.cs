using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Game.Media;
using Game.Media.Backup;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
namespace UIFrame.Regression
{
    public sealed class MediaBackupOwnershipTests
    {
        string root;
        [SetUp] public void Setup(){root=Path.Combine(Path.GetTempPath(),"uiframe-system-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);}
        [TearDown] public void Cleanup(){Directory.Delete(root,true);}
        BackupConfiguration Config()=>new BackupConfiguration {ServerUrl="http://127.0.0.1:18787",Account="integration-user",AccessToken=()=>"uiframe-integration-test-token",StorageDirectory=Path.Combine(root,"backup"),AllowDevelopmentHttp=true};
        static async UniTask<Exception> Observe(UniTask task){try{await task;return null;}catch(Exception error){return error;}}
        sealed class Gallery : IMediaTransport,IDisposable
        {
            readonly IMediaTransport previous=NativeMedia.Transport;
            readonly Dictionary<string,string> replies=new Dictionary<string,string>();
            internal MediaItem[] Photos=Array.Empty<MediaItem>();
            internal string DeniedId,DeniedOperation="stat";
            internal Gallery(){NativeMedia.Transport=this;}
            public void Start(string json)
            {
                var r=JsonUtility.FromJson<MediaRequest>(json);var result=new MediaResponse {status="ok",access="Authorized",boundary="stable",items=Array.Empty<MediaItem>()};
                if(r.op=="imagesNext")result.items=Photos;
                if(r.op=="stat")result.items=new[]{Photos.Single(p=>p.id==r.path)};
                if(r.op==DeniedOperation && r.path==DeniedId)result=new MediaResponse {status="error",code="PermissionDenied",error="Photo library access revoked during preparation"};
                if(r.op=="export" && !(r.op==DeniedOperation && r.path==DeniedId)) {
                    string path=Path.Combine(r.output,"photo.jpg");Directory.CreateDirectory(r.output);File.WriteAllBytes(path,new byte[]{1,2,3,4});
                    result.items=new[]{new MediaItem {path=path,name="photo.jpg",mime="image/jpeg",size=4}};
                }
                replies[r.id]=JsonUtility.ToJson(result);
            }
            public string Poll(string id){if(!replies.TryGetValue(id,out var result))return null;replies.Remove(id);return result;}
            public void Cancel(string id){replies.Remove(id);}
            public bool Pending(string id)=>replies.ContainsKey(id);
            public void Dispose(){NativeMedia.Transport=previous;Assert.IsEmpty(replies);}
        }
        static MediaItem Photo(string id)=>new MediaItem {id=id,source="library",name=id+".jpg",mime="image/jpeg",size=4,version="v1"};
        [UnityTest] public IEnumerator PermissionDeniedDuringVersionCheckSuspendsScope()=>PermissionLoss("stat");
        [UnityTest] public IEnumerator PermissionDeniedDuringExportSuspendsScope()=>PermissionLoss("export");
        IEnumerator PermissionLoss(string operation)=>UniTask.ToCoroutine(async()=>
        {
            using var gallery=new Gallery {Photos=new[]{Photo("first")},DeniedOperation=operation};
            string database=Path.Combine(root,"library.sqlite");
            var fixture=new BackupProtocolFixture {BeforeUpload=(item,token)=>item.sourceId=="library:first"?Task.Delay(Timeout.Infinite,token):Task.CompletedTask};
            var library=await ImageLibraryIndex.OpenAsync(database);var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,fixture);
            var scope=new ImageLibraryScope(ImageLibrarySourceKind.PhotoLibrary);
            try {
                var automatic=await AutomaticImageBackup.CreateAsync(service,library);
                await automatic.ConfigureAsync(new AutomaticBackupPolicy {enabled=true,sourceKind=BackupSourceKind.PhotoLibrary,wifiOnly=false,includeExisting=true});
                await automatic.ScanOnceAsync();var task=(await service.QueryTasksAsync()).Items.Single();
                var before=await library.GetPositionAsync(scope);
                string separate=Path.Combine(root,"separate.jpg");File.WriteAllBytes(separate,new byte[]{4,3,2,1});
                string unrelated=(await service.SubmitPhotosAsync(new[]{ImageReference.FromFile(separate)}))[0];
                gallery.Photos=new[]{Photo("first"),Photo("second")};gallery.DeniedId="second";
                await automatic.ScanOnceAsync();await service.WaitForIdleAsync();
                var error=await Observe(automatic.ScanOnceAsync());
                Assert.IsInstanceOf<BackupSubmissionException>(error);
                Assert.AreEqual("PermissionDenied",((BackupSubmissionException)error).InnerExceptions.OfType<GalleryException>().Single().Code);
                Assert.AreEqual(1,(await service.GetTaskAsync(task.id)).desiredAction);
                await service.WaitForIdleAsync();
                Assert.AreEqual(BackupState.Paused,(await service.GetTaskAsync(task.id)).state);
                Assert.AreEqual(BackupState.Completed,(await service.GetTaskAsync(unrelated)).state);
                Assert.IsEmpty((await automatic.GetPreparationFailuresAsync()).Items);
                Assert.Greater((await library.GetPositionAsync(scope)).PermissionGeneration,before.PermissionGeneration);
                gallery.DeniedId=null;
                await library.ShutdownAsync();library=await ImageLibraryIndex.OpenAsync(database);
                automatic=await AutomaticImageBackup.CreateAsync(service,library);
                var confirmation=await Observe(automatic.ScanOnceAsync());
                Assert.IsInstanceOf<GalleryException>(confirmation);
                Assert.AreEqual("ScopeConfirmationRequired",((GalleryException)confirmation).Code);
                await automatic.ConfirmScopeAsync();await automatic.ScanOnceAsync();
                await service.WaitForIdleAsync();
                Assert.AreEqual(BackupState.Paused,(await service.GetTaskAsync(task.id)).state);
                var tasks=(await service.QueryTasksAsync()).Items;
                Assert.AreEqual(4,tasks.Count); // Keep the failed preparation and the newly authorized submission.
                Assert.AreEqual(1,tasks.Count(x=>x.state==BackupState.Failed));
                Assert.AreEqual(1,tasks.Count(x=>x.source=="library:first"));
            } finally {await library.ShutdownAsync();await service.ShutdownAsync();}
        });
    }
}

namespace UIFrame.Regression
{
 public sealed class MediaResultOwnershipTests
 {
  [System.Runtime.InteropServices.DllImport("libc",SetLastError=true)] static extern int chmod(string path,uint mode);
  string root;
  [SetUp] public void Setup(){if(Application.platform!=RuntimePlatform.OSXEditor && Application.platform!=RuntimePlatform.LinuxEditor)Assert.Ignore("Real directory permission failure requires a POSIX host.");root=Path.Combine(Path.GetTempPath(),"uiframe-delivery-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);}
  [TearDown] public void Cleanup(){if(root!=null){chmod(root,448);Directory.Delete(root,true);}}
  ImageReference Source(out ImageStorage owner)
  {
   string path=Path.Combine(root,"image.png");var texture=new Texture2D(47,31,TextureFormat.RGBA32,false);
   try {File.WriteAllBytes(path,texture.EncodeToPNG());}finally {UnityEngine.Object.DestroyImmediate(texture);}
   var metadata=ImageReference.FromFile(path);owner=new ImageStorage(root);
   return new ImageReference("file",path,metadata.FileName,metadata.MimeType,metadata.ByteCount.Value,version:metadata.Version,owner:owner);
  }
  [UnityTest] public IEnumerator PreviewMustReleaseResultWhenSourceReleaseFails()=>UniTask.ToCoroutine(async()=>
  {
   var image=Source(out var owner);var before=Resources.FindObjectsOfTypeAll<Texture2D>().Select(t=>t.GetInstanceID()).ToHashSet();
   var task=GameImageReader.LoadPreviewAsync(image,new ImagePreviewOptions {MaxEdge=64});
   Assert.AreEqual(0,chmod(root,320));owner.Dispose();
   ImageTexture result=null;Exception failure=null;
   try {result=await task;}catch(Exception error){failure=error;}
   var leaked=Resources.FindObjectsOfTypeAll<Texture2D>().Where(t=>!before.Contains(t.GetInstanceID()) && t.width==47 && t.height==31).ToArray();
   TestContext.WriteLine($"failure={failure}; delivered={result!=null}; retained textures={leaked.Length}");
   try {Assert.IsNotNull(failure,"The real source-directory cleanup must fail for this probe.");Assert.IsNull(result);Assert.IsEmpty(leaked,"A failed result handoff must release the newly generated texture.");}
   finally {chmod(root,448);result?.Dispose();foreach(var t in leaked)if(t!=null)UnityEngine.Object.DestroyImmediate(t);}
  });
  [UnityTest] public IEnumerator SourceCleanupFailureDoesNotReplaceCancellation()=>UniTask.ToCoroutine(async()=>
  {
   var image=Source(out var owner);
   var gate=(SemaphoreSlim)typeof(GameImageReader).GetField("processingGate",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic).GetValue(null);
   await gate.WaitAsync();
   using var canceled=new CancellationTokenSource();ImageTexture result=null;
   try {
    var task=GameImageReader.LoadPreviewAsync(image,cancellationToken:canceled.Token);
    Assert.AreEqual(0,chmod(root,320));owner.Dispose();
    LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("(IOException|UnauthorizedAccessException):"));
    canceled.Cancel();Exception failure=null;
    try {result=await task;}catch(Exception error){failure=error;}
    Assert.IsInstanceOf<OperationCanceledException>(failure);
    Assert.AreEqual(canceled.Token,((OperationCanceledException)failure).CancellationToken);
    Assert.IsNull(result);
   } finally {chmod(root,448);result?.Dispose();gate.Release();}
   using var next=await GameImageReader.LoadPreviewAsync(ImageReference.FromFile(image.Id),new ImagePreviewOptions {MaxEdge=64});
   Assert.AreEqual(47,next.Texture.width);
  });
  [UnityTest] public IEnumerator ExportMustReleaseResultWhenSourceReleaseFails()=>UniTask.ToCoroutine(async()=>
  {
   var image=Source(out var owner);
   string initialization=ImagePaths.NewDirectory();string session=Directory.GetParent(initialization).FullName;Directory.Delete(initialization);
   var before=Directory.GetDirectories(session).ToHashSet();
   var task=GameImageReader.ExportFileAsync(image,new ImageExportOptions {Mode=ImageExportMode.PreserveProvidedBytes});
   Assert.AreEqual(0,chmod(root,320));owner.Dispose();
   ImageFile result=null;Exception failure=null;
   try {result=await task;}catch(Exception error){failure=error;}
   var leaked=Directory.GetDirectories(session).Where(p=>!before.Contains(p)).ToArray();
   TestContext.WriteLine($"failure={failure}; delivered={result!=null}; retained export directories={leaked.Length}");
   try {Assert.IsNotNull(failure,"The real source-directory cleanup must fail for this probe.");Assert.IsNull(result);Assert.IsEmpty(leaked,"A failed result handoff must release the generated export file.");}
   finally {chmod(root,448);result?.Dispose();foreach(string p in leaked)Directory.Delete(p,true);}
  });
 }
}
