using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Media;
using Game.Media.Backup;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UIFrame.Regression
{
    public sealed class MediaTests
    {
        string root, imagePath;
        [SetUp] public void Setup()
        {
            root = Path.Combine(Path.GetTempPath(), "uiframe-media-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            imagePath = Path.Combine(root, "图片.png");
            var texture = new Texture2D(8, 4, TextureFormat.RGBA32, false);
            texture.SetPixels(Enumerable.Repeat(new Color(1, 0, 0, 0.5f),32).ToArray()); texture.Apply();
            File.WriteAllBytes(imagePath, texture.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(texture);
        }
        [TearDown] public void TearDown() { if(Directory.Exists(root)) Directory.Delete(root,true); }
        BackupConfiguration Config(string account = "integration-user") => new BackupConfiguration
        {
            ServerUrl = "http://127.0.0.1:18787", Account = account, AccessToken = () => "uiframe-integration-test-token",
            StorageDirectory = Path.Combine(root,"backup"), AllowDevelopmentHttp = true
        };

        [UnityTest] public IEnumerator PreviewCancellationDoesNotLeaveDecodedTextures() => UniTask.ToCoroutine(async () =>
        {
            var fixture = new Texture2D(509, 503, TextureFormat.RGBA32, false);
            File.WriteAllBytes(imagePath, fixture.EncodeToPNG()); UnityEngine.Object.DestroyImmediate(fixture);
            var before = Resources.FindObjectsOfTypeAll<Texture2D>().Select(x => x.GetInstanceID()).ToHashSet();
            Texture2D[] Residuals() => Resources.FindObjectsOfTypeAll<Texture2D>().Where(x => !before.Contains(x.GetInstanceID()) &&
                (x.width == 509 && x.height == 503 || x.width == 17)).ToArray();
            try
            {
                int canceled = 0;
                for (int i = 0; i < 12; i++)
                {
                    using var cancel = new CancellationTokenSource();
                    var task = GameImageReader.LoadPreviewAsync(ImageReference.FromFile(imagePath), new ImagePreviewOptions { MaxEdge = 17 }, cancel.Token);
                    await UniTask.DelayFrame(1 + i % 3); cancel.Cancel();
                    try { using var image = await task; } catch (OperationCanceledException) { canceled++; }
                }
                await UniTask.DelayFrame(3);
                Assert.Greater(canceled, 0); Assert.IsEmpty(Residuals(), "Canceled loads must release the download handler's source texture.");
                using var normal = await GameImageReader.LoadPreviewAsync(ImageReference.FromFile(imagePath));
                Assert.IsFalse(normal.Texture.isReadable); Assert.AreEqual(1, normal.Texture.mipmapCount);
            }
            finally { foreach (var texture in Residuals()) UnityEngine.Object.DestroyImmediate(texture); }
        });

        [UnityTest] public IEnumerator PixelBudgetIsExplicitAndReadablePreviewRemainsAvailable() => UniTask.ToCoroutine(async () =>
        {
            Exception failure = null;
            try { using var image = await GameImageReader.LoadPreviewAsync(ImageReference.FromFile(imagePath), new ImagePreviewOptions { MaxPixels = 31 }); }
            catch (Exception error) { failure = error; }
            Assert.AreEqual("ImageTooLarge", ((GalleryException)failure).Code);
            using var accepted = await GameImageReader.LoadPreviewAsync(ImageReference.FromFile(imagePath), new ImagePreviewOptions { MaxPixels = 32, Readable = true });
            Assert.AreEqual(32, accepted.Texture.GetPixels32().Length); Assert.AreEqual(1, accepted.Texture.mipmapCount);
            using var canceled = new CancellationTokenSource();
            var first = GameImageReader.LoadPreviewAsync(ImageReference.FromFile(imagePath));
            var waiting = GameImageReader.LoadPreviewAsync(ImageReference.FromFile(imagePath), cancellationToken: canceled.Token);
            canceled.Cancel(); failure = null;
            try { using var unused = await waiting; } catch (Exception error) { failure = error; }
            Assert.IsInstanceOf<OperationCanceledException>(failure);
            using var finished = await first;
            using var next = await GameImageReader.LoadPreviewAsync(ImageReference.FromFile(imagePath));
        });

        [Test] public void JpegHeaderSkipsRepeatedMetadataAndStillReadsExif()
        {
            string path = Path.Combine(root, "metadata.jpg");
            using (var file = File.Create(path))
            {
                file.Write(new byte[] {255,216}, 0, 2);
                var segment = new byte[65537]; segment[0]=255; segment[1]=225; segment[2]=255; segment[3]=255;
                for (int i=0;i<128;i++) file.Write(segment,0,segment.Length);
                byte[] exif = {255,225,0,34,69,120,105,102,0,0,73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,6,0,0,0,0,0,0,0};
                file.Write(exif,0,exif.Length);
                byte[] dimensions = {255,192,0,11,8,0,8,0,16,1,1,17,0,255,217}; file.Write(dimensions,0,dimensions.Length);
            }
            var header = ImageHeader.ReadFile(path);
            Assert.AreEqual(16,header.Width); Assert.AreEqual(8,header.Height); Assert.AreEqual(6,header.Orientation);
        }

        [UnityTest] public IEnumerator DirectoryVisitorBoundsPagesAndPreservesCallbackFailure() => UniTask.ToCoroutine(async () =>
        {
            for(int i=0;i<400;i++) File.WriteAllText(Path.Combine(root,i+".png"), "metadata only");
            int count=0, pages=0;
            await GameImageDirectory.VisitAsync(root, page => { Assert.LessOrEqual(page.Count,200); count+=page.Count; pages++; return UniTask.FromResult(true); });
            Assert.AreEqual(401,count); Assert.AreEqual(3,pages);
            int stoppedPages=0;
            Assert.IsFalse(await GameImageDirectory.VisitAsync(root,page => { stoppedPages++; return UniTask.FromResult(false); }));
            Assert.AreEqual(1,stoppedPages);
            var original = new Exception("page consumer failed"); Exception observed=null; int calls=0;
            try { await GameImageDirectory.VisitAsync(root,page => { calls++; throw original; }); } catch(Exception error) { observed=error; }
            Assert.AreSame(original,observed); Assert.AreEqual(1,calls);
        });

        [Test] public void DirectoryIdentityHasStablePropertyOrder()
        {
            string compact="{\"bookmarkId\":\"directory-id\",\"relative\":\"photo.png\"}";
            Assert.AreEqual(compact,ImageIdentity.Directory("{\"relative\":\"photo.png\",\"bookmarkId\":\"directory-id\"}"));
            Assert.AreEqual(compact,ImageIdentity.Directory(compact));
            Assert.AreEqual("content://provider/a",ImageIdentity.Directory("content://provider/a"));
        }

        sealed class UnknownLengthContent : System.Net.Http.HttpContent
        {
            readonly byte[] data;
            internal UnknownLengthContent(int size) { data=Enumerable.Repeat((byte)'x',size).ToArray(); }
            protected override bool TryComputeLength(out long length) { length=0; return false; }
            protected override System.Threading.Tasks.Task<System.IO.Stream> CreateContentReadStreamAsync() => System.Threading.Tasks.Task.FromResult<Stream>(new MemoryStream(data,false));
            protected override System.Threading.Tasks.Task SerializeToStreamAsync(Stream stream,System.Net.TransportContext context) => throw new InvalidOperationException("Response must be streamed.");
        }
        [UnityTest] public IEnumerator BackupResponseLimitCoversKnownAndUnknownLengths() => UniTask.ToCoroutine(async () =>
        {
            using(var boundary=new System.Net.Http.HttpResponseMessage { Content=new UnknownLengthContent(65536) })
                Assert.AreEqual(65536,(await ImageBackupService.ReadResponse(boundary,default)).Length);
            foreach(bool known in new[]{false,true})
            {
                using var oversized=new System.Net.Http.HttpResponseMessage { Content=known ? (System.Net.Http.HttpContent)new System.Net.Http.ByteArrayContent(new byte[65537]) : new UnknownLengthContent(65537) };
                Exception observed=null; try { await ImageBackupService.ReadResponse(oversized,default); } catch(Exception error) { observed=error; }
                Assert.IsInstanceOf<IOException>(observed);
            }
        });

        sealed class ResponseHandler : System.Net.Http.HttpMessageHandler
        {
            internal Func<System.Net.Http.HttpResponseMessage> respond;
            internal int requests;
            protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken token)
            { requests++; return System.Threading.Tasks.Task.FromResult(respond()); }
        }
        sealed class StreamResponse : System.Net.Http.HttpContent
        {
            readonly Stream stream;
            internal StreamResponse(Stream stream) { this.stream=stream; }
            protected override bool TryComputeLength(out long length) { length=0; return false; }
            protected override System.Threading.Tasks.Task<Stream> CreateContentReadStreamAsync() => System.Threading.Tasks.Task.FromResult(stream);
            protected override System.Threading.Tasks.Task SerializeToStreamAsync(Stream target,System.Net.TransportContext context) => throw new InvalidOperationException("Response must be streamed.");
        }
        sealed class StalledStream : MemoryStream
        {
            readonly System.Threading.Tasks.TaskCompletionSource<int> pending=new System.Threading.Tasks.TaskCompletionSource<int>();
            internal volatile bool closed, reading;
            public override System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token) { reading=true; return pending.Task; }
            protected override void Dispose(bool disposing)
            { closed=true; pending.TrySetException(new ObjectDisposedException(nameof(StalledStream))); base.Dispose(disposing); }
        }
        sealed class CountingStream : MemoryStream
        {
            internal int read;
            internal CountingStream(byte[] data) : base(data,false) { }
            public override async System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)
            { int n=await base.ReadAsync(buffer,offset,count,token); read+=n; return n; }
        }
        [UnityTest] public IEnumerator RequestDeadlineIncludesBodyAndPreservesCallerCancellation() => UniTask.ToCoroutine(async () =>
        {
            var config=Config(); config.RequestTimeout=TimeSpan.FromMilliseconds(250);
            var handler=new ResponseHandler();
            using var service=await ImageBackupService.CreateAsync(config,NativeBackup.Call,false,handler);
            foreach(bool cancelByCaller in new[]{false,true})
            {
                var stream=new StalledStream(); handler.respond=()=>new System.Net.Http.HttpResponseMessage { Content=new StreamResponse(stream) };
                using var cancellation=new CancellationTokenSource();
                var pending=service.RefreshServerCapabilitiesAsync(cancellation.Token);
                if(cancelByCaller) cancellation.Cancel();
                Exception observed=null; try { await pending; } catch(Exception error) { observed=error; }
                if(cancelByCaller) Assert.IsInstanceOf<OperationCanceledException>(observed); else Assert.IsInstanceOf<TimeoutException>(observed);
                Assert.IsTrue(stream.closed);
            }
            handler.respond=()=>new System.Net.Http.HttpResponseMessage { Content=new System.Net.Http.StringContent("{\"protocolVersion\":1,\"account\":\"integration-user\",\"chunkBytes\":1024,\"maxFileBytes\":1048576}") };
            await service.RefreshServerCapabilitiesAsync(); Assert.AreEqual(3,handler.requests);
        });

        [Test] public void GpuImageTransformPreservesAllExifOrientationsAndAlpha()
        {
            var source = new Texture2D(2, 3, TextureFormat.RGBA32, false);
            Color32[] pixels = { new Color32(255,0,0,128), new Color32(0,255,0,255), new Color32(0,0,255,255),
                new Color32(255,255,255,255), new Color32(0,0,0,255), new Color32(255,255,0,255) };
            source.SetPixels32(pixels); source.Apply(false);
            int[][] expected = { new[]{0,1,2,3,4,5}, new[]{1,0,3,2,5,4}, new[]{5,4,3,2,1,0}, new[]{4,5,2,3,0,1},
                new[]{5,3,1,4,2,0}, new[]{1,3,5,0,2,4}, new[]{0,2,4,1,3,5}, new[]{4,2,0,5,3,1} };
            var render = typeof(GameImageReader).GetMethod("RenderImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            try
            {
                for (int orientation = 1; orientation <= 8; orientation++)
                {
                    var result = (Texture2D)render.Invoke(null, new object[] { source, orientation, 3, false, Color.white });
                    try
                    {
                        Assert.AreEqual(orientation >= 5 ? 3 : 2, result.width);
                        var actual = result.GetPixels32();
                        for (int i = 0; i < actual.Length; i++)
                        {
                            var wanted = pixels[expected[orientation - 1][i]];
                            Assert.That(actual[i].r, Is.EqualTo(wanted.r).Within(2), "orientation="+orientation+", pixel="+i);
                            Assert.That(actual[i].g, Is.EqualTo(wanted.g).Within(2)); Assert.That(actual[i].b, Is.EqualTo(wanted.b).Within(2));
                            Assert.That(actual[i].a, Is.EqualTo(wanted.a).Within(2));
                        }
                    }
                    finally { UnityEngine.Object.DestroyImmediate(result); }
                }
                var flattened = (Texture2D)render.Invoke(null, new object[] { source, 1, 3, true, Color.white });
                try
                {
                    var pixel = flattened.GetPixels32()[0];
                    Assert.That(pixel.r, Is.EqualTo(255).Within(2)); Assert.That(pixel.g, Is.EqualTo(127).Within(3));
                    Assert.That(pixel.b, Is.EqualTo(127).Within(3)); Assert.AreEqual(255, pixel.a);
                }
                finally { UnityEngine.Object.DestroyImmediate(flattened); }
            }
            finally { UnityEngine.Object.DestroyImmediate(source); }
        }

        [UnityTest] public IEnumerator DirectorySnapshotIsPagedAndRecursiveIsExplicit() => UniTask.ToCoroutine(async () =>
        {
            Directory.CreateDirectory(Path.Combine(root,"nested")); File.Copy(imagePath,Path.Combine(root,"nested","two.png"));
            File.WriteAllText(Path.Combine(root,"ignore.txt"),"not an image");
            var shallow = await GameImageDirectory.QueryAsync(root); var deep = await GameImageDirectory.QueryAsync(root,true);
            Assert.AreEqual(1,shallow.Count); Assert.AreEqual(2,deep.Count); Assert.AreEqual(1,deep.GetPage(1,1).Count);
            Assert.Throws<ArgumentOutOfRangeException>(()=>deep.GetPage(0,0));
        });

        [UnityTest] public IEnumerator ExportSurvivesSelectionDisposalAndIsDeletedByOwner() => UniTask.ToCoroutine(async () =>
        {
            var selection=await GameGallery.ImportFilesAsync(new[]{imagePath});
            var reference=selection.Items[0]; var file=await GameImageReader.ExportFileAsync(reference); string path=file.LocalPath;
            selection.Dispose(); Assert.IsTrue(File.Exists(path));
            Exception failure=null; try { await GameImageReader.ExportFileAsync(reference); } catch(Exception error) { failure=error; }
            Assert.IsInstanceOf<ObjectDisposedException>(failure);
            file.Dispose(); file.Dispose(); Assert.IsFalse(File.Exists(path)); Assert.IsTrue(File.Exists(imagePath));
        });

        [UnityTest] public IEnumerator PreviewDownsizesAndDisposesOwnedUnityObjects() => UniTask.ToCoroutine(async () =>
        {
            var preview=await GameImageReader.LoadPreviewAsync(ImageReference.FromFile(imagePath),new ImagePreviewOptions{MaxEdge=4});
            var texture=preview.Texture; var sprite=preview.Sprite;
            Assert.AreEqual(4,texture.width); Assert.AreEqual(2,texture.height); Assert.AreSame(sprite,preview.Sprite);
            preview.Dispose(); preview.Dispose(); Assert.IsTrue(texture==null); Assert.IsTrue(sprite==null);
        });

        [UnityTest] public IEnumerator JpegExportHasMatchingFormatAndDimensions() => UniTask.ToCoroutine(async () =>
        {
            using var file=await GameImageReader.ExportFileAsync(ImageReference.FromFile(imagePath),new ImageExportOptions{Mode=ImageExportMode.Jpeg,MaxEdge=4});
            Assert.AreEqual("image/jpeg",file.MimeType); var bytes=File.ReadAllBytes(file.LocalPath);
            Assert.AreEqual(255,bytes[0]); Assert.AreEqual(216,bytes[1]);
            using var preview=await GameImageReader.LoadPreviewAsync(ImageReference.FromFile(file.LocalPath)); Assert.AreEqual(4,preview.Texture.width);
        });

        [UnityTest] public IEnumerator PreCanceledImportDoesNotDeliverSelection() => UniTask.ToCoroutine(async () =>
        {
            using var cts=new CancellationTokenSource(); cts.Cancel(); Exception failure=null;
            try { await GameGallery.ImportFilesAsync(new[]{imagePath},cts.Token); } catch(Exception error) { failure=error; }
            Assert.IsInstanceOf<OperationCanceledException>(failure);
        });

        [UnityTest] public IEnumerator InvalidImageDoesNotBecomeSuccessfulPreview() => UniTask.ToCoroutine(async () =>
        {
            File.WriteAllText(imagePath,"not png"); Exception failure=null;
            try { using var image=await GameImageReader.LoadPreviewAsync(ImageReference.FromFile(imagePath)); } catch(Exception error) { failure=error; }
            Assert.IsInstanceOf<GalleryException>(failure);
        });

        [Test] public void AndroidBuildPermissionConfigurationIsOptInAndIdempotent()
        {
            var method = typeof(UIFrame.Editor.GalleryBuildProcessor).GetMethod("OnPostGenerateGradleAndroidProject");
            if (method == null) Assert.Ignore("Requires the active Android build target.");
            string manifest = Path.Combine(root,"src","main","AndroidManifest.xml"); Directory.CreateDirectory(Path.GetDirectoryName(manifest));
            File.WriteAllText(manifest,"<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\"><application /></manifest>");
            var settings = UIFrame.Editor.GalleryBuildSettings.instance; bool previous = settings.enableLibraryRead;
            try
            {
                var processor = new UIFrame.Editor.GalleryBuildProcessor(); settings.enableLibraryRead = false;
                method.Invoke(processor,new object[]{root}); Assert.IsFalse(File.ReadAllText(manifest).Contains("READ_MEDIA_IMAGES"));
                settings.enableLibraryRead = true; method.Invoke(processor,new object[]{root}); method.Invoke(processor,new object[]{root});
                var xml = new System.Xml.XmlDocument(); xml.Load(manifest);
                Assert.AreEqual(3,xml.GetElementsByTagName("uses-permission").Count);
                StringAssert.Contains("GalleryBridge",File.ReadAllText(Path.Combine(root,"proguard-unity.txt")));
            }
            finally { settings.enableLibraryRead = previous; }
        }


        [UnityTest] public IEnumerator RepositoryAcceptReopenPaginationAndAccountIsolation() => UniTask.ToCoroutine(async()=>
        {
            string operation=Guid.NewGuid().ToString("N"),id;
            var service=await ImageBackupService.CreateAsync(Config());
            try
            {
                id=(await service.EnqueueAsync(operation,new[]{ImageReference.FromFile(imagePath)}))[0];
                var page=await service.QueryTasksAsync(new BackupTaskQuery {PageSize=1});Assert.AreEqual(id,page.Items[0].id);
                Assert.IsTrue((await service.QueryPreparationAsync(operation)).Accepted);
                await service.PauseAsync();
            }
            finally{await service.ShutdownAsync();}
            service=await ImageBackupService.CreateAsync(Config());
            try
            {
                Assert.IsTrue(service.IsPaused);Assert.AreEqual(id,(await service.QueryTasksAsync()).Items[0].id);
                Assert.AreEqual(1,(await service.GetSummaryAsync())[BackupState.Queued]);
                using var other=await ImageBackupService.CreateAsync(Config("another-account"));Assert.IsEmpty((await other.QueryTasksAsync()).Items);
                await service.ResumeAsync();await service.CancelAsync(id);Assert.AreEqual(BackupState.Canceled,(await service.GetTaskAsync(id)).state);
            }
            finally{await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator PreparationBudgetFailureIsAtomicAndCapacityReturns() => UniTask.ToCoroutine(async()=>
        {
            var config=Config();config.DiskBudgetBytes=new FileInfo(imagePath).Length;var service=await ImageBackupService.CreateAsync(config);
            try
            {
                Exception failure=null;try{await service.EnqueueAsync(new[]{ImageReference.FromFile(imagePath),ImageReference.FromFile(imagePath)});}catch(Exception error){failure=error;}
                Assert.IsInstanceOf<BackupBudgetExceededException>(failure);Assert.IsEmpty((await service.QueryTasksAsync()).Items);
                var ids=await service.EnqueueAsync(new[]{ImageReference.FromFile(imagePath)});Assert.AreEqual(1,ids.Count);
            }
            finally{await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator DurableOperationsAreIdempotentAndGlobalPauseIsIndependent() => UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config());
            try
            {
                var first=(await service.EnqueueAsync(new[]{ImageReference.FromFile(imagePath)}))[0];
                string operation=Guid.NewGuid().ToString("N");var command=new BackupOperationCommand {OperationId=operation,Action=BackupAction.Pause,TaskIds=new[]{first}};
                await service.SubmitOperationAsync(command);Assert.AreEqual(BackupOperationPhase.Completed,(await service.WaitOperationAsync(operation)).Phase);
                Assert.AreEqual(BackupState.Paused,(await service.GetTaskAsync(first)).state);
                await service.SubmitOperationAsync(command);Assert.AreEqual(1,(await service.QueryOperationAsync(operation)).SelectedCount);
                Exception failure=null;try{await service.SubmitOperationAsync(new BackupOperationCommand {OperationId=operation,Action=BackupAction.Cancel,TaskIds=new[]{first}});}catch(Exception error){failure=error;}
                Assert.IsInstanceOf<BackupRepositoryException>(failure);
                await service.PauseAsync();var resume=await service.SubmitOperationAsync(new BackupOperationCommand {OperationId=Guid.NewGuid().ToString("N"),Action=BackupAction.Resume,TaskIds=new[]{first}});
                await service.WaitOperationAsync(resume);Assert.IsTrue((await service.GetSummaryAsync()).QueuePaused);
                Assert.AreEqual(BackupState.Queued,(await service.GetTaskAsync(first)).state);
            }
            finally{await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator LibraryRefreshIsPagedAndVersionsRemainVisibleAcrossScopes() => UniTask.ToCoroutine(async()=>
        {
            var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"library.sqlite"));
            var shallow=new ImageLibraryScope(ImageLibrarySourceKind.Directory,root);var deep=new ImageLibraryScope(ImageLibrarySourceKind.Directory,root,true);
            try
            {
                await library.RefreshAsync(shallow);await library.RefreshAsync(deep);var before=await library.GetPositionAsync(deep);
                var bytes=File.ReadAllBytes(imagePath);File.AppendAllText(imagePath,"version");
                library.RequestRefresh(shallow);await library.RefreshAsync(shallow);library.RequestRefresh(deep);await library.RefreshAsync(deep);
                var changes=await library.ReadChangesAsync(deep,before);Assert.IsTrue(changes.Items.Any(c=>c.Kind==ImageLibraryChangeKind.ContentChanged));
                var page=await library.QueryAsync(shallow,1);Assert.AreEqual(1,page.Items.Count);Assert.IsNotNull(page.Next);
                Assert.IsEmpty((await library.QueryAsync(shallow,1,page.Next)).Items);
                File.Delete(imagePath);library.RequestRefresh(shallow);await library.RefreshAsync(shallow);Assert.IsEmpty((await library.QueryAsync(shallow)).Items);
                var position=await library.GetPositionAsync(deep);await library.PruneChangesAsync(position.Sequence);
                Assert.IsTrue((await library.ReadChangesAsync(deep,before)).RequiresRefresh);
                Assert.GreaterOrEqual((await library.GetPositionAsync(deep)).Sequence,position.Sequence);
            }
            finally{await library.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator AutomaticBaselinePersistsWithoutAcceptingHistoricalVersions() => UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config());var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"index.sqlite"));
            try
            {
                var automatic=await AutomaticImageBackup.CreateAsync(service,library);
                await automatic.ConfigureAsync(new AutomaticBackupPolicy {enabled=true,source=root,sourceKind=BackupSourceKind.Directory,includeExisting=false,wifiOnly=false});
                await automatic.ScanOnceAsync();Assert.IsEmpty((await service.QueryTasksAsync()).Items);
                File.Copy(imagePath,Path.Combine(root,"new.png"));library.RequestRefresh(new ImageLibraryScope(ImageLibrarySourceKind.Directory,root));
                await automatic.ScanOnceAsync();Assert.AreEqual(1,(await service.QueryTasksAsync()).Items.Count);
                await automatic.ScanOnceAsync();Assert.AreEqual(1,(await service.QueryTasksAsync()).Items.Count);
            }
            finally{await library.ShutdownAsync();await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator ThumbnailCoalescingIndependentCancellationAndShutdownLeases() => UniTask.ToCoroutine(async()=>
        {
            int loads=0;var gate=new UniTaskCompletionSource();
            var cache=new ImageThumbnailCache(1024*1024,async(image,options,token)=>{loads++;await gate.Task;return new ImageTexture(new Texture2D(8,4));});
            var source=ImageReference.FromFile(imagePath);var cancellation=new CancellationTokenSource();
            var canceled=cache.AcquireAsync(source,cancellationToken:cancellation.Token);var kept=cache.AcquireAsync(source);
            cancellation.Cancel();await UniTask.Yield();gate.TrySetResult();
            Exception failure=null;try{using var value=await canceled;}catch(Exception error){failure=error;}Assert.IsInstanceOf<OperationCanceledException>(failure);
            var lease=await kept;var texture=lease.Texture;Assert.AreEqual(1,loads);
            using(var hit=await cache.AcquireAsync(source))Assert.AreSame(texture,hit.Texture);
            await cache.ShutdownAsync();Assert.IsTrue(texture!=null);lease.Dispose();Assert.IsTrue(texture==null);cancellation.Dispose();
            Assert.AreEqual(0,cache.Statistics.PendingKeys);Assert.AreEqual(0,cache.Statistics.Waiters);
        });
        [UnityTest] public IEnumerator ThumbnailAllCanceledKeyCanBeReacquiredBeforeOldLoadCompletes() => UniTask.ToCoroutine(async()=>
        {
            var gates=new System.Collections.Generic.List<UniTaskCompletionSource<ImageTexture>>();
            var cache=new ImageThumbnailCache(0,(image,options,token)=>{var gate=new UniTaskCompletionSource<ImageTexture>();gates.Add(gate);return gate.Task;});
            var source=ImageReference.FromFile(imagePath);using var cancel=new CancellationTokenSource();var first=cache.AcquireAsync(source,cancellationToken:cancel.Token);cancel.Cancel();await UniTask.Yield();
            var second=cache.AcquireAsync(source);Assert.AreEqual(2,gates.Count);
            var old=new Texture2D(8,4);gates[0].TrySetResult(new ImageTexture(old));var next=new Texture2D(8,4);gates[1].TrySetResult(new ImageTexture(next));
            try{using var ignored=await first;Assert.Fail("Expected cancellation");}catch(OperationCanceledException){}
            using(var lease=await second){Assert.AreSame(next,lease.Texture);Assert.IsTrue(old==null);}
            await cache.ShutdownAsync();Assert.IsTrue(next==null);
        });
        [UnityTest] public IEnumerator ThumbnailSourceLeaseAndAdmissionBudgetsAreBounded() => UniTask.ToCoroutine(async()=>
        {
            var selection=await GameGallery.ImportFilesAsync(new[]{imagePath});string path=selection.Items[0].Id;var ready=new UniTaskCompletionSource();
            var cache=new ImageThumbnailCache(0,async(image,options,token)=>{await ready.Task;Assert.IsTrue(File.Exists(path));return new ImageTexture(new Texture2D(8,4));});
            var pending=cache.AcquireAsync(selection.Items[0]);selection.Dispose();Assert.IsTrue(File.Exists(path));
            Assert.Throws<ObjectDisposedException>(()=>cache.AcquireAsync(selection.Items[0]));
            ready.TrySetResult();using(var lease=await pending){}await cache.ShutdownAsync();Assert.IsFalse(File.Exists(path));
            var admissions=new System.Collections.Generic.List<IDisposable>();var source=ImageReference.FromFile(imagePath);
            try
            {
                for(int i=0;i<128;i++)admissions.Add(ImageWorkBudget.Acquire(source,true,true));
                Assert.Throws<GalleryException>(()=>ImageWorkBudget.Acquire(source,true,true));
                admissions[0].Dispose();admissions[0]=ImageWorkBudget.Acquire(source,true,true);
            }
            finally{foreach(var item in admissions)item.Dispose();}
            Assert.AreEqual(0,ImageWorkBudget.Keys);Assert.AreEqual(0,ImageWorkBudget.Waiters);
        });
        [UnityTest] public IEnumerator LibraryShutdownDrainsAcceptedQueryAndRejectsNewWork() => UniTask.ToCoroutine(async()=>
        {
            var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"closing.sqlite"));
            var scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,root);
            await library.RefreshAsync(scope);
            var query=library.QueryAsync(scope);var closing=library.ShutdownAsync();
            var page=await query;Assert.AreEqual(1,page.Items.Count);await closing;
            Exception failure=null;try{await library.QueryAsync(scope);}catch(Exception error){failure=error;}
            Assert.IsInstanceOf<ObjectDisposedException>(failure);
        });
        [UnityTest] public IEnumerator MaintenancePassesExpiredHeadersAndPreservesOperationIdentity() => UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config());
            try
            {
                var ids=new System.Collections.Generic.List<string>();
                for(int i=0;i<35;i++)
                {
                    string id=Guid.NewGuid().ToString("N");ids.Add(id);
                    await service.SubmitOperationAsync(new BackupOperationCommand {OperationId=id,Action=BackupAction.Pause});
                    await service.WaitOperationAsync(id);
                }
                var maintenance=new BackupMaintenance(service);var policy=new BackupRetentionPolicy {HistoryAge=TimeSpan.Zero,KeepHistoryCount=0,TimeSlice=TimeSpan.FromSeconds(5)};
                await maintenance.RunAsync(policy);await maintenance.RunAsync(policy);
                foreach(string id in ids)Assert.IsTrue((await service.QueryOperationAsync(id)).DetailsExpired);
                await service.SubmitOperationAsync(new BackupOperationCommand {OperationId=ids[0],Action=BackupAction.Pause});
                Assert.IsTrue((await service.QueryOperationAsync(ids[0])).DetailsExpired);
            }
            finally{await service.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator HundredThumbnailWaitersShareOneLoadAndVersionInvalidationKeepsLeasesAlive() => UniTask.ToCoroutine(async()=>
        {
            int loads=0;var gate=new UniTaskCompletionSource();var source=ImageReference.FromFile(imagePath);
            var cache=new ImageThumbnailCache(1024*1024,async(image,options,token)=>{loads++;await gate.Task;return new ImageTexture(new Texture2D(8,4));});
            var cancellations=new CancellationTokenSource[99];var requests=new UniTask<ImageTexture>[99];
            try
            {
                for(int i=0;i<99;i++){cancellations[i]=new CancellationTokenSource();requests[i]=cache.AcquireAsync(source,cancellationToken:cancellations[i].Token);}
                var retained=cache.AcquireAsync(source);foreach(var cancel in cancellations)cancel.Cancel();await UniTask.Yield();gate.TrySetResult();
                foreach(var request in requests){try{using var ignored=await request;Assert.Fail("Canceled waiter completed");}catch(OperationCanceledException){}}
                using var first=await retained;var old=first.Texture;Assert.AreEqual(1,loads);
                cache.Invalidate(source);using(var next=await cache.AcquireAsync(source)){Assert.AreNotSame(old,next.Texture);Assert.IsTrue(old!=null);}
                Assert.AreEqual(2,loads);Assert.AreEqual(0,cache.Statistics.Waiters);
            }
            finally{foreach(var cancel in cancellations)cancel?.Dispose();await cache.ShutdownAsync();}
        });
        [UnityTest] public IEnumerator ThumbnailDeliveryFailureCompletesEveryWaiterAndReturnsAdmission() => UniTask.ToCoroutine(async()=>
        {
            var gate=new UniTaskCompletionSource<ImageTexture>();var cache=new ImageThumbnailCache(0,(image,options,token)=>gate.Task);
            var source=ImageReference.FromFile(imagePath);var first=cache.AcquireAsync(source);var second=cache.AcquireAsync(source);
            var disposed=new ImageTexture(new Texture2D(8,4));disposed.Dispose();gate.TrySetResult(disposed);
            foreach(var request in new[]{first,second})
            {
                Exception failure=null;try{using var ignored=await request;}catch(Exception error){failure=error;}
                Assert.IsInstanceOf<ObjectDisposedException>(failure);
            }
            await cache.ShutdownAsync();Assert.AreEqual(0,cache.Statistics.PendingKeys);Assert.AreEqual(0,cache.Statistics.Waiters);
        });
        static async UniTask ConfirmForDownload(ImageBackupService service,BackupTaskInfo task)
        {
            await service.Db(BackupRepository.Command.Claim,default,task.id,0,0,DateTime.UtcNow.Ticks,Guid.NewGuid().ToString("N"));
            await service.Db(BackupRepository.Command.Start,default,task.id,1L);
            await service.Db(BackupRepository.Command.Finish,default,task.id,1L,1,"verified","",DateTime.UtcNow.Ticks,task.size,task.sha256);
            await service.Db(BackupRepository.Command.Release,default,task.id,1L,true,true);
        }
        [UnityTest] public IEnumerator DownloadsBoundUnknownLengthAndVerifyBeforePublishing() => UniTask.ToCoroutine(async () =>
        {
            var handler=new ResponseHandler(); using var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,handler);
            await service.EnqueueAsync(new[]{ImageReference.FromFile(imagePath)});
            var task=(await service.QueryTasksAsync()).Items.Single(); await ConfirmForDownload(service,task);
            byte[] expected=File.ReadAllBytes(imagePath); string destination=Path.Combine(root,"restored.png");
            foreach(string mode in new[]{"oversize","short","corrupt","valid"})
            {
                byte[] body=mode=="oversize"?new byte[expected.Length+1024*1024]:mode=="short"?expected.Take(expected.Length-1).ToArray():(byte[])expected.Clone();
                if(mode=="corrupt") body[0]^=1;
                var stream=new CountingStream(body); handler.respond=()=>new System.Net.Http.HttpResponseMessage { Content=new StreamResponse(stream) };
                Exception observed=null; try { await service.DownloadBackupAndVerifyAsync("verified",destination); } catch(Exception error) { observed=error; }
                Assert.LessOrEqual(stream.read,expected.Length+1); Assert.IsEmpty(Directory.GetFiles(root,"*.part"));
                if(mode=="valid") { Assert.IsNull(observed); CollectionAssert.AreEqual(expected,File.ReadAllBytes(destination)); }
                else { Assert.IsInstanceOf<IOException>(observed); Assert.IsFalse(File.Exists(destination)); }
            }
            Exception exists=null; try { await service.DownloadBackupAndVerifyAsync("verified",destination); } catch(Exception error) { exists=error; }
            Assert.IsInstanceOf<IOException>(exists); Assert.AreEqual(4,handler.requests); CollectionAssert.AreEqual(expected,File.ReadAllBytes(destination));
        });

        [UnityTest] public IEnumerator StalledDownloadTimesOutAndRemovesTemporaryFile() => UniTask.ToCoroutine(async () =>
        {
            var config=Config(); config.RequestTimeout=TimeSpan.FromMilliseconds(250);
            var handler=new ResponseHandler(); using var service=await ImageBackupService.CreateAsync(config,NativeBackup.Call,false,handler);
            await service.EnqueueAsync(new[]{ImageReference.FromFile(imagePath)});
            var task=(await service.QueryTasksAsync()).Items.Single(); await ConfirmForDownload(service,task);
            foreach(bool cancelByCaller in new[]{false,true})
            {
                var stream=new StalledStream(); handler.respond=()=>new System.Net.Http.HttpResponseMessage { Content=new StreamResponse(stream) };
                string destination=Path.Combine(root,"restored.png"); using var cancellation=new CancellationTokenSource();
                var pending=service.DownloadBackupAndVerifyAsync("verified",destination,cancellation.Token);
                await UniTask.WaitUntil(()=>stream.reading || stream.closed);
                if(cancelByCaller) cancellation.Cancel();
                Exception observed=null; try { await pending; } catch(Exception error) { observed=error; }
                if(cancelByCaller) Assert.IsInstanceOf<OperationCanceledException>(observed); else Assert.IsInstanceOf<TimeoutException>(observed);
                Assert.IsTrue(stream.closed); Assert.IsFalse(File.Exists(destination)); Assert.IsEmpty(Directory.GetFiles(root,"*.part"));
            }
        });

        sealed class CapabilityChangeHandler : System.Net.Http.HttpMessageHandler
        {
            internal int requests;
            protected override async System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            {
                requests++;
                var capability = new ServerCapabilities { protocolVersion = 1, account = "integration-user", chunkBytes = 1024, maxFileBytes = 999999, backgroundUpload = true };
                string response;
                if (request.Method == System.Net.Http.HttpMethod.Get) response = JsonUtility.ToJson(capability);
                else
                {
                    var upload = JsonUtility.FromJson<UploadRequest>(await request.Content.ReadAsStringAsync());
                    capability.account = "different-account";
                    response = JsonUtility.ToJson(new UploadResponse { uploadId=upload.key, sha256=upload.sha256, size=upload.size, capabilities=capability });
                }
                return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent(response) };
            }
        }
        [UnityTest] public IEnumerator InvalidCapabilitiesInUploadResponseStopDependentQueue() => UniTask.ToCoroutine(async () =>
        {
            var handler=new CapabilityChangeHandler();using var service=await ImageBackupService.CreateAsync(Config(),NativeBackup.Call,false,handler);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath), ImageReference.FromFile(imagePath) });
            Exception observed = null; try { await service.ProcessAsync(); } catch (Exception error) { observed = error; }
            Assert.IsInstanceOf<InvalidOperationException>(observed); Assert.AreEqual(2, handler.requests);
            Assert.AreEqual(1, (await service.QueryTasksAsync()).Items.Count(x => x.state == BackupState.NeedsAttention));
            Assert.AreEqual(1, (await service.QueryTasksAsync()).Items.Count(x => x.state == BackupState.Queued));
        });

        [UnityTest, Category("MediaIntegration"), Explicit("Requires integration_server.py")]
        public IEnumerator CapabilitiesAreReusedAcrossPassesAndExplicitlyRefreshable() => UniTask.ToCoroutine(async () =>
        {
            int requests = 0; var config = Config(); config.AccessToken = () => { requests++; return "uiframe-integration-test-token"; };
            using var service = await ImageBackupService.CreateAsync(config);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) }); await service.ProcessAsync();
            Assert.AreEqual(4, requests); // capabilities, create, one chunk, commit
            string next = Path.Combine(root, "next.png"); File.Copy(imagePath, next);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(next) }); await service.ProcessAsync();
            Assert.AreEqual(7, requests); await service.ProcessAsync(); Assert.AreEqual(7, requests);
            await service.RefreshServerCapabilitiesAsync(); Assert.AreEqual(8, requests);
            Assert.IsTrue((await service.QueryTasksAsync()).Items.All(x => x.state == BackupState.Completed));
        });

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool FailingPolicy(Exception original) { throw original; }
        [UnityTest, Category("MediaIntegration"), Explicit("Requires integration_server.py")]
        public IEnumerator PolicyExceptionStopsPassAndPreservesOriginalWithoutRetry() => UniTask.ToCoroutine(async () =>
        {
            var config = Config(); config.EnableTransientRetries = true;
            using var service = await ImageBackupService.CreateAsync(config);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath), ImageReference.FromFile(imagePath) });
            var original = new System.Net.Http.HttpRequestException("policy failure"); int calls = 0; Exception observed = null;
            try { await service.ProcessAsync(canTransfer: () => ++calls == 2 ? FailingPolicy(original) : true); } catch (Exception error) { observed = error; }
            Assert.AreSame(original, observed); StringAssert.Contains(nameof(FailingPolicy), observed.StackTrace);
            Assert.AreEqual(2, calls); Assert.AreEqual(2, (await service.QueryTasksAsync()).Items.Count(x => x.state == BackupState.Queued)); Assert.IsTrue((await service.QueryTasksAsync()).Items.All(x => x.retries == 0));
        });

        [UnityTest, Category("MediaIntegration"), Explicit("Requires integration_server.py")]
        public IEnumerator CredentialCallbackExceptionStopsPassAndPreservesOriginal() => UniTask.ToCoroutine(async () =>
        {
            var config = Config(); int calls = 0; var original = new InvalidOperationException("credential callback failure");
            config.AccessToken = () => { if (++calls == 2) throw original; return "uiframe-integration-test-token"; };
            using var service = await ImageBackupService.CreateAsync(config);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath), ImageReference.FromFile(imagePath) });
            Exception observed = null; try { await service.ProcessAsync(); } catch (Exception error) { observed = error; }
            Assert.AreSame(original, observed); Assert.AreEqual(2, calls);
            Assert.AreEqual(1, (await service.QueryTasksAsync()).Items.Count(x => x.state == BackupState.NeedsAttention)); Assert.AreEqual(1, (await service.QueryTasksAsync()).Items.Count(x => x.state == BackupState.Queued));
        });

        [UnityTest,Category("MediaIntegration"),Explicit("Requires integration_server.py")]
        public IEnumerator ReceiptDownloadSurvivesHistoryCleanupAndReopen() => UniTask.ToCoroutine(async()=>
        {
            var service=await ImageBackupService.CreateAsync(Config());
            try
            {
                await service.EnqueueAsync(new[]{ImageReference.FromFile(imagePath)});await service.ProcessAsync();
                var record=(await service.QueryBackupsAsync()).Items.Single();
                await new BackupMaintenance(service).RunAsync(new BackupRetentionPolicy {HistoryAge=TimeSpan.Zero,KeepHistoryCount=0,TimeSlice=TimeSpan.FromSeconds(5)});
                Assert.IsEmpty((await service.QueryTasksAsync()).Items);
                string target=Path.Combine(root,"restored.png");await service.DownloadBackupAndVerifyAsync(record.BackupId,target);
                CollectionAssert.AreEqual(File.ReadAllBytes(imagePath),File.ReadAllBytes(target));
                await service.EnqueueAsync(new[]{ImageReference.FromFile(imagePath)});await service.ProcessAsync();Assert.AreEqual(1,(await service.QueryBackupsAsync()).Items.Count);
            }
            finally{await service.ShutdownAsync();}
        });
    }
}
