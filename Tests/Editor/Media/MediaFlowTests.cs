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
        sealed class Server : HttpMessageHandler
        {
            internal int Uploads;
            internal bool HoldFirst,FailUploads,MissingSession;
            internal readonly System.Threading.Tasks.TaskCompletionSource<bool> Release=new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            {
                if(request.RequestUri.AbsolutePath=="/v1/capabilities")return new HttpResponseMessage {Content=new StringContent("{\"protocolVersion\":1,\"account\":\"audit\",\"chunkBytes\":1024,\"maxFileBytes\":1048576}")};
                if(MissingSession && request.Method==HttpMethod.Get)return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) {Content=new StringContent("{}")};
                int number=Interlocked.Increment(ref Uploads);
                if(HoldFirst && number==1) {
                    var stopped=new System.Threading.Tasks.TaskCompletionSource<bool>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                    using(token.Register(()=>stopped.TrySetCanceled(token)))await await System.Threading.Tasks.Task.WhenAny(Release.Task,stopped.Task);
                }
                if(FailUploads)throw new HttpRequestException("controlled transport failure");
                var upload=JsonUtility.FromJson<UploadRequest>(await request.Content.ReadAsStringAsync());
                return new HttpResponseMessage {Content=new StringContent(JsonUtility.ToJson(new UploadResponse {uploadId=upload.key,sha256=upload.sha256,size=upload.size,offset=upload.size,completed=true,backupId=upload.key,capabilities=new ServerCapabilities {protocolVersion=1,account="audit",chunkBytes=1024,maxFileBytes=1048576}}))};
            }
        }
        static string StopAfterClaim(string id)=>$"CREATE TRIGGER review_stop_after_claim AFTER INSERT ON task_attempts WHEN NEW.task_id='{id}' BEGIN UPDATE tasks SET desired_action=1 WHERE id=NEW.task_id; END";
        static string FailRelease(string id)=>$"CREATE TRIGGER review_release_failure BEFORE UPDATE OF payload_released ON task_attempts WHEN NEW.task_id='{id}' AND NEW.payload_released=1 AND OLD.payload_released=0 BEGIN SELECT RAISE(ABORT,'release fixture'); END";
        [UnityTest] public IEnumerator StoppedBeforeStartPropagatesReleaseFailure()=>UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config());var server=new Server();
            try {
                var ids=await service.EnqueueAsync(new[]{ImageReference.FromFile(Photo("first")),ImageReference.FromFile(Photo("second"))});
                string catalog=Path.Combine(service.RepositoryRoot,service.StoreId,"catalog.sqlite");await service.ShutdownAsync();service=null;
                await ConfigureDatabase(catalog,StopAfterClaim(ids[0]),FailRelease(ids[0]));
                service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
                var error=await Observe(service.ProcessAsync());Assert.IsInstanceOf<BackupRepositoryException>(error);StringAssert.Contains("release fixture",error.Message);
                Assert.AreEqual(0,server.Uploads);Assert.IsFalse(service.IsRunning);
                var stopped=await service.GetTaskAsync(ids[0]);Assert.AreEqual(BackupState.Paused,stopped.state);
                var attempt=(await service.Db(BackupRepository.Command.Attempt,default,stopped.id,stopped.generation)).Single;
                Assert.AreEqual(0,attempt.Number("payload_released"));Assert.AreEqual(0,attempt.Number("credential_released"));
                Assert.AreEqual(BackupState.Queued,(await service.GetTaskAsync(ids[1])).state);
            } finally {if(service!=null)await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator StoppedBeforeStartReleasesAndContinuesOtherTasks()=>UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config());var server=new Server();
            try {
                var ids=await service.EnqueueAsync(new[]{ImageReference.FromFile(Photo("first")),ImageReference.FromFile(Photo("second"))});
                string catalog=Path.Combine(service.RepositoryRoot,service.StoreId,"catalog.sqlite");await service.ShutdownAsync();service=null;
                await ConfigureDatabase(catalog,StopAfterClaim(ids[0]));
                service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);await service.ProcessAsync();
                var stopped=await service.GetTaskAsync(ids[0]);Assert.AreEqual(BackupState.Paused,stopped.state);
                var attempt=(await service.Db(BackupRepository.Command.Attempt,default,stopped.id,stopped.generation)).Single;
                Assert.AreEqual(1,attempt.Number("payload_released"));Assert.AreEqual(1,attempt.Number("credential_released"));
                Assert.AreEqual(1,server.Uploads);Assert.AreEqual(BackupState.Completed,(await service.GetTaskAsync(ids[1])).state);
            } finally {if(service!=null)await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator CredentialErrorRemainsPrimaryWhenReleaseFails()=>UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config());var original=new InvalidOperationException("credential fixture");
            try {
                var ids=await service.EnqueueAsync(new[]{ImageReference.FromFile(Photo("first"))});
                string catalog=Path.Combine(service.RepositoryRoot,service.StoreId,"catalog.sqlite");await service.ShutdownAsync();service=null;
                await ConfigureDatabase(catalog,FailRelease(ids[0]));var config=Config();int reads=0;config.AccessToken=()=>++reads==1?"fixture":throw original;
                service=await ImageBackupService.CreateAsync(config,NativeBackup.Call,false,new Server());
                LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("release fixture"));
                Assert.AreSame(original,await Observe(service.ProcessAsync()));Assert.IsFalse(service.IsRunning);
                Assert.AreEqual(BackupState.Failed,(await service.GetTaskAsync(ids[0])).state);
            } finally {if(service!=null)await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator CancelCurrentUploadAllowsOtherTasksToComplete()=>UniTask.ToCoroutine(async()=>
        {
            var server=new Server {HoldFirst=true};var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            try {
                var ids=await service.EnqueueAsync(new[]{ImageReference.FromFile(Photo("first")),ImageReference.FromFile(Photo("second"))});
                var process=service.ProcessAsync().AsTask();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await UniTask.WaitUntil(()=>server.Uploads==1,cancellationToken:timeout.Token);await service.CancelAsync(ids[0]);await process;
                Assert.AreEqual(BackupState.NeedsAttention,(await service.GetTaskAsync(ids[0])).state);
                Assert.AreEqual(BackupState.Completed,(await service.GetTaskAsync(ids[1])).state);Assert.AreEqual(2,server.Uploads);
            } finally {server.Release.TrySetResult(true);await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator CancelQueuedTaskDoesNotInvalidateAlreadySelectedPage()=>UniTask.ToCoroutine(async()=>
        {
            var server=new Server {HoldFirst=true};var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            try {
                var ids=await service.EnqueueAsync(new[]{ImageReference.FromFile(Photo("first")),ImageReference.FromFile(Photo("second")),ImageReference.FromFile(Photo("third"))});
                var process=service.ProcessAsync().AsTask();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await UniTask.WaitUntil(()=>server.Uploads==1,cancellationToken:timeout.Token);await service.CancelAsync(ids[1]);server.Release.TrySetResult(true);await process;
                Assert.AreEqual(BackupState.Canceled,(await service.GetTaskAsync(ids[1])).state);
                Assert.AreEqual(BackupState.Completed,(await service.GetTaskAsync(ids[2])).state);Assert.AreEqual(2,server.Uploads);
            } finally {server.Release.TrySetResult(true);await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator CredentialFailureBeforeRequestIsDefinitive()=>UniTask.ToCoroutine(async()=>
        {
            var server=new Server();int reads=0;var original=new InvalidOperationException("credential fixture");var config=Config();config.AccessToken=()=>++reads==1?"fixture":throw original;
            var service=await ImageBackupService.CreateAsync(config,NativeBackup.Call,false,server);
            try {
                var ids=await service.EnqueueAsync(new[]{ImageReference.FromFile(Photo("photo"))});
                Assert.AreSame(original,await Observe(service.ProcessAsync()));Assert.AreEqual(0,server.Uploads);
                Assert.AreEqual(BackupState.Failed,(await service.GetTaskAsync(ids[0])).state);
                await service.CancelAsync(ids[0]);Assert.AreEqual(BackupState.Canceled,(await service.GetTaskAsync(ids[0])).state);
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator LocalFailureDuringRetryPreservesPriorUnknownUntilReconciled()=>UniTask.ToCoroutine(async()=>
        {
            var server=new Server {FailUploads=true};bool failCredential=false;var original=new InvalidOperationException("credential fixture");var config=Config();config.AccessToken=()=>failCredential?throw original:"fixture";
            var service=await ImageBackupService.CreateAsync(config,NativeBackup.Call,false,server);
            try {
                var ids=await service.EnqueueAsync(new[]{ImageReference.FromFile(Photo("photo"))});await service.ProcessAsync();
                Assert.AreEqual(BackupState.NeedsAttention,(await service.GetTaskAsync(ids[0])).state);
                await service.RetryAsync(ids[0]);await service.CancelAsync(ids[0]);
                Assert.AreEqual(BackupState.NeedsAttention,(await service.GetTaskAsync(ids[0])).state);
                await service.RetryAsync(ids[0]);failCredential=true;
                Assert.AreSame(original,await Observe(service.ProcessAsync()));Assert.AreEqual(1,server.Uploads);
                Assert.AreEqual(BackupState.NeedsAttention,(await service.GetTaskAsync(ids[0])).state);
                failCredential=false;server.MissingSession=true;
                await service.ReconcileTaskAsync(ids[0]);await service.CancelAsync(ids[0]);
                Assert.AreEqual(BackupState.Canceled,(await service.GetTaskAsync(ids[0])).state);
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator AbandonedCleanupFailuresCanBeListedAfterReopenAndRetriedByFile()=>UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config());
            try {
                string owner=(string)typeof(ImageBackupService).GetField("owner",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(service);
                await service.Db(BackupRepository.Command.Prepare,default,"aa",owner,DateTime.UtcNow.Ticks,2,"b1","file:first","v1","first.jpg","image/jpeg",4L,"b2","file:second","v1","second.jpg","image/jpeg",4L);
                string folder=Path.Combine(service.RepositoryRoot,service.StoreId,"payloads");
                foreach(var id in new[]{"b1","b2"}){Directory.CreateDirectory(Path.Combine(folder,id+".payload"));File.WriteAllText(Path.Combine(folder,id+".payload","occupied"),"fixture");}
                await service.Db(BackupRepository.Command.Abandon,default,"aa",owner,DateTime.UtcNow.Ticks,"interrupted preparation");
                LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("BackupRepositoryException"));LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("BackupRepositoryException"));
                await service.CleanupFilesAsync();await service.ShutdownAsync();service=await ImageBackupService.CreateAsync(Config());
                Assert.IsEmpty((await service.QueryTasksAsync()).Items);
                var first=await service.QueryCleanupFailuresAsync(1);var second=await service.QueryCleanupFailuresAsync(1,first.Next);
                Assert.AreEqual("b1",first.Items.Single().FileId);Assert.AreEqual("b2",second.Items.Single().FileId);
                Assert.IsNull(first.Items[0].TaskId);Assert.IsNotEmpty(first.Items[0].Error);
                Directory.Delete(Path.Combine(folder,"b1.payload"),true);await service.RetryCleanupAsync(first.Items[0].FileId);
                Assert.AreEqual("b2",(await service.QueryCleanupFailuresAsync()).Items.Single().FileId);
                Assert.AreEqual(4,(await new BackupMaintenance(service).PreviewAsync()).CleanupFailedBytes);
            } finally {await service.ShutdownAsync();}
        });
    }
}
