using System;
using System.Collections;
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

namespace UIFrame.Regression
{
    public sealed class BackupProtocolTests
    {
        string root;
        [SetUp] public void Setup(){root=Path.Combine(Path.GetTempPath(),"uiframe-v2-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);}
        [TearDown] public void Cleanup(){if(Directory.Exists(root))Directory.Delete(root,true);}
        BackupConfiguration Config()=>new BackupConfiguration {ServerUrl="https://api.invalid",Account="integration-user",AccessToken=()=>"business-secret",StorageDirectory=Path.Combine(root,"backup")};
        ImageReference Photo(string name){string path=Path.Combine(root,name+".jpg");File.WriteAllBytes(path,new byte[]{1,2,3,4});return ImageReference.FromFile(path);}
        static TaskCompletionSource<bool> Gate()=>new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        static async Task AwaitGate(TaskCompletionSource<bool> gate,CancellationToken token) {
            var stopped=Gate();using(token.Register(()=>stopped.TrySetCanceled(token)))await await Task.WhenAny(gate.Task,stopped.Task);
        }
        static async UniTask<Exception> Observe(UniTask work){try{await work;return null;}catch(Exception error){return error;}}
        static async UniTask<Exception> Observe<T>(UniTask<T> work){try{await work;return null;}catch(Exception error){return error;}}
        static async UniTask Wait(Func<bool> condition){using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));await UniTask.WaitUntil(condition,cancellationToken:deadline.Token);}
        sealed class DisposeFailureFixture : BackupProtocolFixture
        {
            internal readonly Exception Error=new IOException("fixture transport disposal failed");
            protected override void Dispose(bool disposing){base.Dispose(disposing);if(disposing)throw Error;}
        }
        [UnityTest] public IEnumerator ShutdownPreservesExecutorFailureAndStillReleasesRepository()=>UniTask.ToCoroutine(async()=>{
            var fixture=new DisposeFailureFixture();var config=Config();
            var service=await ImageBackupService.CreateAsync(config,NativeBackup.Call,false,fixture);
            var primary=new InvalidOperationException("fixture network policy failed");
            service.SetDesktopNetworkPolicy(()=>throw primary);
            LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("InvalidOperationException: fixture network policy failed"));
            await Observe(service.SubmitAsync(Guid.NewGuid().ToString("N"),new[]{Photo("one")}));
            Assert.AreSame(primary,await Observe(service.WaitForIdleAsync()));
            LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("IOException: fixture transport disposal failed"));
            Assert.AreSame(primary,await Observe(service.ShutdownAsync()));
            service=await ImageBackupService.CreateAsync(config,NativeBackup.Call,false,new BackupProtocolFixture());
            try {Assert.AreEqual(BackupState.NeedsAttention,(await service.QueryTasksAsync()).Items.Single().state);}
            finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator ControlCleanupFailureCanBeQueriedAndRetriedWithoutChangingReceipt()=>UniTask.ToCoroutine(async()=>{
            if(Application.platform==RuntimePlatform.WindowsEditor)
                Assert.Ignore("This fault fixture replaces an open request file using POSIX unlink semantics.");
            var fixture=new BackupProtocolFixture {ConfirmOnPlan=true};
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,fixture);string broken=null;
            fixture.BeforeConfirm=item=>{
                if(broken!=null)return;
                broken=Directory.GetFiles(Path.Combine(service.RepositoryRoot,service.StoreId,"controls"),"*.json").Single();
                File.Delete(broken);Directory.CreateDirectory(broken);File.WriteAllText(Path.Combine(broken,"blocker"),"x");
            };
            try {
                LogAssert.Expect(LogType.Error,"Backup control file cleanup failed; its recorded item requires explicit cleanup retry.");
                await service.SubmitPhotosAsync(new[]{Photo("one"),Photo("two")});await service.WaitForIdleAsync();
                var failure=(await service.GetControlCleanupFailuresAsync()).Single();
                Assert.IsNotEmpty(failure.Error);Assert.AreEqual(2,(await service.QueryBackupsAsync()).Items.Count);
                fixture.BeforeConfirm=null;
                string independent=(await service.SubmitPhotosAsync(new[]{Photo("independent")}))[0];await service.WaitForIdleAsync();
                var retention=new BackupRetentionPolicy {HistoryAge=TimeSpan.Zero,KeepHistoryCount=0,MaximumItems=1,TimeSlice=TimeSpan.FromSeconds(5)};
                Assert.AreEqual(1,(await new BackupMaintenance(service).RunAsync(retention)).HistoryRemoved);
                Assert.IsNull(await service.GetTaskAsync(independent));
                Assert.AreEqual(2,(await service.QueryTasksAsync()).Items.Count);
                Assert.IsInstanceOf<BackupRepositoryException>(await Observe(service.RetryControlCleanupAsync(failure.RequestId)));
                Directory.Delete(broken,true);await service.RetryControlCleanupAsync(failure.RequestId);
                Assert.IsEmpty(await service.GetControlCleanupFailuresAsync());
                Assert.AreEqual(3,(await service.QueryBackupsAsync()).Items.Count);
            } finally {if(broken!=null && Directory.Exists(broken))Directory.Delete(broken,true);await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator ConfirmationDeadlineCancelsRequestButWaitsForActualRelease()=>UniTask.ToCoroutine(async()=>{
            var canceled=Gate();var released=Gate();
            var fixture=new BackupProtocolFixture {BeforeQuery=async token=>{
                using(token.Register(()=>canceled.TrySetResult(true)))await released.Task;
                token.ThrowIfCancellationRequested();
            }};
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,fixture);
            try {
                // Seed a nearly expired confirmation window through repository
                // commands, without a 24-hour wait or direct database mutation.
                const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                string owner=(string)typeof(ImageBackupService).GetField("owner",flags).GetValue(service);
                string credential=(string)typeof(ImageBackupService).GetMethod("StoreDesktopCredential",flags).Invoke(service,new object[]{"secret"});
                long past=DateTime.UtcNow.AddDays(-1).AddSeconds(4).Ticks;
                await service.Db(BackupRepository.Command.Prepare,default,"aa",owner,past,1,"bb","file:fixture","v1","fixture.jpg","image/jpeg",4L);
                File.WriteAllBytes(Path.Combine(service.RepositoryRoot,service.StoreId,"payloads/bb.payload"),new byte[]{1,2,3,4});
                await service.Db(BackupRepository.Command.Seal,default,"bb",owner,4L,new string('a',64),"image/jpeg",1024L,past);
                await service.Db(BackupRepository.Command.AcceptItem,default,"bb",owner,past);
                await service.Db(BackupRepository.Command.Claim,default,"bb",0,false,past);
                await service.Db(BackupRepository.Command.Handoff,default,"bb",1,credential);
                await service.Db(BackupRepository.Command.ControlCreate,default,"cc",0,past,false,true);
                await service.Db(BackupRepository.Command.ControlSeal,default,"cc");
                await service.Db(BackupRepository.Command.ControlSubmitted,default,"cc","prior-owner");
                await service.Db(BackupRepository.Command.ControlFail,default,"cc","paused after submission",past,true);
                await service.Db(BackupRepository.Command.ControlRelease,default,"cc");
                await service.Db(BackupRepository.Command.ProtocolRelease,default,"bb",1);
                await service.Db(BackupRepository.Command.ProtocolResume,default,"bb",credential,DateTime.UtcNow.Ticks);
                // Public resume drives existing eligible work without resetting
                // its already persisted confirmation deadline.
                await service.ResumeAsync();await Wait(()=>fixture.Queries==1);
                await Wait(()=>canceled.Task.IsCompleted);
                var active=(await service.Db(BackupRepository.Command.Controls,default,0,"")).Single;
                Assert.IsFalse(active.Flag("released"));
                Assert.IsInstanceOf<BackupRepositoryException>(await Observe(service.Db(BackupRepository.Command.ProtocolRelease,default,"bb",1)));
                Assert.IsTrue(File.Exists(Path.Combine(service.RepositoryRoot,service.StoreId,active.Text("relative_path"))));
                released.TrySetResult(true);await service.WaitForIdleAsync();
                var task=await service.GetTaskAsync("bb");Assert.AreEqual(BackupState.NeedsAttention,task.state);
                StringAssert.Contains("Confirmation deadline reached",task.error);
                Assert.IsEmpty((await service.Db(BackupRepository.Command.Controls,default,0,"")).Rows);
                Assert.AreEqual(1,fixture.Queries);
                fixture.BeforeQuery=null;fixture.ConfirmOnPlan=true;
                await service.SubmitPhotosAsync(new[]{Photo("later")});await service.WaitForIdleAsync();
                Assert.AreEqual(1,(await service.QueryBackupsAsync()).Items.Count);
            } finally {released.TrySetResult(true);await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator SubmissionIsOfflineDurableAndQueriesEveryAcceptedMember()=>UniTask.ToCoroutine(async()=>{
            var server=new BackupProtocolFixture();var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            string operation=Guid.NewGuid().ToString("N"),id;
            try {
                service.SetDesktopNetworkPolicy(()=>false);
                var submission=await service.SubmitAsync(operation,new[]{Photo("one"),Photo("two")});
                Assert.AreEqual(2,submission.AcceptedTaskIds.Count);Assert.AreEqual(0,server.Plans);
                Assert.IsTrue(submission.Items.All(x=>x.Acceptance==BackupAcceptanceStage.SystemScheduled));id=submission.Items[0].TaskId;
                await service.PauseAsync();Assert.IsTrue(service.IsPaused);
                Assert.IsTrue((await service.QueryTasksAsync()).Items.All(x=>x.state==BackupState.Paused));
            } finally {await service.ShutdownAsync();}
            service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,new BackupProtocolFixture());
            try {
                Assert.AreEqual(id,(await service.QuerySubmissionAsync(operation)).Items[0].TaskId);
                Assert.IsTrue(service.IsPaused);await service.ResumeAsync();await service.WaitForIdleAsync();
                Assert.AreEqual(2,(await service.GetSummaryAsync())[BackupState.Completed]);
                var other=Config();other.Account="another";other.StorageDirectory=Path.Combine(root,"backup");
                using var isolated=await ImageBackupService.CreateAsync(other);Assert.IsEmpty((await isolated.QueryTasksAsync()).Items);
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator EmptyPutResponsesWaitForReceiptsAndReleaseUploadSlots()=>UniTask.ToCoroutine(async()=>{
            var server=new BackupProtocolFixture {ReverseResults=true};var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            try {
                await service.SubmitPhotosAsync(Enumerable.Range(0,8).Select(i=>Photo("photo"+i)).ToArray());
                await service.WaitForIdleAsync();
                Assert.AreEqual(8,server.Uploads);Assert.AreEqual(8,(await service.QueryBackupsAsync()).Items.Count);
                Assert.LessOrEqual(server.Plans,4);Assert.Less(server.Queries,8);
                Assert.IsTrue((await service.QueryTasksAsync()).Items.All(x=>x.state==BackupState.Completed && !x.cleanupPending));
                Assert.IsEmpty(Directory.GetFiles(Path.Combine(service.RepositoryRoot,service.StoreId,"controls")));
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator SourceFailureIsIsolatedAndPartialResultSurvivesQuery()=>UniTask.ToCoroutine(async()=>{
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,new BackupProtocolFixture());
            try {
                var missing=Photo("missing");File.Delete(missing.Id);string operation=Guid.NewGuid().ToString("N");
                BackupSubmissionException failure=null;
                try{await service.SubmitAsync(operation,new[]{missing,Photo("valid")});}catch(BackupSubmissionException error){failure=error;}
                Assert.IsNotNull(failure);Assert.IsFalse(failure.HasSharedFailure);Assert.AreEqual(1,failure.InnerExceptions.Count);
                Assert.AreEqual(BackupState.Failed,failure.Result.Items[0].State);Assert.AreEqual(1,failure.Result.AcceptedTaskIds.Count);
                await service.WaitForIdleAsync();var query=await service.QuerySubmissionAsync(operation);
                Assert.AreEqual(BackupState.Completed,query.Items[1].State);Assert.IsNotEmpty(query.Items[0].Error);
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator PreparationWindowWaitsForCapacityWithoutOverReservation()=>UniTask.ToCoroutine(async()=>{
            var gate=Gate();var server=new BackupProtocolFixture {BeforeUpload=(item,token)=>AwaitGate(gate,token)};
            var config=Config();config.DiskBudgetBytes=4;var service=await ImageBackupService.CreateAsync(config,NativeBackup.Call,false,server);
            Task<System.Collections.Generic.IReadOnlyList<string>> submission=null;
            try {
                submission=service.SubmitPhotosAsync(new[]{Photo("first"),Photo("second")}).AsTask();await Wait(()=>service.IsWaitingForCapacity);
                var storage=await new BackupMaintenance(service).PreviewAsync();Assert.LessOrEqual(storage.PreparationBytes+storage.UploadBytes+storage.ReclaimableBytes,4);
                gate.TrySetResult(true);Assert.AreEqual(2,(await submission).Count);await service.WaitForIdleAsync();
                Assert.AreEqual(2,(await service.GetSummaryAsync())[BackupState.Completed]);Assert.IsFalse(service.IsWaitingForCapacity);
            } finally {gate.TrySetResult(true);if(submission!=null)try{await submission;}catch(Exception){}await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator ConcurrentSubmissionFailsBeforeCallingCredentialsAgain()=>UniTask.ToCoroutine(async()=>{
            int credentials=0;var config=Config();config.AccessToken=()=>{credentials++;return "secret";};config.DiskBudgetBytes=4;
            var gate=Gate();var server=new BackupProtocolFixture {BeforeUpload=(item,token)=>AwaitGate(gate,token)};
            var service=await ImageBackupService.CreateAsync(config,NativeBackup.Call,false,server);
            Task<BackupSubmissionResult> first=null;
            try {
                first=service.SubmitAsync(Guid.NewGuid().ToString("N"),new[]{Photo("first"),Photo("second")}).AsTask();
                await Wait(()=>service.IsWaitingForCapacity);
                var failure=await Observe(service.SubmitAsync(Guid.NewGuid().ToString("N"),new[]{Photo("third")}));
                Assert.IsInstanceOf<InvalidOperationException>(failure);Assert.AreEqual(1,credentials);
                gate.TrySetResult(true);await first;await service.WaitForIdleAsync();
                Assert.AreEqual(2,(await service.QueryTasksAsync()).Items.Count);
            } finally {gate.TrySetResult(true);if(first!=null)try{await first;}catch(Exception){}await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator PauseEndsPreparationCapacityWaitAndRetainsAcceptedPhoto()=>UniTask.ToCoroutine(async()=>{
            var config=Config();config.DiskBudgetBytes=4;
            var gate=Gate();var server=new BackupProtocolFixture {BeforeUpload=(item,token)=>AwaitGate(gate,token)};
            var service=await ImageBackupService.CreateAsync(config,NativeBackup.Call,false,server);
            Task<BackupSubmissionResult> submission=null;
            try {
                submission=service.SubmitAsync(Guid.NewGuid().ToString("N"),new[]{Photo("first"),Photo("second")}).AsTask();
                await Wait(()=>service.IsWaitingForCapacity);await service.PauseAsync();
                BackupSubmissionException failure=null;try{await submission;}catch(BackupSubmissionException error){failure=error;}
                Assert.IsNotNull(failure);Assert.IsFalse(service.IsWaitingForCapacity);
                Assert.AreEqual(BackupState.Paused,failure.Result.Items[0].State);
                Assert.AreEqual(BackupState.Failed,failure.Result.Items[1].State);
                Assert.IsInstanceOf<InvalidOperationException>(await Observe(service.RetryAsync(failure.Result.Items[1].TaskId)));
                Assert.IsEmpty((await service.QueryBackupsAsync()).Items);
            } finally {gate.TrySetResult(true);if(submission!=null)try{await submission;}catch(Exception){}await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator ThirtyTwoPhotosKeepProgressWithOneSlowAndOneFailedUpload()=>UniTask.ToCoroutine(async()=>{
            var gate=Gate();string held=null,failed=null;var server=new BackupProtocolFixture();
            server.BeforeUpload=(item,token)=>{
                if(item.name=="slow.jpg"){held=item.clientTaskId;return AwaitGate(gate,token);}
                if(item.name=="failed.jpg"){failed=item.clientTaskId;throw new System.Net.Http.HttpRequestException("Controlled single-photo failure");}
                return Task.CompletedTask;
            };
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            try {
                var photos=new[]{Photo("slow"),Photo("failed")}.Concat(Enumerable.Range(0,30).Select(i=>Photo("normal"+i))).ToArray();
                var ids=await service.SubmitPhotosAsync(photos);await Wait(()=>held!=null && server.Queries>0);
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while((await service.GetSummaryAsync())[BackupState.Completed]<30)await UniTask.Delay(30,cancellationToken:deadline.Token);
                Assert.AreEqual(BackupState.Uploading,(await service.GetTaskAsync(held)).state);
                Assert.AreEqual(BackupState.NeedsAttention,(await service.GetTaskAsync(failed)).state);
                await service.CancelAsync(held,deadline.Token);await service.WaitForIdleAsync(deadline.Token);
                Assert.AreEqual(BackupState.Canceled,(await service.GetTaskAsync(held)).state);
                Assert.AreEqual(30,(await service.QueryBackupsAsync()).Items.Count);Assert.AreEqual(32,server.Uploads);
            } finally {gate.TrySetResult(true);await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator UnknownUploadRequiresExplicitReconcileAndNeverAutomaticRetry()=>UniTask.ToCoroutine(async()=>{
            var server=new BackupProtocolFixture {FailUploads=true};var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            try {
                var ids=await service.SubmitPhotosAsync(new[]{Photo("one")});await service.WaitForIdleAsync();
                Assert.AreEqual(BackupState.NeedsAttention,(await service.GetTaskAsync(ids[0])).state);
                Assert.IsInstanceOf<InvalidOperationException>(await Observe(service.RetryAsync(ids[0])));
                await service.WaitForIdleAsync();Assert.AreEqual(1,server.Uploads);
                server.FailUploads=false;await service.ReconcileTaskAsync(ids[0]);await service.WaitForIdleAsync();
                Assert.AreEqual(BackupState.Completed,(await service.GetTaskAsync(ids[0])).state);
                Assert.AreEqual(1,(await service.GetTaskAsync(ids[0])).generation);Assert.AreEqual(2,server.Uploads);
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator ReconcileRetainsFailedCancellationAndDoesNotResumeUpload()=>UniTask.ToCoroutine(async()=>{
            var server=new BackupProtocolFixture {FailUploads=true,FailFirstCancel=true};
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            try {
                string id=(await service.SubmitPhotosAsync(new[]{Photo("cancel")}))[0];await service.WaitForIdleAsync();
                Assert.IsNotNull(await Observe(service.CancelAsync(id)));await service.WaitForIdleAsync();
                Assert.AreEqual(BackupState.NeedsAttention,(await service.GetTaskAsync(id)).state);
                Assert.AreEqual(1,server.Cancels);server.FailUploads=false;
                await service.ReconcileTaskAsync(id);await service.WaitForIdleAsync();
                Assert.AreEqual(BackupState.Canceled,(await service.GetTaskAsync(id)).state);
                Assert.AreEqual(2,server.Cancels);Assert.AreEqual(1,server.Uploads);Assert.AreEqual(0,server.Queries);
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator LostPlanResponseReconcilesWithoutNewAttemptOrSecondPlan()=>UniTask.ToCoroutine(async()=>{
            var server=new BackupProtocolFixture {FailFirstPlan=true};var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            try {
                string id=(await service.SubmitPhotosAsync(new[]{Photo("one")}))[0];await service.WaitForIdleAsync();
                Assert.AreEqual(BackupState.NeedsAttention,(await service.GetTaskAsync(id)).state);
                await service.ReconcileTaskAsync(id);Assert.IsNotNull(await Observe(service.ReconcileTaskAsync(id)));
                await service.WaitForIdleAsync();Assert.AreEqual(1,server.Plans);Assert.AreEqual(1,server.Uploads);
                Assert.AreEqual(BackupState.Completed,(await service.GetTaskAsync(id)).state);
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator WrongAccountResponseFailsOnlyItsBatchAndNoPhotoIsUploaded()=>UniTask.ToCoroutine(async()=>{
            var server=new BackupProtocolFixture {ResponseAccount="another"};var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            try {
                await service.SubmitPhotosAsync(new[]{Photo("one"),Photo("two")});await service.WaitForIdleAsync();
                Assert.AreEqual(0,server.Uploads);Assert.AreEqual(2,(await service.GetSummaryAsync())[BackupState.NeedsAttention]);
                Assert.IsEmpty((await service.QueryBackupsAsync()).Items);
                server.ResponseAccount=null;await service.SubmitPhotosAsync(new[]{Photo("other")});await service.WaitForIdleAsync();
                Assert.AreEqual(1,(await service.GetSummaryAsync())[BackupState.Completed]);
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator CredentialsAreReadOncePerSubmissionAndOriginalFailureIsPreserved()=>UniTask.ToCoroutine(async()=>{
            var original=new InvalidOperationException("credential fixture");var config=Config();int reads=0;bool fail=true;
            config.AccessToken=()=>{reads++;if(fail)throw original;return "secret";};
            var server=new BackupProtocolFixture();var service=await ImageBackupService.CreateAsync(config,NativeBackup.Call,false,server);
            try {
                Exception observed=null;try{await service.SubmitPhotosAsync(new[]{Photo("first")});}catch(Exception error){observed=error;}
                Assert.AreSame(original,observed);Assert.IsEmpty((await service.QueryTasksAsync()).Items);Assert.AreEqual(0,server.Plans);
                fail=false;await service.SubmitPhotosAsync(new[]{Photo("second"),Photo("third")});await service.WaitForIdleAsync();
                Assert.AreEqual(2,reads);Assert.AreEqual(2,(await service.QueryBackupsAsync()).Items.Count);
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator ConcurrentWaitersObserveOneDriverAndQueuePauseReleasesOwners()=>UniTask.ToCoroutine(async()=>{
            var gate=Gate();var server=new BackupProtocolFixture {BeforeUpload=(item,token)=>AwaitGate(gate,token)};
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            try {
                var ids=await service.SubmitPhotosAsync(new[]{Photo("one")});await Wait(()=>server.Uploads==1);
                var first=service.WaitForIdleAsync().AsTask();var second=service.WaitForIdleAsync().AsTask();
                Assert.IsTrue(service.IsRunning);await service.PauseAsync();await Task.WhenAll(first,second);
                var task=await service.GetTaskAsync(ids[0]);Assert.AreEqual(BackupState.Paused,task.state);
                var attempt=(await service.Db(BackupRepository.Command.Attempt,default,task.id,task.generation)).Single;
                Assert.IsTrue(attempt.Flag("payload_released"));Assert.IsTrue(attempt.Flag("credential_released"));
                gate.TrySetResult(true);await service.ResumeAsync();await service.WaitForIdleAsync();
                Assert.AreEqual(BackupState.Completed,(await service.GetTaskAsync(ids[0])).state);
            } finally {gate.TrySetResult(true);await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator CleanupFailureDoesNotStopOtherUploadsAndRetryTargetsOneFile()=>UniTask.ToCoroutine(async()=>{
            var server=new BackupProtocolFixture {ConfirmOnPlan=true};var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,server);
            string broken=null,bad=null;
            server.BeforeConfirm=item=>{
                if(item.name!="broken.jpg" || broken!=null)return;
                bad=item.clientTaskId;broken=Path.Combine(service.RepositoryRoot,service.StoreId,"payloads",bad+".payload");
                File.Delete(broken);Directory.CreateDirectory(broken);File.WriteAllText(Path.Combine(broken,"occupied"),"test");
            };
            try {
                LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("BackupRepositoryException"));
                await service.SubmitPhotosAsync(new[]{Photo("broken"),Photo("normal")});await service.WaitForIdleAsync();
                Assert.AreEqual(2,(await service.QueryBackupsAsync()).Items.Count);Assert.IsNotEmpty((await service.GetTaskAsync(bad)).cleanupError);
                await service.SubmitPhotosAsync(new[]{Photo("independent")});await service.WaitForIdleAsync();
                Assert.AreEqual(3,(await service.QueryBackupsAsync()).Items.Count);
                Directory.Delete(broken,true);await service.RetryCleanupAsync(bad);
                Assert.IsFalse((await service.GetTaskAsync(bad)).cleanupPending);
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator SubmissionDetailsExpireHonestlyAfterHistoryCleanup()=>UniTask.ToCoroutine(async()=>{
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,new BackupProtocolFixture {ConfirmOnPlan=true});
            try {
                string id=Guid.NewGuid().ToString("N");await service.SubmitAsync(id,new[]{Photo("one")});await service.WaitForIdleAsync();
                await new BackupMaintenance(service).RunAsync(new BackupRetentionPolicy {HistoryAge=TimeSpan.Zero,KeepHistoryCount=0,TimeSlice=TimeSpan.FromSeconds(5)});
                Assert.IsEmpty((await service.QueryTasksAsync()).Items);
                var item=(await service.QuerySubmissionAsync(id)).Items.Single();Assert.IsTrue(item.DetailsExpired);Assert.IsNull(item.State);Assert.IsNull(item.Acceptance);
                Assert.AreEqual(1,(await service.QueryBackupsAsync()).Items.Count);
            } finally {await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator DurableOperationIdentityAndGlobalPauseStayIndependent()=>UniTask.ToCoroutine(async()=>{
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,new BackupProtocolFixture());
            try {
                service.SetDesktopNetworkPolicy(()=>false);string id=(await service.SubmitPhotosAsync(new[]{Photo("one")}))[0];
                var command=new BackupOperationCommand {OperationId=Guid.NewGuid().ToString("N"),Action=BackupAction.Pause,TaskIds=new[]{id}};
                await service.SubmitOperationAsync(command);await service.WaitOperationAsync(command.OperationId);
                Assert.AreEqual(BackupState.Paused,(await service.GetTaskAsync(id)).state);
                await service.SubmitOperationAsync(command);Assert.AreEqual(1,(await service.QueryOperationAsync(command.OperationId)).SelectedCount);
                command.Action=BackupAction.Cancel;Exception conflict=null;try{await service.SubmitOperationAsync(command);}catch(Exception error){conflict=error;}Assert.IsNotNull(conflict);
                await service.PauseAsync();
                var resume=new BackupOperationCommand {OperationId=Guid.NewGuid().ToString("N"),Action=BackupAction.Resume,TaskIds=new[]{id}};
                await service.SubmitOperationAsync(resume);await service.WaitOperationAsync(resume.OperationId);Assert.IsTrue(service.IsPaused);
            } finally {await service.ShutdownAsync();}
        });
    }
}
