using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Game.Media;
using Game.Media.Backup;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Command=Game.Media.Backup.BackupRepository.Command;

namespace UIFrame.Regression
{
    public sealed class BackupAdmissionTests
    {
        string root;
        [SetUp] public void Setup(){root=Path.Combine(Path.GetTempPath(),"uiframe-admission-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);}
        [TearDown] public void Cleanup(){Directory.Delete(root,true);}
        BackupConfiguration Config()=>new BackupConfiguration {ServerUrl="https://api.invalid",Account="integration-user",AccessToken=()=>"fixture",StorageDirectory=Path.Combine(root,"backup"),DiskBudgetBytes=10};
        ImageReference Photo(string name,int size=6){string path=Path.Combine(root,name+".jpg");File.WriteAllBytes(path,new byte[size]);return ImageReference.FromFile(path);}
        static async UniTask Until(Func<bool> predicate){using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));await UniTask.WaitUntil(predicate,cancellationToken:deadline.Token);}
        static async UniTask<BackupSubmissionException> Failure(BackupSubmission submission)
        {try{using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await submission.WaitAsync(timeout.Token);return null;}catch(BackupSubmissionException error){return error;}}
        async UniTask Occupy(ImageBackupService service)
        {service.SetDesktopNetworkPolicy(()=>false);await service.SubmitAndWaitAsync(Guid.NewGuid().ToString("N"),new[]{Photo("occupied")});}

        async UniTask SeedAccepted(ImageBackupService service)
        {
            string owner=(string)typeof(ImageBackupService).GetField("owner",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(service);
            long now=DateTime.UtcNow.Ticks;
            await service.Db(Command.Prepare,default,"ee",owner,now,1,"cc","file:occupied","v1","occupied.jpg","image/jpeg","",0L);
            await service.Db(Command.TryPrepare,default,"cc",owner,6L,10L,10L,now);
            File.WriteAllBytes(Path.Combine(service.RepositoryRoot,service.StoreId,"payloads/cc.payload"),new byte[6]);
            await service.Db(Command.Seal,default,"cc",owner,6L,new string('a',64),"image/jpeg",10L,now);
            await service.Db(Command.AcceptItem,default,"cc",owner,now);
        }
        [UnityTest] public IEnumerator FailedOperationDriverRejectsAdmissionButPreservesQueriesAndReleasesOnShutdown()=>UniTask.ToCoroutine(async()=>{
            var config=Config();config.EnableNativeBackgroundTransfer=true;
            var original=new IOException("Operation synchronization unavailable");int syncs=0;bool shutdown=false;
            var service=await ImageBackupService.CreateAsync(config,request=>{
                if(request.op=="root")return new NativeBackupStatus {root=config.StorageDirectory};
                if(request.op=="sync"){Interlocked.Increment(ref syncs);throw original;}
                Assert.AreEqual("wake",request.op);return new NativeBackupStatus {exists=true};
            },true,new BackupProtocolFixture());
            try {
                LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("IOException: Operation synchronization unavailable"));
                await service.SubmitOperationAsync(new BackupOperationCommand {OperationId="aa",Action=BackupAction.Pause});
                var field=typeof(ImageBackupService).GetField("operationDriveFailure",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
                await Until(()=>field.GetValue(service)!=null);
                foreach(string id in new[]{"bb","aa"}) {
                    Exception failure=null;try{await service.SubmitOperationAsync(new BackupOperationCommand {OperationId=id,Action=BackupAction.Pause});}catch(Exception error){failure=error;}
                    Assert.AreSame(original,failure);
                }
                Assert.IsNull(await service.QueryOperationAsync("bb"),"Failed admission must not persist an undriven operation.");
                Assert.AreEqual(BackupOperationPhase.Completed,(await service.WaitOperationAsync("aa")).Phase,"An already completed result must survive the later synchronization failure.");
                Assert.IsEmpty((await service.QueryTasksAsync()).Items);Assert.AreEqual(1,syncs);
                Exception closeFailure=null;try{await service.ShutdownAsync();}catch(Exception error){closeFailure=error;}finally{shutdown=true;}
                Assert.AreSame(original,closeFailure);
                var reopened=await ImageBackupService.CreateAsync(config,request=>request.op=="root"?new NativeBackupStatus {root=config.StorageDirectory}:new NativeBackupStatus {exists=true},true,new BackupProtocolFixture());
                try {await reopened.SubmitOperationAsync(new BackupOperationCommand {OperationId="bb",Action=BackupAction.Pause});Assert.AreEqual(BackupOperationPhase.Completed,(await reopened.WaitOperationAsync("bb")).Phase);}
                finally {await reopened.ShutdownAsync();}
            } finally {if(!shutdown)try{await service.ShutdownAsync();}catch(Exception error){Assert.AreSame(original,error);}}
        });
        [UnityTest] public IEnumerator CapacitySchedulingFailureIsNotRetriedDuringFinalization()=>UniTask.ToCoroutine(async()=>{
            var config=Config();config.EnableNativeBackgroundTransfer=true;
            var original=new IOException("Native scheduler unavailable");int wakes=0;
            var service=await ImageBackupService.CreateAsync(config,request=>{
                if(request.op=="root")return new NativeBackupStatus {root=config.StorageDirectory};
                Assert.AreEqual("wake",request.op);
                if(Interlocked.Increment(ref wakes)>1)throw original;
                return new NativeBackupStatus {exists=true};
            },true,new BackupProtocolFixture());
            try {
                await SeedAccepted(service);
                var request=await service.SubmitAsync("aa",new[]{Photo("waiting")});
                var failure=await Failure(request);Assert.IsNotNull(failure);
                Assert.AreEqual(2,wakes,"One startup wake and exactly one failed preparation wake are allowed.");
                Assert.AreEqual(1,failure.InnerExceptions.Count);Assert.AreSame(original,failure.InnerExceptions[0]);
                Assert.AreEqual(BackupState.Failed,failure.Result.Items.Single().State);
                Assert.AreEqual(128,service.AvailablePreparationSlots);Assert.IsFalse(service.IsWaitingForCapacity);
                Assert.AreEqual(BackupState.Queued,(await service.GetTaskAsync("cc")).state);
                Assert.IsTrue(File.Exists(Path.Combine(service.RepositoryRoot,service.StoreId,"payloads/cc.payload")));
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator DesktopPreservesAggregationWindowWhileAnotherPhotoIsPreparing()=>UniTask.ToCoroutine(async()=>{
            using var native=new HeldExport();var server=new BackupProtocolFixture();
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            try {
                await SeedAccepted(service);
                // Fix the repository's aggregation deadline in the future so the
                // assertion depends on completed driver passes, not frame speed.
                await service.Db(Command.Claim,default,"cc",0,false,DateTime.UtcNow.AddMinutes(1).Ticks);
                await service.Db(Command.Handoff,default,"cc",1L,"fixture-protected-reference");
                var request=await service.SubmitAsync("aa",new[]{new ImageReference("library","held","held.jpg","image/jpeg",1,version:"v1")});
                await Until(()=>native.Export!=null);
                int passes=0;service.SetDesktopNetworkPolicy(()=>{passes++;return true;});
                await service.SynchronizeNativeAsync();await Until(()=>passes>=1);
                await service.SynchronizeNativeAsync();await Until(()=>passes>=2);
                var task=(await service.Db(Command.Task,default,"cc")).Single;
                Assert.IsNull(task.Text("control_id"));Assert.AreEqual((long)BackupState.Uploading,task.Number("state"));
                Assert.AreEqual(0,server.Plans);Assert.IsFalse(request.IsCompleted);
            } finally {native.Released=true;await service.ShutdownAsync();}
        });

        [UnityTest] public IEnumerator RegistrationRetainsAllTemporarySourcesUntilActualPreparationEnds()=>UniTask.ToCoroutine(async()=>{
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,new BackupProtocolFixture());
            ImageSelection selection=null;
            try {
                await Occupy(service);string directory=Path.Combine(root,"selection");Directory.CreateDirectory(directory);var storage=new ImageStorage(directory);
                var photos=new List<ImageReference>();
                for(int i=0;i<2;i++) {string path=Path.Combine(directory,i+".jpg");File.WriteAllBytes(path,new byte[1]);var m=ImageReference.FromFile(path);photos.Add(new ImageReference("file",path,m.FileName,m.MimeType,version:m.Version,owner:storage));}
                selection=new ImageSelection(photos.ToArray(),storage);
                var request=await service.SubmitAsync(Guid.NewGuid().ToString("N"),selection.Items);selection.Dispose();selection=null;
                await Until(()=>service.IsWaitingForCapacity);Assert.IsTrue(Directory.Exists(directory));
                Assert.AreEqual(BackupCapacityWaitReason.StagingBudget,service.CapacityWait.Reasons);Assert.AreEqual(10,service.CapacityWait.RequiredBytes);
                service.SetDesktopNetworkPolicy(()=>true);await request.WaitAsync();await service.WaitForIdleAsync();
                Assert.IsFalse(Directory.Exists(directory));Assert.AreEqual(3,(await service.QueryBackupsAsync()).Items.Count);
            } finally {selection?.Dispose();await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator QueuedCancellationCompletesWithoutWaitingForHeadCapacity()=>UniTask.ToCoroutine(async()=>{
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,new BackupProtocolFixture());
            BackupSubmission head=null;
            try {
                await Occupy(service);head=await service.SubmitAsync(Guid.NewGuid().ToString("N"),new[]{Photo("head")});
                await Until(()=>service.IsWaitingForCapacity);
                var queued=await service.SubmitAsync(Guid.NewGuid().ToString("N"),new[]{Photo("queued",1)});
                await service.CancelPreparationAsync(queued.OperationId);var failure=await Failure(queued);
                Assert.IsNotNull(failure);Assert.IsFalse(head.IsCompleted);Assert.AreEqual(BackupAcceptanceStage.Registered,failure.Result.Items.Single().Acceptance);
                Assert.IsTrue(failure.InnerExceptions.Any(e=>e is OperationCanceledException));
                await service.CancelPreparationAsync(head.OperationId);Assert.IsNotNull(await Failure(head));
            } finally {await service.ShutdownAsync();if(head!=null)await Failure(head);}
        });
        [UnityTest] public IEnumerator AutomaticScanRegistersNewChangesWhilePreparationWaits()=>UniTask.ToCoroutine(async()=>{
            var uploadGate=new TaskCompletionSource<bool>();
            var server=new BackupProtocolFixture {BeforeUpload=async(_,token)=>await uploadGate.Task};
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"library.sqlite"));
            try {
                await Occupy(service);string directory=Path.Combine(root,"photos");Directory.CreateDirectory(directory);File.WriteAllBytes(Path.Combine(directory,"first.jpg"),new byte[6]);
                var automatic=await AutomaticImageBackup.CreateAsync(service,library,()=>true);
                await automatic.ConfigureAsync(new AutomaticBackupPolicy {enabled=true,sourceKind=BackupSourceKind.Directory,source=directory,includeExisting=true,wifiOnly=true});
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await automatic.ScanOnceAsync(deadline.Token);await Until(()=>service.IsWaitingForCapacity);
                File.WriteAllBytes(Path.Combine(directory,"second.jpg"),new byte[1]);await automatic.ScanOnceAsync(deadline.Token);
                Assert.IsTrue(service.IsWaitingForCapacity);Assert.AreEqual(3,(await service.QueryTasksAsync()).Items.Count);
                uploadGate.TrySetResult(true);await service.WaitForIdleAsync(deadline.Token);Assert.AreEqual(3,(await service.QueryBackupsAsync()).Items.Count);
            } finally {uploadGate.TrySetResult(true);await service.ShutdownAsync();await library.ShutdownAsync();}
        });
        sealed class HeldExport : IMediaTransport,IDisposable
        {
            readonly IMediaTransport previous=NativeMedia.Transport;
            readonly Dictionary<string,MediaRequest> requests=new Dictionary<string,MediaRequest>();
            internal bool Canceled,Released;
            internal string Export;
            internal HeldExport(){NativeMedia.Transport=this;}
            public void Start(string json){var r=JsonUtility.FromJson<MediaRequest>(json);requests.Add(r.id,r);if(r.op=="export"){Export=r.id;Directory.CreateDirectory(r.output);File.WriteAllBytes(Path.Combine(r.output,"partial.jpg"),new byte[2]);}}
            public string Poll(string id){var r=requests[id];if(r.op=="export")return null;requests.Remove(id);return JsonUtility.ToJson(new MediaResponse {status="ok",items=new[]{new MediaItem {version="v1"}}});}
            public void Cancel(string id){if(id==Export)Canceled=true;else requests.Remove(id);}
            public bool Pending(string id){if(id==Export && Released){requests.Remove(id);return false;}return requests.ContainsKey(id);}
            public void Dispose(){NativeMedia.Transport=previous;Assert.IsEmpty(requests);}
        }
        [UnityTest] public IEnumerator PauseWaitsForActualProducerReleaseAndDoesNotReviveStoppedMembers()=>UniTask.ToCoroutine(async()=>{
            using var native=new HeldExport();var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,new BackupProtocolFixture());
            try {
                var request=await service.SubmitAsync("aa",new[]{new ImageReference("library","photo","photo.jpg","image/jpeg",version:"v1")});
                await Until(()=>native.Export!=null);var pause=service.PauseAsync().AsTask();await Until(()=>native.Canceled);
                Assert.IsFalse(pause.IsCompleted);Assert.IsFalse(request.IsCompleted);
                native.Released=true;await pause;Assert.IsNotNull(await Failure(request));
                await service.ResumeAsync();Assert.AreEqual(BackupState.Failed,(await service.QuerySubmissionAsync("aa")).Items.Single().State);
                var next=await service.SubmitAsync("bb",new[]{Photo("new",1)});await next.WaitAsync();await service.WaitForIdleAsync();
                Assert.AreEqual(1,(await service.QueryBackupsAsync()).Items.Count);
            } finally {native.Released=true;await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator RevokedScopeWaitsForActualExportReleaseBeforeReclaimingReservation()=>UniTask.ToCoroutine(async()=>{
            using var native=new HeldExport();var server=new BackupProtocolFixture();
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            try {
                await service.Db(Command.Scope,default,"aa","library:chosen","bb",1,1,1,true);
                await service.Db(Command.BeginScan,default,"aa","cc","bb",1,1,1,0);
                await service.Db(Command.ScanPage,default,"aa","cc",1,"library:photo","v1","photo.jpg","image/jpeg","photo",0);
                await service.Db(Command.ActivateScan,default,"aa","cc","bb",1,1,1,0,0,0);
                var request=await service.RegisterSubmission("dd",new[]{new ImageReference("library","photo","photo.jpg","image/jpeg",version:"v1")},"aa",1,default);
                await Until(()=>native.Export!=null);await service.SuspendPreparationScope("aa");await Until(()=>native.Canceled);
                Assert.IsFalse(request.IsCompleted);Assert.AreEqual(10,(await new BackupMaintenance(service).PreviewAsync()).PreparationBytes);
                native.Released=true;var failure=await Failure(request);Assert.IsNotNull(failure);
                Assert.IsTrue(failure.InnerExceptions.OfType<GalleryException>().Any(e=>e.IsScopeAccessFailure));Assert.AreEqual(0,server.Plans);
                var storage=await new BackupMaintenance(service).PreviewAsync();Assert.AreEqual(0,storage.PreparationBytes+storage.UploadBytes+storage.ReclaimableBytes);
            } finally {native.Released=true;await service.ShutdownAsync();}
        });
    }
}
