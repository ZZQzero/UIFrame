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
        [UnityTest] public IEnumerator ShutdownDrainsAcceptedQueryAndRejectsNewOperations()=>UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config());
            var query=service.QueryTasksAsync();var closing=service.ShutdownAsync();
            Assert.IsInstanceOf<ObjectDisposedException>(await Observe(service.ResumeAsync()));
            Assert.IsEmpty((await query).Items);await closing;
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
            var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"index.sqlite"));var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,new BackupProtocolFixture {Account="audit"});
            try {
                var scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,root);await library.RefreshAsync(scope);
                var image=(await library.QueryAsync(scope)).Items.Single();var stamp=File.GetLastWriteTimeUtc(photo);
                await service.SubmitPhotosAsync(new[]{image});var accepted=(await service.QueryTasksAsync()).Items.Single();StringAssert.EndsWith(accepted.sha256,image.Version);
                File.WriteAllBytes(photo,new byte[]{4,3,2,1});File.SetLastWriteTimeUtc(photo,stamp);
                BackupSubmissionException failure=null;try{await service.SubmitPhotosAsync(new[]{image});}catch(BackupSubmissionException error){failure=error;}
                Assert.IsNotNull(failure);Assert.AreEqual("SourceChanged",((GalleryException)failure.InnerExceptions.Single()).Code);Assert.AreEqual(2,(await service.QueryTasksAsync()).Items.Count);
            } finally {await library.ShutdownAsync();await service.ShutdownAsync();}
        });
    }
}
