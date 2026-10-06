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
    public sealed class MediaBackupCapacityTests
    {
        string root;
        [SetUp] public void Setup() { root = Path.Combine(Path.GetTempPath(), "uiframe-capacity-regression-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
        [TearDown] public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        BackupConfiguration Config(long bytes) => new BackupConfiguration {
            ServerUrl = "https://api.invalid", Account = "integration-user", AccessToken = () => "fixture-token",
            StorageDirectory = Path.Combine(root, "backup"), DiskBudgetBytes = bytes
        };
        ImageReference Photo(string path, int bytes) { File.WriteAllBytes(path, new byte[bytes]); return ImageReference.FromFile(path); }

        [UnityTest] public IEnumerator UnknownLengthWaitsForReclaimableCapacity() => UniTask.ToCoroutine(async () => {
            var service = await ImageBackupService.CreateAsync(Config(10), NativeBackup.Call, false, new BackupProtocolFixture());
            Task<BackupSubmissionResult> pending = null;
            try {
                service.SetDesktopNetworkPolicy(() => false);
                await service.SubmitAndWaitAsync(Guid.NewGuid().ToString("N"), new[] { Photo(Path.Combine(root, "first.jpg"), 6) });
                var original = Photo(Path.Combine(root, "second.jpg"), 6);
                var unknown = new ImageReference("file", original.Id, original.FileName, original.MimeType, version: original.Version);
                pending = service.SubmitAndWaitAsync(Guid.NewGuid().ToString("N"), new[] { unknown }).AsTask();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await UniTask.WaitUntil(() => pending.IsCompleted || service.IsWaitingForCapacity, cancellationToken: deadline.Token);
                Assert.IsTrue(service.IsWaitingForCapacity);
                service.SetDesktopNetworkPolicy(() => true);
                await pending;
                await service.WaitForIdleAsync();
                Assert.AreEqual(2,(await service.QueryBackupsAsync()).Items.Count);
            } finally {
                await service.ShutdownAsync();
                if (pending != null) try { await pending; } catch (Exception) { }
            }
        });

        [UnityTest] public IEnumerator AutomaticOversizeDoesNotRejectHealthyNeighbor() => UniTask.ToCoroutine(async () => {
            string photos = Path.Combine(root, "photos"); Directory.CreateDirectory(photos);
            Photo(Path.Combine(photos, "01-too-large.jpg"), 6);
            Photo(Path.Combine(photos, "02-healthy.jpg"), 1);
            var service = await ImageBackupService.CreateAsync(Config(5), NativeBackup.Call, false, new BackupProtocolFixture());
            var library = await ImageLibraryIndex.OpenAsync(Path.Combine(root, "library.sqlite"));
            try {
                var automatic = await AutomaticImageBackup.CreateAsync(service, library);
                await automatic.ConfigureAsync(new AutomaticBackupPolicy { enabled = true, sourceKind = BackupSourceKind.Directory,
                    source = photos, includeExisting = true, wifiOnly = false });
                Exception failure = null;
                try { await automatic.ScanOnceAsync(); } catch (Exception error) { failure = error; }
                await service.WaitForIdleAsync();
                var tasks = await service.QueryTasksAsync();
                Assert.IsNull(failure);
                Assert.IsTrue(tasks.Items.Any(t => t.name == "02-healthy.jpg" && t.state == BackupState.Completed),
                    "Oversize source must be isolated so the independent one-byte image can be backed up.");
            } finally { await service.ShutdownAsync(); await library.ShutdownAsync(); }
        });
        [UnityTest] public IEnumerator UnknownOversizeIsLocalAndReleasesCapacity() => UniTask.ToCoroutine(async () => {
            var service=await ImageBackupService.CreateAsync(Config(5),NativeBackup.Call,false,new BackupProtocolFixture());
            try {
                var sources=new[]{Photo(Path.Combine(root,"large.jpg"),6),Photo(Path.Combine(root,"small.jpg"),1)}
                    .Select(p=>new ImageReference("file",p.Id,p.FileName,p.MimeType,version:p.Version)).ToArray();
                BackupSubmissionException failure=null;
                try {await service.SubmitAndWaitAsync(Guid.NewGuid().ToString("N"),sources);}
                catch(BackupSubmissionException error){failure=error;}
                Assert.IsNotNull(failure);Assert.IsFalse(failure.HasSharedFailure);
                Assert.AreEqual(BackupState.Failed,failure.Result.Items[0].State);
                await service.WaitForIdleAsync();
                var receipts=await service.QueryBackupsAsync();Assert.AreEqual(1,receipts.Items.Count);
                Assert.AreEqual("small.jpg",receipts.Items[0].Name);
            } finally {await service.ShutdownAsync();}
        });

        [UnityTest] public IEnumerator CancelingWaitDoesNotCancelPreparationAndExplicitStopPreservesAcceptedItem() => UniTask.ToCoroutine(async () => {
            var service=await ImageBackupService.CreateAsync(Config(10),NativeBackup.Call,false,new BackupProtocolFixture());
            using var cancel=new CancellationTokenSource();
            try {
                service.SetDesktopNetworkPolicy(()=>false);
                await service.SubmitAndWaitAsync(Guid.NewGuid().ToString("N"),new[]{Photo(Path.Combine(root,"accepted.jpg"),6)});
                var source=Photo(Path.Combine(root,"waiting.jpg"),6);
                var submission=await service.SubmitAsync(Guid.NewGuid().ToString("N"),new[]{new ImageReference("file",source.Id,source.FileName,source.MimeType,version:source.Version)});
                var pending=submission.WaitAsync(cancel.Token).AsTask();
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await UniTask.WaitUntil(()=>pending.IsCompleted || service.IsWaitingForCapacity,cancellationToken:deadline.Token);
                Assert.IsTrue(service.IsWaitingForCapacity);cancel.Cancel();
                bool canceled=false;try{await pending;}catch(OperationCanceledException){canceled=true;}Assert.IsTrue(canceled);
                Assert.AreEqual(BackupState.Preparing,(await service.QueryTasksAsync()).Items.Single(t=>t.name=="waiting.jpg").state);
                await service.CancelPreparationAsync(submission.OperationId);
                BackupSubmissionException failure=null;try{await submission.WaitAsync();}catch(BackupSubmissionException error){failure=error;}
                Assert.IsNotNull(failure);Assert.IsTrue(failure.InnerExceptions.Any(e=>e is OperationCanceledException));
                var tasks=await service.QueryTasksAsync();
                Assert.Contains(tasks.Items.Single(t=>t.name=="accepted.jpg").state,new[]{BackupState.Queued,BackupState.Uploading});
                Assert.AreEqual(BackupState.Failed,tasks.Items.Single(t=>t.name=="waiting.jpg").state);
                service.SetDesktopNetworkPolicy(()=>true);await service.WaitForIdleAsync();
                Assert.AreEqual("accepted.jpg",(await service.QueryBackupsAsync()).Items.Single().Name);
            } finally {await service.ShutdownAsync();}
        });
    }
}
