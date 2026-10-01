using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Media;
using Game.Media.Backup;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UIFrame.Regression
{
    public sealed class MediaLifecycleTests
    {
        string root,photo;
        [SetUp] public void Setup()
        {
            root=Path.Combine(Path.GetTempPath(),"uiframe-lifecycle-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            photo=Path.Combine(root,"photo.jpg");File.WriteAllBytes(photo,new byte[]{1,2,3,4});
        }
        [TearDown] public void Cleanup(){if(Directory.Exists(root))Directory.Delete(root,true);}
        BackupConfiguration Config()=>new BackupConfiguration {ServerUrl="http://127.0.0.1:18787",Account="audit",AccessToken=()=>"fixture",
            StorageDirectory=Path.Combine(root,"backup"),AllowDevelopmentHttp=true};
        static async UniTask<Exception> Observe(UniTask operation){try{await operation;return null;}catch(Exception error){return error;}}
        sealed class Server : HttpMessageHandler
        {
            internal bool HoldCapabilities,HoldUploads,DownloadEntered,CapabilitiesEntered,UploadEntered;
            internal readonly System.Threading.Tasks.TaskCompletionSource<HttpResponseMessage> Pending=new System.Threading.Tasks.TaskCompletionSource<HttpResponseMessage>(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
            internal static HttpResponseMessage Capabilities()=>new HttpResponseMessage {Content=new StringContent("{\"protocolVersion\":1,\"account\":\"audit\",\"chunkBytes\":1024,\"maxFileBytes\":1048576}")};
            protected override async System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            {
                if(request.RequestUri.AbsolutePath=="/v1/capabilities") {CapabilitiesEntered=true;return HoldCapabilities?await Pending.Task:Capabilities();}
                if(request.RequestUri.AbsolutePath.StartsWith("/v1/backups/",StringComparison.Ordinal)){DownloadEntered=true;return await Pending.Task;}
                UploadEntered=true;if(HoldUploads)return await Pending.Task;
                var upload=JsonUtility.FromJson<UploadRequest>(await request.Content.ReadAsStringAsync());
                return new HttpResponseMessage {Content=new StringContent(JsonUtility.ToJson(new UploadResponse {uploadId=upload.key,sha256=upload.sha256,size=upload.size,offset=upload.size,completed=true,backupId=upload.key,capabilities=new ServerCapabilities {protocolVersion=1,account="audit",chunkBytes=1024,maxFileBytes=1048576}}))};
            }
        }
        static async UniTask Confirm(ImageBackupService service,BackupTaskInfo task)
        {
            await service.Db(BackupRepository.Command.Claim,default,task.id,0,0,DateTime.UtcNow.Ticks,Guid.NewGuid().ToString("N"));
            await service.Db(BackupRepository.Command.Start,default,task.id,1L);
            await service.Db(BackupRepository.Command.Finish,default,task.id,1L,1,task.key,"",DateTime.UtcNow.Ticks,task.size,task.sha256);
            await service.Db(BackupRepository.Command.Release,default,task.id,1L,true,true);
        }
        [UnityTest] public IEnumerator DownloadAllowsResumeAndCapabilityRefresh()=>UniTask.ToCoroutine(async()=>
        {
            var server=new Server();var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);System.Threading.Tasks.Task download=null;
            try {
                await service.EnqueueAsync(new[]{ImageReference.FromFile(photo)});var task=(await service.QueryTasksAsync()).Items.Single();await Confirm(service,task);
                await service.PauseAsync();download=service.DownloadBackupAndVerifyAsync(task.key,Path.Combine(root,"restored.jpg")).AsTask();
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await UniTask.WaitUntil(()=>server.DownloadEntered,cancellationToken:timeout.Token);
                await service.ResumeAsync();await service.RefreshServerCapabilitiesAsync();Assert.IsFalse(service.IsPaused);
                Assert.AreEqual(1,(await new BackupMaintenance(service).RunAsync(new BackupRetentionPolicy {TimeSlice=TimeSpan.FromSeconds(10)})).FilesDeleted);
                server.Pending.TrySetResult(new HttpResponseMessage {Content=new ByteArrayContent(File.ReadAllBytes(photo))});await download;
            } finally {
                server.Pending.TrySetResult(new HttpResponseMessage {Content=new ByteArrayContent(File.ReadAllBytes(photo))});
                if(download!=null)await download;await service.ShutdownAsync();
            }
        });
        [UnityTest] public IEnumerator CapabilityAndUploadAdmissionIsMutuallyExclusive()=>UniTask.ToCoroutine(async()=>
        {
            var server=new Server {HoldCapabilities=true};var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            try {
                var refresh=service.RefreshServerCapabilitiesAsync();
                Assert.IsInstanceOf<InvalidOperationException>(await Observe(service.ProcessAsync()));
                server.Pending.TrySetResult(Server.Capabilities());await refresh;
            } finally {server.Pending.TrySetResult(Server.Capabilities());await service.ShutdownAsync();}
            server=new Server {HoldUploads=true};service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);System.Threading.Tasks.Task processing=null;
            try {
                await service.EnqueueAsync(new[]{ImageReference.FromFile(photo)});processing=service.ProcessAsync().AsTask();
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await UniTask.WaitUntil(()=>server.UploadEntered,cancellationToken:timeout.Token);
                Assert.IsInstanceOf<InvalidOperationException>(await Observe(service.RefreshServerCapabilitiesAsync()));
                server.Pending.TrySetException(new IOException("controlled transport failure"));await processing;
            } finally {
                server.Pending.TrySetException(new IOException("fixture cleanup"));if(processing!=null)await processing;await service.ShutdownAsync();
            }
        });
        [UnityTest] public IEnumerator ShutdownDrainsAcceptedQueryAndRejectsNewOperations()=>UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config());
            var query=service.QueryTasksAsync();var closing=service.ShutdownAsync();
            Assert.IsInstanceOf<ObjectDisposedException>(await Observe(service.ResumeAsync()));
            Assert.IsEmpty((await query).Items);await closing;
        });
        [UnityTest] public IEnumerator QueuePauseDoesNotRejectAdmittedPreparation()=>UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config());
            try {
                var preparation=service.EnqueueAsync(new[]{ImageReference.FromFile(photo)}).AsTask();
                await service.PauseAsync();await preparation;
                Assert.IsTrue((await service.GetSummaryAsync()).QueuePaused);
                Assert.AreEqual(1,(await service.QueryTasksAsync()).Items.Count);await service.ResumeAsync();
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator CleanupFailureIsPerFileAndRetryDoesNotCleanOtherFiles()=>UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,new Server());
            try {
                string second=Path.Combine(root,"second.jpg");File.WriteAllText(second,"second");
                await service.EnqueueAsync(new[]{ImageReference.FromFile(photo),ImageReference.FromFile(second)});
                var tasks=(await service.QueryTasksAsync()).Items;foreach(var task in tasks)await Confirm(service,task);
                var bad=tasks[0];string broken=Path.Combine(service.RepositoryRoot,service.StoreId,bad.relativePath);
                File.Delete(broken);Directory.CreateDirectory(broken);File.WriteAllText(Path.Combine(broken,"occupied"),"fixture");
                BackupCleanupException failure=null;
                try{await new BackupMaintenance(service).RunAsync(new BackupRetentionPolicy {TimeSlice=TimeSpan.FromSeconds(10)});}
                catch(BackupCleanupException error){failure=error;}
                Assert.IsNotNull(failure);Assert.AreEqual(1,failure.Result.FilesDeleted);Assert.AreEqual(1,failure.Result.Failures.Count);
                Assert.AreEqual(bad.id,failure.Result.Failures[0].FileId);Assert.IsTrue(failure.Result.Failures[0].Error.IsIsolatedCleanupFailure);
                Assert.AreEqual(BackupState.Completed,(await service.GetTaskAsync(bad.id)).state);Assert.AreEqual(2,(await service.QueryBackupsAsync()).Items.Count);
                string third=Path.Combine(root,"third.jpg");File.WriteAllText(third,"third");await service.EnqueueAsync(new[]{ImageReference.FromFile(third)});
                await service.ProcessAsync();Assert.AreEqual(3,(await service.QueryBackupsAsync()).Items.Count);
                Assert.IsNotEmpty((await service.GetTaskAsync(bad.id)).cleanupError);
                string fourth=Path.Combine(root,"fourth.jpg");File.WriteAllText(fourth,"fourth");await service.EnqueueAsync(new[]{ImageReference.FromFile(fourth)});
                var last=(await service.QueryTasksAsync()).Items.Last();await Confirm(service,last);
                Directory.Delete(broken,true);await service.RetryCleanupAsync(bad.id);
                Assert.IsFalse((await service.GetTaskAsync(bad.id)).cleanupPending);
                Assert.IsTrue(File.Exists(Path.Combine(service.RepositoryRoot,service.StoreId,last.relativePath)),"A targeted retry cleaned an unrelated payload.");
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator NotificationFailureAndCanceledWaiterDoNotInvalidateIndex()=>UniTask.ToCoroutine(async()=>
        {
            var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"index.sqlite"));var scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,root);
            bool fail=false;var original=new Exception("controlled subscriber failure");
            var watch=library.Watch(scope,_=>{if(fail)throw original;});
            var gate=(SemaphoreSlim)typeof(ImageLibraryIndex).GetField("refreshGate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(library);bool held=false;
            try {
                await library.RefreshAsync(scope);fail=true;
                Exception observed=null;try{await library.RefreshAsync(scope);}catch(Exception error){observed=error;}
                Assert.AreSame(original,observed);fail=false;watch.Resume();
                Assert.IsFalse((await library.GetPositionAsync(scope)).RequiresRefresh);
                Assert.AreEqual(ImageLibraryRefreshKind.Current,(await library.RefreshAsync(scope)).Kind);
                await gate.WaitAsync();held=true;using var cancel=new CancellationTokenSource();
                var waiting=library.RefreshAsync(scope,cancel.Token);cancel.Cancel();
                try{await waiting;Assert.Fail("Expected cancellation");}catch(OperationCanceledException){}
                gate.Release();held=false;
                Assert.AreEqual(ImageLibraryRefreshKind.Current,(await library.RefreshAsync(scope)).Kind);
                fail=true;LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("controlled subscriber failure"));watch.RequestRefresh();
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await UniTask.WaitUntil(()=>watch.Failure!=null,cancellationToken:timeout.Token);
                Assert.AreSame(original,watch.Failure);fail=false;watch.Resume();
                Assert.IsFalse((await library.GetPositionAsync(scope)).RequiresRefresh);
                Assert.AreEqual(ImageLibraryRefreshKind.Current,(await library.RefreshAsync(scope)).Kind);
            } finally {if(held)gate.Release();await watch.CloseAsync();await library.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator LastWatchCloseWaitsForAdmittedRefresh()=>UniTask.ToCoroutine(async()=>
        {
            var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"index.sqlite"));var scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,root);
            var watch=library.Watch(scope,_=>{});
            var gate=(SemaphoreSlim)typeof(ImageLibraryIndex).GetField("refreshGate",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(library);bool held=false;
            try {
                await gate.WaitAsync();held=true;var refresh=library.RefreshAsync(scope);
                var close=watch.CloseAsync();Assert.AreEqual(UniTaskStatus.Pending,close.Status);
                gate.Release();held=false;await refresh;await close;await watch.CloseAsync();
                Assert.IsTrue((await library.GetPositionAsync(scope)).RequiresRefresh);
            } finally {if(held)gate.Release();await watch.CloseAsync();await library.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator ScopeCapacityTracksLiveWatchesAndSharedOwnership()=>UniTask.ToCoroutine(async()=>
        {
            var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"index.sqlite"));
            try {
                for(int i=0;i<24;i++) {
                    string folder=Path.Combine(root,"album"+i);Directory.CreateDirectory(folder);var scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,folder);
                    await library.QueryAsync(scope);
                    var first=library.Watch(scope,_=>{});var second=library.Watch(scope,_=>{});
                    await library.RefreshAsync(scope);await first.CloseAsync();
                    Assert.AreEqual(ImageLibraryRefreshKind.Current,(await library.RefreshAsync(scope)).Kind);
                    await second.CloseAsync();Assert.IsTrue((await library.GetPositionAsync(scope)).RequiresRefresh);
                    Assert.AreEqual(ImageLibraryRefreshKind.CompleteReconciliation,(await library.RefreshAsync(scope)).Kind);
                }
            } finally {await library.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator IndexedCopyRejectsChangedBytesWithIdenticalMetadata()=>UniTask.ToCoroutine(async()=>
        {
            var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"index.sqlite"));var service=await ImageBackupService.CreateAsync(Config());
            try {
                var scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,root);await library.RefreshAsync(scope);
                var image=(await library.QueryAsync(scope)).Items.Single();var stamp=File.GetLastWriteTimeUtc(photo);
                await service.EnqueueAsync(new[]{image});var accepted=(await service.QueryTasksAsync()).Items.Single();StringAssert.EndsWith(accepted.sha256,image.Version);
                File.WriteAllBytes(photo,new byte[]{4,3,2,1});File.SetLastWriteTimeUtc(photo,stamp);
                GalleryException failure=null;try{await service.EnqueueAsync(new[]{image});}catch(GalleryException error){failure=error;}
                Assert.IsNotNull(failure);Assert.AreEqual("SourceChanged",failure.Code);Assert.AreEqual(1,(await service.QueryTasksAsync()).Items.Count);
            } finally {await library.ShutdownAsync();await service.ShutdownAsync();}
        });
    }
}
