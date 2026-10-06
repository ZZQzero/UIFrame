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

namespace UIFrame.Regression
{
    public sealed class MediaBusinessBoundaryTests
    {
        string root;
        [SetUp] public void SetUp()
        {
            root=Path.Combine(Path.GetTempPath(),"uiframe-business-boundaries-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }
        [TearDown] public void TearDown(){Directory.Delete(root,true);}
        ImageReference Photo(string name="photo",int bytes=4)
        {
            string path=Path.Combine(root,name+".jpg");File.WriteAllBytes(path,new byte[bytes]);
            return ImageReference.FromFile(path);
        }
        static async UniTask<Exception> Failure(UniTask task)
        {try{await task;return null;}catch(Exception error){return error;}}
        static async UniTask<Exception> Failure<T>(UniTask<T> task)
        {try{await task;return null;}catch(Exception error){return error;}}
        static async UniTask Until(Func<bool> predicate)
        {using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));await UniTask.WaitUntil(predicate,cancellationToken:timeout.Token);}

        sealed class Gallery : IMediaTransport,IDisposable
        {
            readonly IMediaTransport previous=NativeMedia.Transport;
            readonly Dictionary<string,MediaRequest> pending=new Dictionary<string,MediaRequest>();
            internal readonly List<MediaRequest> Calls=new List<MediaRequest>();
            internal readonly Queue<MediaResponse> Pages=new Queue<MediaResponse>();
            internal bool HoldAccess;
            internal string FailedOperation;
            internal Gallery(){NativeMedia.Transport=this;}
            public void Start(string json)
            {var request=JsonUtility.FromJson<MediaRequest>(json);Calls.Add(request);pending.Add(request.id,request);}
            public string Poll(string id)
            {
                var request=pending[id];
                if(HoldAccess && request.op=="access")return null;
                pending.Remove(id);
                var response=request.op==FailedOperation
                    ?new MediaResponse {status="error",code="FixtureFailure",error=request.op+" failed"}
                    :request.op=="imagesNext"?Pages.Dequeue():new MediaResponse {status="ok",access="Authorized",boundary="stable",items=Array.Empty<MediaItem>()};
                return JsonUtility.ToJson(response);
            }
            public void Cancel(string id){pending.Remove(id);}
            public bool Pending(string id)=>pending.ContainsKey(id);
            public void Dispose(){NativeMedia.Transport=previous;Assert.IsEmpty(pending,"Native requests must release their owner.");}
            internal void Page(int start,int count,bool more=false)
            {Pages.Enqueue(new MediaResponse {status="ok",hasNext=more,items=Enumerable.Range(start,count).Select(i=>new MediaItem {id=i.ToString(),name=i+".jpg",mime="image/jpeg",version="v1"}).ToArray()});}
        }

        [UnityTest] public IEnumerator GalleryPagesKeepOneScanAndCloseExactlyOnce()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Gallery();native.Page(0,200,true);native.Page(200,1);
            var retained=new List<IReadOnlyList<ImageReference>>();
            Assert.IsTrue(await GameGallery.VisitImagesAsync(page=>{retained.Add(page);return UniTask.FromResult(true);},"album"));
            CollectionAssert.AreEqual(new[]{200,1},retained.Select(p=>p.Count));
            Assert.AreEqual("0",retained[0][0].Id);Assert.AreEqual("200",retained[1][0].Id);
            CollectionAssert.AreEqual(new[]{"imagesOpen","imagesNext","imagesNext","imagesClose"},native.Calls.Select(r=>r.op));
            Assert.AreEqual(1,native.Calls.Select(r=>r.path).Distinct().Count());Assert.AreEqual("album",native.Calls[0].album);
        });

        [UnityTest] public IEnumerator GalleryEarlyStopClosesWithoutFetchingAnotherPage()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Gallery();native.Page(0,200,true);native.Page(200,1);
            Assert.IsFalse(await GameGallery.VisitImagesAsync(_=>UniTask.FromResult(false)));
            CollectionAssert.AreEqual(new[]{"imagesOpen","imagesNext","imagesClose"},native.Calls.Select(r=>r.op));
            Assert.AreEqual(1,native.Pages.Count);
        });

        [UnityTest] public IEnumerator GalleryCallbackFailureKeepsIdentityWhenCloseAlsoFails()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Gallery {FailedOperation="imagesClose"};native.Page(0,1,true);
            var original=new InvalidOperationException("consumer failed");
            LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("GalleryException: imagesClose failed"));
            Assert.AreSame(original,await Failure(GameGallery.VisitImagesAsync(_=>throw original)));
            Assert.AreEqual(1,native.Calls.Count(r=>r.op=="imagesClose"));
        });

        [UnityTest] public IEnumerator GalleryCloseFailureCannotReportSuccessfulVisit()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Gallery {FailedOperation="imagesClose"};native.Page(0,1);
            var failure=await Failure(GameGallery.VisitImagesAsync(_=>UniTask.FromResult(true)));
            Assert.IsInstanceOf<GalleryException>(failure);Assert.AreEqual("imagesClose failed",failure.Message);
            native.FailedOperation=null;native.Page(0,1);
            Assert.AreEqual(1,(await GameGallery.QueryImagesAsync()).Count);
        });

        [UnityTest] public IEnumerator GalleryNextFailureReleasesScanAndAllowsExplicitNewVisit()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Gallery {FailedOperation="imagesNext"};int delivered=0;
            Assert.IsInstanceOf<GalleryException>(await Failure(GameGallery.VisitImagesAsync(_=>{delivered++;return UniTask.FromResult(true);})));Assert.AreEqual(0,delivered);
            Assert.AreEqual(1,native.Calls.Count(r=>r.op=="imagesClose"));
            native.FailedOperation=null;native.Page(0,1);
            Assert.AreEqual(1,(await GameGallery.QueryImagesAsync()).Count);
            Assert.AreEqual(2,native.Calls.Where(r=>r.op=="imagesOpen").Select(r=>r.path).Distinct().Count());
        });

        [UnityTest] public IEnumerator GalleryCancellationBetweenPagesStillClosesWithUncanceledToken()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Gallery();native.Page(0,1,true);using var cancel=new CancellationTokenSource();
            var failure=await Failure(GameGallery.VisitImagesAsync(_=>{cancel.Cancel();return UniTask.FromResult(true);},cancellationToken:cancel.Token));
            Assert.IsInstanceOf<OperationCanceledException>(failure);Assert.AreEqual(cancel.Token,((OperationCanceledException)failure).CancellationToken);
            CollectionAssert.AreEqual(new[]{"imagesOpen","imagesNext","imagesClose"},native.Calls.Select(r=>r.op));
        });

        [UnityTest] public IEnumerator NativeAdmissionLimitReturnsCapacityAfterCanceledRequest()=>UniTask.ToCoroutine(async()=>
        {
            using var native=new Gallery {HoldAccess=true};using var cancel=new CancellationTokenSource();
            var requests=new List<Task<LibraryAccess>>();
            try {
                requests.Add(GameGallery.GetLibraryAccessAsync(cancel.Token).AsTask());
                for(int i=1;i<32;i++)requests.Add(GameGallery.GetLibraryAccessAsync().AsTask());
                var failure=await Failure(GameGallery.GetLibraryAccessAsync());
                Assert.IsInstanceOf<GalleryException>(failure);Assert.AreEqual("MediaQueueFull",((GalleryException)failure).Code);Assert.AreEqual(32,native.Calls.Count);
                cancel.Cancel();Assert.IsInstanceOf<OperationCanceledException>(await Failure(requests[0].AsUniTask()));
                native.HoldAccess=false;Assert.AreEqual(LibraryAccess.Authorized,await GameGallery.GetLibraryAccessAsync());
            } finally {native.HoldAccess=false;foreach(var request in requests)await Failure(request.AsUniTask());}
        });

        [UnityTest] public IEnumerator DirectoryCancellationAfterFirstPageDoesNotDeliverRemainingFiles()=>UniTask.ToCoroutine(async()=>
        {
            for(int i=0;i<201;i++)Photo(i.ToString());
            using var cancel=new CancellationTokenSource();int delivered=0;
            var failure=await Failure(GameImageDirectory.VisitAsync(root,page=>{delivered+=page.Count;cancel.Cancel();return UniTask.FromResult(true);},cancellationToken:cancel.Token));
            Assert.IsInstanceOf<OperationCanceledException>(failure);Assert.AreEqual(200,delivered);
            Assert.AreEqual(201,(await GameImageDirectory.QueryAsync(root)).Count);
        });

        [UnityTest] public IEnumerator ImportValidatesEverySourceBeforeAllocatingTemporaryStorage()=>UniTask.ToCoroutine(async()=>
        {
            string init=ImagePaths.NewDirectory(),session=Directory.GetParent(init).FullName;Directory.Delete(init);
            string[] before=Directory.GetDirectories(session);var source=Photo();
            Assert.IsInstanceOf<FileNotFoundException>(await Failure(GameGallery.ImportFilesAsync(new[]{source.Id,Path.Combine(root,"missing.jpg")})));
            CollectionAssert.AreEquivalent(before,Directory.GetDirectories(session));Assert.IsTrue(File.Exists(source.Id));
            using var selection=await GameGallery.ImportFilesAsync(new[]{source.Id});Assert.AreEqual(1,selection.Items.Count);
        });

        [UnityTest] public IEnumerator LibraryPagingRejectsForeignScopeAndDatabaseCursors()=>UniTask.ToCoroutine(async()=>
        {
            Photo("a");Photo("b");Photo("c");
            var first=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"first.sqlite"));
            var second=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"second.sqlite"));
            var scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,root);
            var otherScope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,root,true);
            try {
                await first.RefreshAsync(scope);await first.RefreshAsync(otherScope);await second.RefreshAsync(scope);
                var page=await first.QueryAsync(scope,1);Assert.IsNotNull(page.Next);
                Assert.IsInstanceOf<InvalidOperationException>(await Failure(first.QueryAsync(otherScope,1,page.Next)));
                Assert.IsInstanceOf<InvalidOperationException>(await Failure(second.QueryAsync(scope,1,page.Next)));
                var next=await first.QueryAsync(scope,1,page.Next);Assert.AreNotEqual(page.Items[0].Id,next.Items[0].Id);
                Assert.AreEqual(3,(await first.QueryAsync(scope)).Items.Count);
            } finally {await first.ShutdownAsync();await second.ShutdownAsync();}
        });

        [UnityTest] public IEnumerator LibraryPagesTraverseBoundaryWithoutDuplicatesAndSurviveReopen()=>UniTask.ToCoroutine(async()=>
        {
            for(int i=0;i<201;i++)Photo(i.ToString("D3"));
            string path=Path.Combine(root,"library.sqlite");var scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,root);
            var library=await ImageLibraryIndex.OpenAsync(path);
            try {
                await library.RefreshAsync(scope);var first=await library.QueryAsync(scope,200);
                Assert.AreEqual(200,first.Items.Count);Assert.IsNotNull(first.Next);
                await library.ShutdownAsync();library=await ImageLibraryIndex.OpenAsync(path);
                var last=await library.QueryAsync(scope,200,first.Next);Assert.AreEqual(1,last.Items.Count);Assert.IsNull(last.Next);
                Assert.AreEqual(201,first.Items.Concat(last.Items).Select(p=>p.Id).Distinct().Count());
                CollectionAssert.AreEqual(first.Items.Concat(last.Items).Select(p=>p.Id).OrderBy(p=>p,StringComparer.Ordinal),first.Items.Concat(last.Items).Select(p=>p.Id));
            } finally {await library.ShutdownAsync();}
        });

        [UnityTest] public IEnumerator LibraryInvalidPageSizesDoNotPoisonLaterReads()=>UniTask.ToCoroutine(async()=>
        {
            Photo();var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"library.sqlite"));
            var scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,root);
            try {
                await library.RefreshAsync(scope);var position=await library.GetPositionAsync(scope);
                foreach(int size in new[]{0,-1,201,int.MaxValue}) {
                    Assert.IsInstanceOf<ArgumentOutOfRangeException>(await Failure(library.QueryAsync(scope,size)));
                    Assert.IsInstanceOf<ArgumentOutOfRangeException>(await Failure(library.ReadChangesAsync(scope,position,size)));
                }
                Assert.AreEqual(1,(await library.QueryAsync(scope,1)).Items.Count);
            } finally {await library.ShutdownAsync();}
        });

        [UnityTest] public IEnumerator ThumbnailLoadFailureCompletesAllWaitersAndAllowsExplicitReload()=>UniTask.ToCoroutine(async()=>
        {
            var source=Photo();var original=new IOException("decode failed");var gate=new UniTaskCompletionSource<ImageTexture>();int loads=0;
            var cache=new ImageThumbnailCache(0,(_,__,___)=>++loads==1?gate.Task:UniTask.FromResult(new ImageTexture(new Texture2D(8,4))));
            try {
                var first=cache.AcquireAsync(source);var second=cache.AcquireAsync(source);gate.TrySetException(original);
                Assert.AreSame(original,await Failure(first));Assert.AreSame(original,await Failure(second));
                Assert.AreEqual(0,cache.Statistics.PendingKeys);Assert.AreEqual(0,cache.Statistics.Waiters);
                using var valid=await cache.AcquireAsync(source);Assert.AreEqual(2,loads);Assert.AreEqual(8,valid.Texture.width);
            } finally {await cache.ShutdownAsync();}
        });

        [UnityTest] public IEnumerator ThumbnailRequestSnapshotsOptionsAndSeparatesRenderingVariants()=>UniTask.ToCoroutine(async()=>
        {
            var source=Photo();var gate=new UniTaskCompletionSource();int loads=0;var observed=new List<ImagePreviewOptions>();
            var cache=new ImageThumbnailCache(1024*1024,async(_,options,__)=>{loads++;observed.Add(options);await gate.Task;return new ImageTexture(new Texture2D(8,4));});
            try {
                var options=new ImagePreviewOptions {MaxEdge=64,Readable=false};var pending=cache.AcquireAsync(source,options);
                options.MaxEdge=128;options.Readable=true;Assert.AreEqual(64,observed[0].MaxEdge);Assert.IsFalse(observed[0].Readable);
                gate.TrySetResult();using var first=await pending;
                using var hit=await cache.AcquireAsync(source,new ImagePreviewOptions {MaxEdge=64});Assert.AreSame(first.Texture,hit.Texture);
                using var variant=await cache.AcquireAsync(source,options);Assert.AreNotSame(first.Texture,variant.Texture);Assert.AreEqual(2,loads);
            } finally {gate.TrySetResult();await cache.ShutdownAsync();}
        });

        [UnityTest] public IEnumerator ThumbnailShutdownWaitsForActualLoadAndReleasesLateResult()=>UniTask.ToCoroutine(async()=>
        {
            var source=Photo();var gate=new UniTaskCompletionSource<ImageTexture>();
            var cache=new ImageThumbnailCache(0,(_,__,___)=>gate.Task);var pending=cache.AcquireAsync(source);
            var closing=cache.ShutdownAsync().AsTask();Assert.IsFalse(closing.IsCompleted);
            Assert.Throws<ObjectDisposedException>(()=>cache.AcquireAsync(source));
            var texture=new Texture2D(8,4);gate.TrySetResult(new ImageTexture(texture));
            Assert.IsInstanceOf<OperationCanceledException>(await Failure(pending));await closing;
            Assert.IsTrue(texture==null);Assert.AreEqual(0,cache.Statistics.PendingKeys);Assert.AreEqual(0,cache.Statistics.Waiters);
            await cache.ShutdownAsync();
        });

        [UnityTest] public IEnumerator ThumbnailShutdownPreservesLateFailureAndStillReleasesAdmission()=>UniTask.ToCoroutine(async()=>
        {
            var gate=new UniTaskCompletionSource<ImageTexture>();var cache=new ImageThumbnailCache(0,(_,__,___)=>gate.Task);
            var pending=cache.AcquireAsync(Photo());var closing=cache.ShutdownAsync().AsTask();var original=new IOException("late decode failed");
            gate.TrySetException(original);Assert.AreSame(original,await Failure(pending));Assert.AreSame(original,await Failure(closing.AsUniTask()));
            Assert.AreEqual(0,cache.Statistics.PendingKeys);Assert.AreEqual(0,cache.Statistics.Waiters);
            Assert.AreSame(original,await Failure(cache.ShutdownAsync()));
        });

        [UnityTest] public IEnumerator ThumbnailWaiterBudgetRejectsOnlyOverflowAndRecovers()=>UniTask.ToCoroutine(async()=>
        {
            var gate=new UniTaskCompletionSource<ImageTexture>();int loads=0;var source=Photo();
            var cache=new ImageThumbnailCache(0,(_,__,___)=>{loads++;return gate.Task;});
            var waiting=new List<UniTask<ImageTexture>>();
            try {
                for(int i=0;i<512;i++)waiting.Add(cache.AcquireAsync(source));
                var failure=Assert.Throws<GalleryException>(()=>cache.AcquireAsync(source));Assert.AreEqual("ImageQueueFull",failure.Code);
                Assert.AreEqual(512,cache.Statistics.Waiters);Assert.AreEqual(1,loads);
            } finally {
                gate.TrySetResult(new ImageTexture(new Texture2D(8,4)));
                foreach(var task in waiting)(await task).Dispose();await cache.ShutdownAsync();
            }
            Assert.AreEqual(0,cache.Statistics.Waiters);Assert.AreEqual(0,cache.Statistics.PendingKeys);
        });

        BackupConfiguration Config(string directory="backup",long budget=1024)=>new BackupConfiguration {
            ServerUrl="https://api.invalid",Account="integration-user",AccessToken=()=>"fixture",
            StorageDirectory=Path.Combine(root,directory),DiskBudgetBytes=budget};

        [UnityTest] public IEnumerator SubmissionWaiterCancellationDoesNotCancelDurablePreparation()=>UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config(budget:6),NativeBackup.Call,false,new BackupProtocolFixture());
            try {
                service.SetDesktopNetworkPolicy(()=>false);
                await service.SubmitAndWaitAsync("aa",new[]{Photo("occupied",6)});
                var request=await service.SubmitAsync("bb",new[]{Photo("waiting",6)});
                await Until(()=>service.IsWaitingForCapacity);
                using var cancel=new CancellationTokenSource();var canceled=request.WaitAsync(cancel.Token);var kept=request.WaitAsync().AsTask();cancel.Cancel();
                Assert.IsInstanceOf<OperationCanceledException>(await Failure(canceled));Assert.IsFalse(request.IsCompleted);Assert.IsFalse(kept.IsCompleted);
                service.SetDesktopNetworkPolicy(()=>true);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await kept.AsUniTask().AttachExternalCancellation(timeout.Token);await service.WaitForIdleAsync(timeout.Token);
                Assert.AreEqual(2,(await service.QueryBackupsAsync()).Items.Count);Assert.AreEqual(128,service.AvailablePreparationSlots);
            } finally {await service.ShutdownAsync();}
        });

        [UnityTest] public IEnumerator InvalidLastSubmissionMemberLeavesNoRegistrationOrRetainedSource()=>UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,new BackupProtocolFixture());
            using var selection=await GameGallery.ImportFilesAsync(new[]{Photo().Id});
            string transient=selection.Items[0].Id;
            try {
                Assert.IsInstanceOf<ArgumentException>(await Failure(service.SubmitAsync("aa",new[]{selection.Items[0],null})));
                Assert.IsNull(await service.QuerySubmissionAsync("aa"));Assert.IsEmpty((await service.QueryTasksAsync()).Items);Assert.AreEqual(128,service.AvailablePreparationSlots);
                selection.Dispose();Assert.IsFalse(File.Exists(transient));
                await service.SubmitAndWaitAsync("aa",new[]{Photo("valid")});await service.WaitForIdleAsync();
                Assert.AreEqual(1,(await service.QueryBackupsAsync()).Items.Count);
            } finally {await service.ShutdownAsync();}
        });

        [UnityTest] public IEnumerator DuplicateSubmissionCannotReplacePersistedMembers()=>UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,new BackupProtocolFixture());
            try {
                service.SetDesktopNetworkPolicy(()=>false);var first=await service.SubmitAndWaitAsync("aa",new[]{Photo("first")});
                Assert.IsInstanceOf<InvalidOperationException>(await Failure(service.SubmitAsync("aa",new[]{Photo("second")})));
                var saved=await service.QuerySubmissionAsync("aa");Assert.AreEqual(first.Items[0].TaskId,saved.Items[0].TaskId);
                Assert.AreEqual(1,(await service.QueryTasksAsync()).Items.Count);Assert.AreEqual(128,service.AvailablePreparationSlots);
            } finally {await service.ShutdownAsync();}
        });

        [UnityTest] public IEnumerator BackupPagingKeepsUpperBoundWhenNewSubmissionArrives()=>UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,new BackupProtocolFixture());
            try {
                service.SetDesktopNetworkPolicy(()=>false);await service.SubmitAndWaitAsync("aa",new[]{Photo("first"),Photo("second"),Photo("third")});
                var first=await service.QueryTasksAsync(new BackupTaskQuery {PageSize=1});
                await service.SubmitAndWaitAsync("bb",new[]{Photo("later")});
                var ids=new List<string>(first.Items.Select(i=>i.id));var cursor=first.Next;
                while(cursor!=null){var page=await service.QueryTasksAsync(new BackupTaskQuery {PageSize=1,Cursor=cursor});ids.AddRange(page.Items.Select(i=>i.id));cursor=page.Next;Assert.LessOrEqual(ids.Count,3);}
                Assert.AreEqual(3,ids.Distinct().Count());Assert.AreEqual(4,(await service.QueryTasksAsync()).Items.Count);
            } finally {await service.ShutdownAsync();}
        });

        [UnityTest] public IEnumerator BackupCursorRejectsOtherStoreAndChangedFilter()=>UniTask.ToCoroutine(async()=>
        {
            var first=await ImageBackupService.CreateAsync(Config("first"),NativeBackup.Call,false,new BackupProtocolFixture());
            var second=await ImageBackupService.CreateAsync(Config("second"),NativeBackup.Call,false,new BackupProtocolFixture());
            try {
                first.SetDesktopNetworkPolicy(()=>false);second.SetDesktopNetworkPolicy(()=>false);
                await first.SubmitAndWaitAsync("aa",new[]{Photo("one"),Photo("two")});
                var page=await first.QueryTasksAsync(new BackupTaskQuery {PageSize=1});
                Assert.IsInstanceOf<ArgumentException>(await Failure(first.QueryTasksAsync(new BackupTaskQuery {PageSize=1,State=BackupState.Completed,Cursor=page.Next})));
                Assert.IsInstanceOf<ArgumentException>(await Failure(second.QueryTasksAsync(new BackupTaskQuery {PageSize=1,Cursor=page.Next})));
                Assert.AreEqual(1,(await first.QueryTasksAsync(new BackupTaskQuery {PageSize=1,Cursor=page.Next})).Items.Count);
                await first.ShutdownAsync();first=await ImageBackupService.CreateAsync(Config("first"),NativeBackup.Call,false,new BackupProtocolFixture());
                var continued=await first.QueryTasksAsync(new BackupTaskQuery {PageSize=1,Cursor=page.Next});
                Assert.AreEqual(1,continued.Items.Count);Assert.AreNotEqual(page.Items[0].id,continued.Items[0].id);
            } finally {await first.ShutdownAsync();await second.ShutdownAsync();}
        });
    }
}
