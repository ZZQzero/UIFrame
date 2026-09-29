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
            public NativeBackupStatus Call(NativeBackupRequest request)
            {
                if (request.op == "submit") { submitted++; onSubmit?.Invoke(request); }
                if (request.op == "pause") { pauses++; status.state = BackupState.Paused; }
                if (request.op == "cancel") { cancels++; status.state = BackupState.Canceled; }
                if (request.op == "forget") { Assert.IsTrue(status.released); forgotten++; status = new NativeBackupStatus { released = true }; }
                return status;
            }
        }
        void MarkNativeOwned(BackupTaskInfo task, BackupState state = BackupState.Uploading)
        {
            task.nativeOwned = true; task.state = state;
            File.WriteAllText(Path.Combine(root, "backup", "batches", task.batchId, task.id + ".json"), JsonUtility.ToJson(task));
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
                await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) }); MarkNativeOwned(service.GetTasks().Single());
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
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) }); var task = service.GetTasks().Single(); MarkNativeOwned(task);
            service.Cancel(task.id); Assert.AreEqual(BackupState.Canceled, service.GetTasks().Single().state);
            Assert.AreEqual(1, Payloads().Length); Assert.AreEqual(0, native.forgotten);
            native.status.released = true; service.GetTasks(); Assert.AreEqual(0, Payloads().Length); Assert.AreEqual(1, native.forgotten);
        });

        [UnityTest] public IEnumerator PauseWaitsForNativeReleaseBeforeResume() => UniTask.ToCoroutine(async () =>
        {
            var native = new NativeFixture(); using var service = new ImageBackupService(Config(), native.Call, true);
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) }); MarkNativeOwned(service.GetTasks().Single());
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
            await service.EnqueueAsync(new[] { ImageReference.FromFile(imagePath) }); MarkNativeOwned(service.GetTasks().Single(), BackupState.Queued);
            await service.PauseAsync(); Assert.AreEqual(BackupState.Paused, service.GetTasks().Single().state);
            service.Resume(); MarkNativeOwned(service.GetTasks().Single(), BackupState.Queued);
            service.Cancel(service.GetTasks().Single().id); Assert.AreEqual(BackupState.Canceled, service.GetTasks().Single().state);
            Assert.AreEqual(0, Payloads().Length);
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
