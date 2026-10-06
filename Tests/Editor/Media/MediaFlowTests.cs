using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Media;
using Game.Media.Backup;
using NUnit.Framework;
using UIFrame.Sqlite;
using UnityEngine;
using UnityEngine.TestTools;

namespace UIFrame.Regression
{
    public sealed class MediaFlowTests
    {
        string root;
        [SetUp] public void Setup(){root=Path.Combine(Path.GetTempPath(),"uiframe-flow-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);}
        [TearDown] public void Cleanup(){Directory.Delete(root,true);}
        string Photo(string name,string directory=null)
        {
            string path=Path.Combine(directory??root,name+".jpg");File.WriteAllBytes(path,new byte[]{1,2,3,4});return path;
        }
        BackupConfiguration Config()=>new BackupConfiguration {ServerUrl="http://127.0.0.1:18787",Account="audit",AccessToken=()=>"fixture",StorageDirectory=Path.Combine(root,"backup"),AllowDevelopmentHttp=true};
        static async UniTask<Exception> Observe(UniTask task){try{await task;return null;}catch(Exception error){return error;}}

        sealed class Transport : IMediaTransport,IDisposable
        {
            readonly IMediaTransport previous=NativeMedia.Transport;
            readonly Dictionary<string,MediaRequest> pending=new Dictionary<string,MediaRequest>();
            readonly HashSet<string> canceled=new HashSet<string>();
            readonly Dictionary<string,string> ready=new Dictionary<string,string>();
            internal string HeldOperation,HeldId,Access="Authorized",RejectOnceOperation;
            internal readonly List<string> Operations=new List<string>();
            internal MediaItem[] Images=Array.Empty<MediaItem>();
            internal bool OpenEnded,DrainBeforeOpen,CloseBeforeOpen;
            internal Transport(){NativeMedia.Transport=this;}
            public void Start(string json)
            {
                var request=JsonUtility.FromJson<MediaRequest>(json);pending.Add(request.id,request);
                Operations.Add(request.op);
                if(request.op==HeldOperation){HeldId=request.id;return;}
                if(request.op=="drain" && HeldOperation=="observe" && !OpenEnded)DrainBeforeOpen=true;
                if(request.op=="imagesClose" && HeldOperation=="imagesOpen" && !OpenEnded)CloseBeforeOpen=true;
                Complete(request.id);
            }
            void Complete(string id)
            {
                var request=pending[id];
                if(canceled.Remove(id)){pending.Remove(id);return;}
                bool protectedOperation=request.op=="observe" || request.op=="drain" || request.op=="imagesOpen" || request.op=="imagesNext";
                if(request.op==RejectOnceOperation || protectedOperation && Access!="Authorized" && Access!="Limited")
                {
                    RejectOnceOperation=null;
                    ready[id]=JsonUtility.ToJson(new MediaResponse {status="error",code="PermissionDenied",error="Native photo access denied"});return;
                }
                ready[id]=JsonUtility.ToJson(new MediaResponse {status="ok",access=Access,boundary="stable",items=request.op=="imagesNext"?Images:Array.Empty<MediaItem>()});
            }
            internal void Release(){OpenEnded=true;Complete(HeldId);}
            public string Poll(string id)
            {
                if(!ready.TryGetValue(id,out var response))return null;
                ready.Remove(id);pending.Remove(id);return response;
            }
            public void Cancel(string id){if(ready.Remove(id))pending.Remove(id);else if(pending.ContainsKey(id))canceled.Add(id);}
            public bool Pending(string id)=>pending.ContainsKey(id);
            public void Dispose(){NativeMedia.Transport=previous;Assert.IsEmpty(pending,"Native resources are still owned.");}
        }
        [UnityTest] public IEnumerator UnknownVersionCannotEnterBaselineOrIncrementalDiscoveries()=>UniTask.ToCoroutine(async()=>
        {
            var image=new MediaItem {id="photo",source="library",name="photo.jpg",mime="image/jpeg",size=4};
            using var native=new Transport {Images=new[]{image}};
            var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"index.sqlite"));
            var service=await ImageBackupService.CreateAsync(Config());
            try {
                var automatic=await AutomaticImageBackup.CreateAsync(service,library);
                await automatic.ConfigureAsync(new AutomaticBackupPolicy {enabled=true,sourceKind=BackupSourceKind.PhotoLibrary,wifiOnly=false,includeExisting=false});
                var first=await Observe(automatic.ScanOnceAsync());
                Assert.IsInstanceOf<GalleryException>(first);Assert.AreEqual("ContentVersionUnavailable",((GalleryException)first).Code);
                image.version="v1";await automatic.ScanOnceAsync();
                image.version=null;
                var changed=await Observe(automatic.ScanOnceAsync());
                Assert.IsInstanceOf<GalleryException>(changed);Assert.AreEqual("ContentVersionUnavailable",((GalleryException)changed).Code);
                Assert.IsEmpty((await service.QueryTasksAsync()).Items);
            } finally {await service.ShutdownAsync();await library.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator LatePreparationAccessFailureBelongsToOriginalConfiguredSource()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Transport {RejectOnceOperation="stat",Images=new[]{new MediaItem {id="photo",source="library",name="photo.jpg",mime="image/jpeg",version="v1"}}};
            var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"index.sqlite"));
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,new BackupProtocolFixture());
            try {
                var automatic=await AutomaticImageBackup.CreateAsync(service,library);
                var oldScope=new ImageLibraryScope(ImageLibrarySourceKind.PhotoLibrary);
                await automatic.ConfigureAsync(new AutomaticBackupPolicy {enabled=true,sourceKind=BackupSourceKind.PhotoLibrary,includeExisting=true,wifiOnly=false});
                await automatic.ScanOnceAsync();long initial=(await library.GetPositionAsync(oldScope)).PermissionGeneration;
                await service.WaitForIdleAsync();
                string directory=Path.Combine(root,"new-source");Directory.CreateDirectory(directory);
                await automatic.ConfigureAsync(new AutomaticBackupPolicy {enabled=true,sourceKind=BackupSourceKind.Directory,source=directory,includeExisting=true,wifiOnly=false});
                var failure=await Observe(automatic.ScanOnceAsync());Assert.IsInstanceOf<BackupSubmissionException>(failure);
                Assert.IsTrue(((BackupSubmissionException)failure).InnerExceptions.OfType<GalleryException>().Any(e=>e.Code=="PermissionDenied"));
                Assert.Greater((await library.GetPositionAsync(oldScope)).PermissionGeneration,initial);
                await automatic.ScanOnceAsync();
                string newScope=ImageBackupService.Hash(new ImageLibraryScope(ImageLibrarySourceKind.Directory,directory).Id+":"+true);
                Assert.IsFalse((await service.Db(Game.Media.Backup.BackupRepository.Command.ScopeState,default,newScope)).Single.Flag("requires_confirmation"));
            } finally {await service.ShutdownAsync();await library.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator WatchThenRefreshWaitsForRegistration()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Transport {HeldOperation="observe"};
            var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"index.sqlite"));
            var scope=new ImageLibraryScope(ImageLibrarySourceKind.PhotoLibrary);var watch=library.Watch(scope,_=>{});
            var refresh=library.RefreshAsync(scope).AsTask();
            try {
                await UniTask.Yield();Assert.IsNotNull(native.HeldId);Assert.IsFalse(native.DrainBeforeOpen);Assert.IsFalse(refresh.IsCompleted);
                native.Release();await refresh;Assert.IsFalse(native.DrainBeforeOpen);
            } finally {if(!native.OpenEnded)native.Release();await refresh;await watch.CloseAsync();await library.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator CancellationWaitsForNativeEndBeforeClosingScan()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Transport {HeldOperation="imagesOpen"};using var cancel=new CancellationTokenSource();
            var visit=Observe(GameGallery.VisitImagesAsync(_=>UniTask.FromResult(true),cancellationToken:cancel.Token).AsUniTask()).AsTask();
            cancel.Cancel();await UniTask.Yield();Assert.IsFalse(visit.IsCompleted);Assert.IsFalse(native.CloseBeforeOpen);
            native.Release();Assert.IsInstanceOf<OperationCanceledException>(await visit);Assert.IsFalse(native.CloseBeforeOpen);
        });
        [UnityTest] public IEnumerator LimitedConfirmationTracksMembershipAcrossObserverAndIndexReopen()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Transport {Access="Limited",Images=new[]{new MediaItem {id="photo",source="library",name="photo.jpg",mime="image/jpeg",version="v1"}}};
            string path=Path.Combine(root,"index.sqlite");var library=await ImageLibraryIndex.OpenAsync(path);var service=await ImageBackupService.CreateAsync(Config());
            try {
                var automatic=await AutomaticImageBackup.CreateAsync(service,library);
                await automatic.ConfigureAsync(new AutomaticBackupPolicy {enabled=true,sourceKind=BackupSourceKind.PhotoLibrary,wifiOnly=false});
                await automatic.ScanOnceAsync();var scope=new ImageLibraryScope(ImageLibrarySourceKind.PhotoLibrary);
                long initial=(await library.GetPositionAsync(scope)).PermissionGeneration;
                await automatic.ConfirmScopeAsync();await automatic.ScanOnceAsync();await automatic.ScanOnceAsync();
                Assert.AreEqual(initial,(await library.GetPositionAsync(scope)).PermissionGeneration);
                await library.ShutdownAsync();library=await ImageLibraryIndex.OpenAsync(path);automatic=await AutomaticImageBackup.CreateAsync(service,library);
                await automatic.ScanOnceAsync();Assert.AreEqual(initial,(await library.GetPositionAsync(scope)).PermissionGeneration);
                native.Images=Array.Empty<MediaItem>();
                var failure=await Observe(automatic.ScanOnceAsync());Assert.AreEqual("ScopeConfirmationRequired",((GalleryException)failure).Code);
                long changed=(await library.GetPositionAsync(scope)).PermissionGeneration;Assert.Greater(changed,initial);
                await automatic.ConfirmScopeAsync();await automatic.ScanOnceAsync();
                Assert.AreEqual(changed,(await library.GetPositionAsync(scope)).PermissionGeneration);
            } finally {await library.ShutdownAsync();await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator RevokedPermissionRequiresConfirmationAfterIndexReopen()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Transport();string path=Path.Combine(root,"index.sqlite");
            var library=await ImageLibraryIndex.OpenAsync(path);var service=await ImageBackupService.CreateAsync(Config());
            var scope=new ImageLibraryScope(ImageLibrarySourceKind.PhotoLibrary);
            try {
                var automatic=await AutomaticImageBackup.CreateAsync(service,library);
                await automatic.ConfigureAsync(new AutomaticBackupPolicy {enabled=true,sourceKind=BackupSourceKind.PhotoLibrary,wifiOnly=false});
                await automatic.ScanOnceAsync();long initial=(await library.GetPositionAsync(scope)).PermissionGeneration;
                native.Access="Denied";native.Operations.Clear();
                Assert.AreEqual("PermissionDenied",((GalleryException)await Observe(library.RefreshAsync(scope).AsUniTask())).Code);
                Assert.IsFalse(native.Operations.Contains("observe"),"Known denied access must be recorded before observing.");
                long denied=(await library.GetPositionAsync(scope)).PermissionGeneration;Assert.Greater(denied,initial);
                Assert.AreEqual("PermissionDenied",((GalleryException)await Observe(library.RefreshAsync(scope).AsUniTask())).Code);
                Assert.AreEqual(denied,(await library.GetPositionAsync(scope)).PermissionGeneration,"The same denial must not advance permission twice.");
                await library.ShutdownAsync();library=await ImageLibraryIndex.OpenAsync(path);native.Access="Authorized";
                automatic=await AutomaticImageBackup.CreateAsync(service,library);
                Assert.AreEqual("ScopeConfirmationRequired",((GalleryException)await Observe(automatic.ScanOnceAsync())).Code);
                await automatic.ConfirmScopeAsync();long confirmed=(await library.GetPositionAsync(scope)).PermissionGeneration;
                await automatic.ScanOnceAsync();Assert.AreEqual(confirmed,(await library.GetPositionAsync(scope)).PermissionGeneration);
                Assert.Greater(confirmed,denied);
            } finally {await library.ShutdownAsync();await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator NativeDenialDuringRefreshSurvivesImmediatePermissionRestoration()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Transport();var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"index.sqlite"));
            var scope=new ImageLibraryScope(ImageLibrarySourceKind.PhotoLibrary);
            try {
                await library.RefreshAsync(scope);
                foreach(string operation in new[]{"observe","drain","imagesOpen","imagesNext"})
                {
                    long before=(await library.GetPositionAsync(scope)).PermissionGeneration;native.RejectOnceOperation=operation;
                    // access already reports Authorized again when the native refusal arrives.
                    var error=(GalleryException)await Observe(library.RefreshAsync(scope).AsUniTask());
                    Assert.AreEqual("PermissionDenied",error.Code,operation);Assert.AreEqual("Native photo access denied",error.Message);
                    long denied=(await library.GetPositionAsync(scope)).PermissionGeneration;Assert.AreEqual(before+1,denied,operation);
                    await library.RefreshAsync(scope);Assert.AreEqual(denied+1,(await library.GetPositionAsync(scope)).PermissionGeneration,operation);
                }
            } finally {await library.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator WatchDenialRecordsPermissionBeforeExplicitResume()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Transport();var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"index.sqlite"));
            var scope=new ImageLibraryScope(ImageLibrarySourceKind.PhotoLibrary);ImageLibraryIndex.WatchHandle watch=null;
            try {
                await library.RefreshAsync(scope);long initial=(await library.GetPositionAsync(scope)).PermissionGeneration;
                native.RejectOnceOperation="drain";
                LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("GalleryException: Native photo access denied"));
                watch=library.Watch(scope,_=>{});using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await UniTask.WaitUntil(()=>watch.Failure!=null,cancellationToken:timeout.Token);
                Assert.AreEqual("PermissionDenied",((GalleryException)watch.Failure).Code);
                Assert.AreEqual(initial+1,(await library.GetPositionAsync(scope)).PermissionGeneration);
                int requests=native.Operations.Count;await UniTask.Delay(150,ignoreTimeScale:true);
                Assert.AreEqual(requests,native.Operations.Count,"A failed observation must not retry itself.");
                watch.Resume();await library.RefreshAsync(scope);Assert.IsNull(watch.Failure);
                Assert.AreEqual(initial+2,(await library.GetPositionAsync(scope)).PermissionGeneration);
            } finally {if(watch!=null)await watch.CloseAsync();await library.ShutdownAsync();}
        });
        static async UniTask ConfigureDatabase(string path,params string[] statements)
        {
            var database=await SqliteDatabase.OpenAsync(new SqliteOpenOptions(path,SqliteOpenMode.OpenExistingReadWrite));
            try {using(await database.ExecuteTransactionAsync(new SqliteBatch(new SqliteQueryBudget(),statements.Select(sql=>new SqliteCommand(sql)).ToArray()))) {}}
            finally {await database.CloseAsync();}
        }
        [UnityTest] public IEnumerator NativePermissionErrorRemainsPrimaryWhenRecordingFails()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Transport();string path=Path.Combine(root,"index.sqlite");
            var library=await ImageLibraryIndex.OpenAsync(path);var scope=new ImageLibraryScope(ImageLibrarySourceKind.PhotoLibrary);
            try {
                await library.RefreshAsync(scope);await library.ShutdownAsync();
                await ConfigureDatabase(path,"CREATE TRIGGER review_access_failure BEFORE UPDATE OF access_state ON library_scopes WHEN NEW.access_state=1 BEGIN SELECT RAISE(ABORT,'access persistence fixture'); END");
                library=await ImageLibraryIndex.OpenAsync(path);native.RejectOnceOperation="imagesNext";
                LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("access persistence fixture"));
                var error=await Observe(library.RefreshAsync(scope).AsUniTask());
                Assert.IsInstanceOf<GalleryException>(error);Assert.AreEqual("Native photo access denied",error.Message);
                Assert.IsTrue((await library.GetPositionAsync(scope)).RequiresRefresh);
            } finally {await library.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator InterruptedRefreshDeliversPreviouslyCommittedChanges()=>UniTask.ToCoroutine(async()=>
        {
            for(int i=0;i<40;i++)Photo("image"+i);
            var images=new List<ImageReference>();await GameImageDirectory.VisitAsync(root,p=>{images.AddRange(p);return UniTask.FromResult(true);});
            var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"index.sqlite"));var scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,root);
            var delivered=new HashSet<string>();bool invalidated=false;
            var watch=library.Watch(scope,b=>{invalidated|=b.RequiresRefresh;foreach(var item in b.Items)delivered.Add(item.SourceIdentity);});
            try {
                using(var locked=new FileStream(images[32].Id,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                    Assert.IsNotNull(await Observe(library.RefreshAsync(scope).AsUniTask()));
                Assert.AreEqual(32,(await library.QueryAsync(scope)).Items.Count);
                await library.RefreshAsync(scope);
                Assert.IsTrue(invalidated || delivered.Count==40,"Committed changes were skipped before notification.");
                Assert.AreEqual(40,(await library.QueryAsync(scope)).Items.Count);
            } finally {await watch.CloseAsync();await library.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator AutomaticScopeOperationsHaveSymmetricAdmission()=>UniTask.ToCoroutine(async()=>
        {
            string oldDir=Path.Combine(root,"old"),newDir=Path.Combine(root,"new");Directory.CreateDirectory(oldDir);Directory.CreateDirectory(newDir);Photo("old",oldDir);Photo("new",newDir);
            var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"index.sqlite"));var service=await ImageBackupService.CreateAsync(Config());
            try {
                var automatic=await AutomaticImageBackup.CreateAsync(service,library);
                await automatic.ConfigureAsync(new AutomaticBackupPolicy {enabled=true,sourceKind=BackupSourceKind.Directory,source=oldDir,wifiOnly=false,includeExisting=true});
                var policy=new AutomaticBackupPolicy {enabled=true,sourceKind=BackupSourceKind.Directory,source=newDir,wifiOnly=false,includeExisting=true};
                var configuration=automatic.ConfigureAsync(policy);policy.source=oldDir;
                Assert.IsInstanceOf<InvalidOperationException>(await Observe(automatic.ScanOnceAsync()));
                await configuration;var scan=automatic.ScanOnceAsync();
                Assert.IsInstanceOf<InvalidOperationException>(await Observe(automatic.ConfigureAsync(policy)));
                Assert.IsInstanceOf<InvalidOperationException>(await Observe(automatic.ConfirmScopeAsync()));
                Assert.IsInstanceOf<InvalidOperationException>(await Observe(automatic.ResetScopeAsync()));
                Assert.IsInstanceOf<InvalidOperationException>(await Observe(automatic.RetryPreparationFailuresAsync()));
                await scan;Assert.AreEqual(newDir,automatic.Policy.source);
                StringAssert.Contains("new.jpg",(await service.QueryTasksAsync()).Items.Single().source);
            } finally {await library.ShutdownAsync();await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator PreparationCleanupFailuresCanBeListedAfterReopenAndRetriedByFile()=>UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config());
            try {
                string owner=(string)typeof(ImageBackupService).GetField("owner",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(service);
                await service.Db(BackupRepository.Command.Prepare,default,"aa",owner,DateTime.UtcNow.Ticks,2,"b1","file:first","v1","first.jpg","image/jpeg","b2","file:second","v1","second.jpg","image/jpeg","",0L);
                string folder=Path.Combine(service.RepositoryRoot,service.StoreId,"payloads");
                foreach(var id in new[]{"b1","b2"}) {
                    await service.Db(BackupRepository.Command.TryPrepare,default,id,owner,4L,4L,100L,DateTime.UtcNow.Ticks);
                    Directory.CreateDirectory(Path.Combine(folder,id+".payload"));File.WriteAllText(Path.Combine(folder,id+".payload","occupied"),"fixture");
                    await service.Db(BackupRepository.Command.FailItem,default,id,owner,"interrupted preparation",DateTime.UtcNow.Ticks);
                }
                LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("BackupRepositoryException"));LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("BackupRepositoryException"));
                await service.CleanupFilesAsync();await service.ShutdownAsync();service=await ImageBackupService.CreateAsync(Config());
                Assert.IsTrue((await service.QueryTasksAsync()).Items.All(x=>x.state==BackupState.Failed));
                var first=await service.QueryCleanupFailuresAsync(1);var second=await service.QueryCleanupFailuresAsync(1,first.Next);
                Assert.AreEqual("b1",first.Items.Single().FileId);Assert.AreEqual("b2",second.Items.Single().FileId);
                Assert.AreEqual("b1",first.Items[0].TaskId);Assert.IsNotEmpty(first.Items[0].Error);
                Directory.Delete(Path.Combine(folder,"b1.payload"),true);await service.RetryCleanupAsync(first.Items[0].FileId);
                Assert.AreEqual("b2",(await service.QueryCleanupFailuresAsync()).Items.Single().FileId);
                Assert.AreEqual(4,(await new BackupMaintenance(service).PreviewAsync()).CleanupFailedBytes);
            } finally {await service.ShutdownAsync();}
        });
    }
}
