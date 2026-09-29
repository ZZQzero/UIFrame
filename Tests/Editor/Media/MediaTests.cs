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

        [UnityTest] public IEnumerator AsyncOpenPreservesQueueAndCanceledOpenReleasesOwnership() => UniTask.ToCoroutine(async () =>
        {
            using (var service = await ImageBackupService.CreateAsync(Config()))
                await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) });
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            Exception observed = null;
            try { using var unused = await ImageBackupService.CreateAsync(Config(), canceled.Token); }
            catch (Exception error) { observed = error; }
            Assert.IsInstanceOf<OperationCanceledException>(observed);
            using var duringOpen = new CancellationTokenSource();
            var pending = ImageBackupService.CreateAsync(Config(), duringOpen.Token); duringOpen.Cancel(); observed = null;
            try { using var unused = await pending; } catch (Exception error) { observed = error; }
            Assert.IsInstanceOf<OperationCanceledException>(observed);
            using var reopened = await ImageBackupService.CreateAsync(Config());
            Assert.AreEqual(1, reopened.GetTasks().Count);
        });

        [UnityTest] public IEnumerator EmptyQueueDoesNotRequestCredentialsOrServer() => UniTask.ToCoroutine(async () =>
        {
            var config = Config(); config.AccessToken = () => throw new Exception("No HTTP request expected");
            using var service = new ImageBackupService(config);
            await service.ProcessAsync();
        });

        [UnityTest] public IEnumerator CachedScanReceiptsDoNotReopenFiles() => UniTask.ToCoroutine(async () =>
        {
            using var service = new ImageBackupService(Config()); var automatic = new AutomaticImageBackup(service);
            automatic.Configure(new AutomaticBackupPolicy { enabled = true, sourceKind = BackupSourceKind.Directory, source = root, includeExisting = true, wifiOnly = false });
            await automatic.ScanOnceAsync();
            string receipt = Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Single(x => x.Contains(".json.entries"));
            using (var exclusive = new FileStream(receipt, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.AreEqual(0, automatic.GetPreparationFailures().Count);
                Assert.AreEqual(0, (await automatic.GetPreparationFailuresAsync()).Count);
                await automatic.ScanOnceAsync();
            }
            Assert.AreEqual(1, service.GetTasks().Count);
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
            using var service = new ImageBackupService(Config());
            var handler = new CapabilityChangeHandler();
            var field = typeof(ImageBackupService).GetField("client", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            ((System.Net.Http.HttpClient)field.GetValue(service)).Dispose(); field.SetValue(service, new System.Net.Http.HttpClient(handler));
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath), ImageReference.FromFile(imagePath) });
            Exception observed = null; try { await service.ProcessAsync(); } catch (Exception error) { observed = error; }
            Assert.IsInstanceOf<InvalidOperationException>(observed); Assert.AreEqual(2, handler.requests);
            Assert.AreEqual(1, service.GetTasks().Count(x => x.state == BackupState.Failed));
            Assert.AreEqual(1, service.GetTasks().Count(x => x.state == BackupState.Queued));
        });

        [UnityTest, Category("MediaIntegration"), Explicit("Requires integration_server.py")]
        public IEnumerator CapabilitiesAreReusedAcrossPassesAndExplicitlyRefreshable() => UniTask.ToCoroutine(async () =>
        {
            int requests = 0; var config = Config(); config.AccessToken = () => { requests++; return "uiframe-integration-test-token"; };
            using var service = new ImageBackupService(config);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) }); await service.ProcessAsync();
            Assert.AreEqual(4, requests); // capabilities, create, one chunk, commit
            string next = Path.Combine(root, "next.png"); File.Copy(imagePath, next);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(next) }); await service.ProcessAsync();
            Assert.AreEqual(7, requests); await service.ProcessAsync(); Assert.AreEqual(7, requests);
            await service.RefreshServerCapabilitiesAsync(); Assert.AreEqual(8, requests);
            Assert.IsTrue(service.GetTasks().All(x => x.state == BackupState.Completed));
        });

        [UnityTest, Category("MediaIntegration"), Explicit("Requires integration_server.py")]
        public IEnumerator AutomaticLoopDrainsExistingReceiptsBeforePreparingNewImages() => UniTask.ToCoroutine(async () =>
        {
            var config = Config(); config.DiskBudgetBytes = new FileInfo(imagePath).Length;
            using var service = new ImageBackupService(config); var automatic = new AutomaticImageBackup(service);
            automatic.Configure(new AutomaticBackupPolicy { enabled = true, sourceKind = BackupSourceKind.Directory, source = root, includeExisting = true, wifiOnly = false });
            await automatic.ScanOnceAsync(); File.Copy(imagePath, Path.Combine(root, "new.png"));
            using var cancel = new CancellationTokenSource();
            var loop = automatic.RunAsync(cancel.Token).Preserve();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await UniTask.WaitUntil(() => service.GetTasks().Count == 2 && service.GetTasks().All(x => x.state == BackupState.Completed), cancellationToken: timeout.Token);
            }
            finally { cancel.Cancel(); try { await loop; } catch (OperationCanceledException) { } }
        });

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

        [UnityTest] public IEnumerator BackupQueuePersistsAndRemainsAccountBound() => UniTask.ToCoroutine(async () =>
        {
            string id;
            using(var service=new ImageBackupService(Config()))
            {
                using var selection=await GameGallery.ImportFilesAsync(new[]{imagePath});
                id=(await service.EnqueueAsync(selection.Items))[0];
            }
            using(var reopened=new ImageBackupService(Config())) { Assert.AreEqual(id,reopened.GetTasks()[0].id); Assert.AreEqual(BackupState.Queued,reopened.GetTasks()[0].state); }
            Assert.Throws<InvalidOperationException>(()=>new ImageBackupService(Config("different-account")));
        });

        [UnityTest] public IEnumerator BackupBudgetFailureDoesNotAcceptPartialBatch() => UniTask.ToCoroutine(async () =>
        {
            var configuration=Config(); configuration.DiskBudgetBytes=1;
            using var service=new ImageBackupService(configuration); Exception failure=null;
            try { await service.EnqueueAsync(new[]{ImageReference.FromFile(imagePath)}); } catch(Exception error) { failure=error; }
            Assert.IsInstanceOf<IOException>(failure); Assert.AreEqual(0,service.GetTasks().Count);
            Assert.AreEqual(0,Directory.GetFiles(Path.Combine(root,"backup","staging"),"*",SearchOption.AllDirectories).Length);
        });

        [Test] public void NonLocalPlainHttpIsRejectedBeforeCreatingStore()
        {
            var configuration=Config(); configuration.ServerUrl="http://example.com";
            Assert.Throws<ArgumentException>(()=>new ImageBackupService(configuration)); Assert.IsFalse(Directory.Exists(configuration.StorageDirectory));
        }

        [UnityTest] public IEnumerator AutomaticBaselineDoesNotEnqueueHistoricalImages() => UniTask.ToCoroutine(async () =>
        {
            using var service=new ImageBackupService(Config()); var automatic=new AutomaticImageBackup(service);
            automatic.Configure(new AutomaticBackupPolicy{enabled=true,sourceKind=BackupSourceKind.Directory,source=root,includeExisting=false,wifiOnly=false});
            await automatic.ScanOnceAsync(); Assert.AreEqual(0,service.GetTasks().Count);
            File.Copy(imagePath,Path.Combine(root,"new.png")); await automatic.ScanOnceAsync(); Assert.AreEqual(1,service.GetTasks().Count);
            await automatic.ScanOnceAsync(); Assert.AreEqual(1,service.GetTasks().Count);
        });

        [Test] public void WifiOnlyAutomaticBackupRequiresVerifiedNetworkCheck()
        {
            using var service=new ImageBackupService(Config()); var automatic=new AutomaticImageBackup(service);
            Assert.Throws<InvalidOperationException>(()=>automatic.Configure(new AutomaticBackupPolicy{enabled=true,sourceKind=BackupSourceKind.Directory,source=root}));
        }

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

        [UnityTest] public IEnumerator PausingQueueDoesNotDeleteAcceptedFiles() => UniTask.ToCoroutine(async () =>
        {
            using var service=new ImageBackupService(Config());
            var id=(await service.EnqueueAsync(new[]{ImageReference.FromFile(imagePath)}))[0];
            await service.PauseAsync(); Assert.AreEqual(BackupState.Paused,service.GetTasks().Single().state);
            service.Resume(); Assert.AreEqual(BackupState.Queued,service.GetTasks().Single().state);
            service.Cancel(id); Assert.AreEqual(BackupState.Canceled,service.GetTasks().Single().state);
            Assert.IsTrue(File.Exists(imagePath)); Assert.AreEqual(0,Directory.GetFiles(Path.Combine(root,"backup"),"*.payload",SearchOption.AllDirectories).Length);
        });

        sealed class NativeFixture
        {
            public NativeBackupStatus status = new NativeBackupStatus { exists = true, released = false, state = BackupState.Uploading };
            public int pauses, cancels, forgotten, submitted;
            public Action<NativeBackupRequest> onSubmit;
            public Exception forgetFailure;
            public int forgetAttempts;
            public NativeBackupStatus Call(NativeBackupRequest request)
            {
                if (request.op == "submit") { submitted++; onSubmit?.Invoke(request); }
                if (request.op == "pause") { pauses++; status.state = BackupState.Paused; }
                if (request.op == "cancel") { cancels++; status.state = BackupState.Canceled; }
                if (request.op == "forget") { forgetAttempts++; if (forgetFailure != null) throw forgetFailure; Assert.IsTrue(status.released); forgotten++; status = new NativeBackupStatus { released = true }; }
                return status;
            }
        }
        void MarkNativeOwned(ImageBackupService service, BackupTaskInfo task, BackupState state = BackupState.Uploading)
        {
            task.nativeOwned = true; task.state = state;
            typeof(ImageBackupService).GetMethod("Save", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(service, new object[] { task });
        }
        string[] Payloads() => Directory.GetFiles(Path.Combine(root, "backup"), "*.payload", SearchOption.AllDirectories);

        [Test] public void UnsupportedNativeModeFailsBeforeCreatingStorage()
        {
            var config = Config(); config.EnableNativeBackgroundTransfer = true;
            Assert.Throws<PlatformNotSupportedException>(() => new ImageBackupService(config));
            Assert.IsFalse(Directory.Exists(config.StorageDirectory));
        }

        [UnityTest] public IEnumerator ReopeningNativeQueueDoesNotStealActivePayload() => UniTask.ToCoroutine(async () =>
        {
            var native = new NativeFixture();
            using (var service = new ImageBackupService(Config(), native.Call, true))
            {
                await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) }); MarkNativeOwned(service, service.GetTasks().Single());
                await service.ShutdownAsync(); Assert.AreEqual(0, native.cancels); Assert.AreEqual(0, native.pauses);
            }
            using var reopened = new ImageBackupService(Config(), native.Call, true);
            Assert.AreEqual(BackupState.Uploading, reopened.GetTasks().Single().state); Assert.AreEqual(1, Payloads().Length);
            native.status.state = BackupState.Completed; native.status.backupId = "verified-server-id";
            Assert.AreEqual(BackupState.Completed, reopened.GetTasks().Single().state); Assert.AreEqual(1, Payloads().Length);
            native.status.released = true;
            Assert.AreEqual(BackupState.Completed, reopened.GetTasks().Single().state); Assert.AreEqual(0, Payloads().Length);
            Assert.AreEqual(1, native.forgotten);
        });

        [UnityTest] public IEnumerator CancelKeepsNativeFileUntilCallbackReleasesIt() => UniTask.ToCoroutine(async () =>
        {
            var native = new NativeFixture(); using var service = new ImageBackupService(Config(), native.Call, true);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) }); var task = service.GetTasks().Single(); MarkNativeOwned(service, task);
            service.Cancel(task.id); Assert.AreEqual(BackupState.Canceled, service.GetTasks().Single().state);
            Assert.AreEqual(1, Payloads().Length); Assert.AreEqual(0, native.forgotten);
            native.status.released = true; service.GetTasks(); Assert.AreEqual(0, Payloads().Length); Assert.AreEqual(1, native.forgotten);
        });

        [UnityTest] public IEnumerator PauseWaitsForNativeReleaseBeforeResume() => UniTask.ToCoroutine(async () =>
        {
            var native = new NativeFixture(); using var service = new ImageBackupService(Config(), native.Call, true);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) }); MarkNativeOwned(service, service.GetTasks().Single());
            var pause = service.PauseAsync(); Assert.AreEqual(1, native.pauses);
            Exception conflict = null;
            try { await service.ProcessAsync(); } catch (Exception error) { conflict = error; }
            Assert.IsInstanceOf<InvalidOperationException>(conflict);
            Assert.Throws<InvalidOperationException>(() => service.Resume()); Assert.AreEqual(1, Payloads().Length);
            native.status.released = true; await pause; service.Resume();
            Assert.AreEqual(BackupState.Queued, service.GetTasks().Single().state); Assert.AreEqual(1, Payloads().Length);
            Assert.IsFalse(service.GetTasks().Single().nativeOwned);
        });

        [UnityTest] public IEnumerator HandoffIntentWithoutNativeTaskCanBePausedAndCanceled() => UniTask.ToCoroutine(async () =>
        {
            var native = new NativeFixture { status = new NativeBackupStatus { released = true } };
            using var service = new ImageBackupService(Config(), native.Call, true);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) }); MarkNativeOwned(service, service.GetTasks().Single(), BackupState.Queued);
            await service.PauseAsync(); Assert.AreEqual(BackupState.Paused, service.GetTasks().Single().state);
            service.Resume(); MarkNativeOwned(service, service.GetTasks().Single(), BackupState.Queued);
            service.Cancel(service.GetTasks().Single().id); Assert.AreEqual(BackupState.Canceled, service.GetTasks().Single().state);
            Assert.AreEqual(0, Payloads().Length);
        });

        [UnityTest] public IEnumerator AutomaticPreparationFailureIsIsolatedPersistentAndExplicitlyRetryable() => UniTask.ToCoroutine(async () =>
        {
            string source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            string large = Path.Combine(source, "a-large.png"), small = Path.Combine(source, "b-small.png");
            File.Copy(imagePath, small); File.Copy(imagePath, large);
            using (var file = new FileStream(large, FileMode.Append)) file.Write(new byte[1024], 0, 1024);
            var config = Config(); config.MaxFileBytes = new FileInfo(small).Length;
            using (var service = new ImageBackupService(config))
            {
                var automatic = new AutomaticImageBackup(service);
                automatic.Configure(new AutomaticBackupPolicy { enabled = true, sourceKind = BackupSourceKind.Directory, source = source, includeExisting = true, wifiOnly = false });
                await automatic.ScanOnceAsync();
                Assert.AreEqual(1, service.GetTasks().Count); Assert.AreEqual("b-small.png", service.GetTasks()[0].name);
                Assert.AreEqual(1, automatic.GetPreparationFailures().Count); Assert.IsNotNull(automatic.LastError);
                await automatic.ScanOnceAsync(); Assert.AreEqual(1, service.GetTasks().Count);
            }
            config.MaxFileBytes = 4096;
            using (var service = new ImageBackupService(config))
            {
                var automatic = new AutomaticImageBackup(service);
                await automatic.ScanOnceAsync(); Assert.AreEqual(1, service.GetTasks().Count); // no implicit retry after reopening
                automatic.RetryPreparationFailures(); await automatic.ScanOnceAsync();
                Assert.AreEqual(2, service.GetTasks().Count); Assert.AreEqual(0, automatic.GetPreparationFailures().Count); Assert.IsNull(automatic.LastError);
            }
        });

        [UnityTest] public IEnumerator SharedBudgetFailureStopsScanWithoutRecordingSourceFailure() => UniTask.ToCoroutine(async () =>
        {
            var config = Config(); config.DiskBudgetBytes = 1;
            using var service = new ImageBackupService(config); var automatic = new AutomaticImageBackup(service);
            automatic.Configure(new AutomaticBackupPolicy { enabled = true, sourceKind = BackupSourceKind.Directory, source = root, includeExisting = true, wifiOnly = false });
            Exception observed = null;
            try { await automatic.ScanOnceAsync(); } catch (Exception error) { observed = error; }
            Assert.IsInstanceOf<BackupBudgetExceededException>(observed);
            Assert.AreEqual(0, automatic.GetPreparationFailures().Count); Assert.AreEqual(0, service.GetTasks().Count);
        });

        [UnityTest] public IEnumerator ChangedFileIsBoundedDuringCopyAndFailedBatchReleasesBudget() => UniTask.ToCoroutine(async () =>
        {
            var config = Config(); config.MaxFileBytes = new FileInfo(imagePath).Length; config.DiskBudgetBytes = config.MaxFileBytes * 2;
            string growing = Path.Combine(root, "growing.png"); File.Copy(imagePath, growing);
            var reference = ImageReference.FromFile(growing);
            using (var file = new FileStream(growing, FileMode.Append)) file.Write(new byte[1024], 0, 1024);
            using var service = new ImageBackupService(config); Exception observed = null;
            try { await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath), reference }); } catch (Exception error) { observed = error; }
            Assert.IsInstanceOf<IOException>(observed); Assert.IsNotInstanceOf<BackupBudgetExceededException>(observed);
            Assert.AreEqual(0, service.GetTasks().Count); Assert.AreEqual(0, Payloads().Length);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath), ImageReference.FromFile(imagePath) });
            Assert.AreEqual(2, service.GetTasks().Count);
        });

        [UnityTest] public IEnumerator NativeCleanupFailureKeepsOwnershipUntilExplicitCleanupRetry() => UniTask.ToCoroutine(async () =>
        {
            var original = new IOException("native forget failed");
            var native = new NativeFixture { forgetFailure = original };
            string id;
            using (var service = new ImageBackupService(Config(), native.Call, true))
            {
                id = (await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) }))[0];
                MarkNativeOwned(service, service.GetTasks().Single()); native.status.state = BackupState.Completed; native.status.released = true;
                Assert.AreSame(original, Assert.Throws<IOException>(() => service.GetTasks()));
                var task = service.GetTasks().Single();
                Assert.IsTrue(task.cleanupPending); Assert.IsTrue(task.nativeOwned); StringAssert.Contains(original.Message, task.cleanupError);
                Assert.AreEqual(1, native.forgetAttempts); Assert.AreEqual(1, Payloads().Length);
            }
            using var reopened = new ImageBackupService(Config(), native.Call, true);
            Assert.IsTrue(reopened.GetTasks().Single().cleanupPending); Assert.AreEqual(1, native.forgetAttempts);
            native.forgetFailure = null; reopened.RetryCleanup(id);
            Assert.IsFalse(reopened.GetTasks().Single().cleanupPending); Assert.IsFalse(reopened.GetTasks().Single().nativeOwned);
            Assert.AreEqual(BackupState.Completed, reopened.GetTasks().Single().state); Assert.AreEqual(0, Payloads().Length);
        });

        [UnityTest] public IEnumerator NativeCleanupRecoversInterruptionAfterForget() => UniTask.ToCoroutine(async () =>
        {
            var native = new NativeFixture { status = new NativeBackupStatus { released = true } };
            using (var service = new ImageBackupService(Config(), native.Call, true))
            {
                await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) });
                var task = service.GetTasks().Single(); task.cleanupPending = true;
                MarkNativeOwned(service, task, BackupState.Completed);
            }
            using var reopened = new ImageBackupService(Config(), native.Call, true);
            Assert.AreEqual(BackupState.Completed, reopened.GetTasks().Single().state);
            Assert.IsFalse(reopened.GetTasks().Single().cleanupPending); Assert.AreEqual(0, Payloads().Length);
        });

        [UnityTest] public IEnumerator UnchangedNativeQueriesDoNotRewriteStoreAndSnapshotsCannotMutateQueue() => UniTask.ToCoroutine(async () =>
        {
            var native = new NativeFixture(); using var service = new ImageBackupService(Config(), native.Call, true);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) });
            var task = service.GetTasks().Single(); MarkNativeOwned(service, task);
            string path = Path.Combine(root, "backup", "batches", task.batchId, task.id + ".json");
            var timestamp = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc); File.SetLastWriteTimeUtc(path, timestamp);
            for (int i = 0; i < 3; i++) service.GetTasks().Single().state = BackupState.Canceled;
            Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(path)); Assert.AreEqual(BackupState.Uploading, service.GetTasks().Single().state);
            native.status.confirmedBytes = 1; service.GetTasks(); Assert.AreNotEqual(timestamp, File.GetLastWriteTimeUtc(path));
        });

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool FailingPolicy(Exception original) { throw original; }
        [UnityTest, Category("MediaIntegration"), Explicit("Requires integration_server.py")]
        public IEnumerator PolicyExceptionStopsPassAndPreservesOriginalWithoutRetry() => UniTask.ToCoroutine(async () =>
        {
            var config = Config(); config.EnableTransientRetries = true;
            using var service = new ImageBackupService(config);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath), ImageReference.FromFile(imagePath) });
            var original = new System.Net.Http.HttpRequestException("policy failure"); int calls = 0; Exception observed = null;
            try { await service.ProcessAsync(canTransfer: () => ++calls == 2 ? FailingPolicy(original) : true); } catch (Exception error) { observed = error; }
            Assert.AreSame(original, observed); StringAssert.Contains(nameof(FailingPolicy), observed.StackTrace);
            Assert.AreEqual(2, calls); Assert.AreEqual(1, service.GetTasks().Count(x => x.state == BackupState.Failed));
            Assert.AreEqual(1, service.GetTasks().Count(x => x.state == BackupState.Queued)); Assert.IsTrue(service.GetTasks().All(x => x.retries == 0));
            Assert.AreEqual(2, Payloads().Length);
        });

        [UnityTest, Category("MediaIntegration"), Explicit("Requires integration_server.py")]
        public IEnumerator CredentialCallbackExceptionStopsPassAndPreservesOriginal() => UniTask.ToCoroutine(async () =>
        {
            var config = Config(); int calls = 0; var original = new InvalidOperationException("credential callback failure");
            config.AccessToken = () => { if (++calls == 2) throw original; return "uiframe-integration-test-token"; };
            using var service = new ImageBackupService(config);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath), ImageReference.FromFile(imagePath) });
            Exception observed = null; try { await service.ProcessAsync(); } catch (Exception error) { observed = error; }
            Assert.AreSame(original, observed); Assert.AreEqual(2, calls);
            Assert.AreEqual(1, service.GetTasks().Count(x => x.state == BackupState.Failed)); Assert.AreEqual(1, service.GetTasks().Count(x => x.state == BackupState.Queued));
        });

        [UnityTest, Category("MediaIntegration"), Explicit("Requires integration_server.py")]
        public IEnumerator QueueWriteFailureStopsOtherUploadsWithoutConvertingItToTaskFailure() => UniTask.ToCoroutine(async () =>
        {
            using var service = new ImageBackupService(Config());
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath), ImageReference.FromFile(imagePath) });
            var task = service.GetTasks()[0];
            string blocked = Path.Combine(root, "backup", "batches", task.batchId, task.id + ".json.new");
            Directory.CreateDirectory(blocked); Exception observed = null;
            try { await service.ProcessAsync(); } catch (Exception error) { observed = error; }
            Assert.IsNotNull(observed); Assert.IsTrue(observed is IOException || observed is UnauthorizedAccessException);
            Assert.IsTrue(service.GetTasks().All(x => x.state == BackupState.Queued)); Assert.AreEqual(2, Payloads().Length);
        });

        [UnityTest, Category("MediaIntegration"), Explicit("Requires integration_server.py")]
        public IEnumerator NativeHandoffIsDurableAndDoesNotSubmitTwice() => UniTask.ToCoroutine(async () =>
        {
            var native = new NativeFixture(); var config = Config(); config.EnableNativeBackgroundTransfer = true;
            using var service = new ImageBackupService(config, native.Call, true);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) }); var task = service.GetTasks().Single();
            native.onSubmit = request =>
            {
                string json = File.ReadAllText(Path.Combine(root, "backup", "batches", task.batchId, task.id + ".json"));
                Assert.IsTrue(JsonUtility.FromJson<BackupTaskInfo>(json).nativeOwned);
                Assert.IsTrue(File.Exists(request.payload)); StringAssert.DoesNotContain(config.AccessToken(), json);
                Assert.IsTrue(request.wifiOnly);
            };
            await service.ProcessAsync(); Assert.AreEqual(1, native.submitted);
            await service.ProcessAsync(); Assert.AreEqual(1, native.submitted); Assert.AreEqual(1, Payloads().Length);
            native.status.state = BackupState.NeedsAttention; native.status.error = "HTTP 401"; native.status.released = true;
            await service.ProcessAsync(); Assert.AreEqual(1, native.submitted); // failed uploads never silently retry
            service.Retry(task.id); native.status = new NativeBackupStatus { exists = true, released = false, state = BackupState.Uploading };
            await service.ProcessAsync(); Assert.AreEqual(2, native.submitted);
        });

        [UnityTest, Category("MediaIntegration"), Explicit("Start Tools~/BackupServer/integration_server.py, then run this test explicitly.")]
        public IEnumerator RealServerRoundTripAndIdempotentResubmission() => UniTask.ToCoroutine(async () =>
        {
            using var service=new ImageBackupService(Config());
            string id=(await service.EnqueueAsync(new[]{ImageReference.FromFile(imagePath)}))[0];
            await service.ProcessAsync(); var task=service.GetTasks().Single();
            Assert.AreEqual(BackupState.Completed,task.state,task.error);
            string restored=Path.Combine(root,"restored.png"); await service.DownloadAndVerifyAsync(id,restored);
            CollectionAssert.AreEqual(File.ReadAllBytes(imagePath),File.ReadAllBytes(restored));
            await service.EnqueueAsync(new[]{ImageReference.FromFile(imagePath)}); await service.ProcessAsync();
            Assert.IsTrue(service.GetTasks().All(x=>x.state==BackupState.Completed));
            Assert.AreEqual(1,service.GetTasks().Select(x=>x.backupId).Distinct().Count());
        });
    }
}
