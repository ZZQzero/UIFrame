using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YooAsset;

#if UNITY_EDITOR
using UnityEditor;
using YooAsset.Editor;
#endif

namespace UIFrame.Regression
{
#if UNITY_EDITOR
    public sealed class UIImageLoaderPackageSetup : IPrebuildSetup, IPostBuildCleanup
    {
        internal const string PackageNameKey = "UIFrame.ImageTests.PackageName";
        internal const string PackageRootKey = "UIFrame.ImageTests.PackageRoot";
        const string SettingExistedKey = "UIFrame.ImageTests.SettingExisted";
        const string AssetPath = "Assets/UIFrameTest/UIImageLoaderTestSprite.png";

        internal static string PackageName => EditorPrefs.GetString(PackageNameKey);

        void IPrebuildSetup.Setup()
        {
            AssetDatabase.Refresh();
            EditorPrefs.SetBool(SettingExistedKey, BundleCollectorSettingData.HasSettingAsset());

            string packageName = $"UIFrameImageTests_{Guid.NewGuid():N}";
            EditorPrefs.SetString(PackageNameKey, packageName);

            var package = BundleCollectorSettingData.CreatePackage(packageName);
            package.EnableAddressable = true;
            package.IgnoreRuleName = nameof(NormalIgnoreRule);
            var group = BundleCollectorSettingData.CreateGroup(package, "Images");
            BundleCollectorSettingData.CreateCollector(group, new BundleCollector
            {
                CollectPath = AssetPath,
                CollectorType = ECollectorType.MainAssetCollector,
                PackRuleName = nameof(PackSeparately),
                FilterRuleName = nameof(CollectSprite)
            });
            BundleCollectorSettingData.SaveFile();

            var result = EditorSimulateBuildInvoker.Build(
                packageName,
                (int)EBundleType.VirtualAssetBundle);
            EditorPrefs.SetString(PackageRootKey, result.PackageRootDirectory);
        }

        void IPostBuildCleanup.Cleanup()
        {
            CleanupCollectorSetting();
        }

        internal static void CleanupCollectorSetting()
        {
            string packageName = PackageName;
            string packageRoot = EditorPrefs.GetString(PackageRootKey);
            if (!string.IsNullOrEmpty(packageName))
            {
                var package = BundleCollectorSettingData.Setting.Packages
                    .Find(item => item.PackageName == packageName);
                if (package != null)
                {
                    BundleCollectorSettingData.RemovePackage(package);
                    BundleCollectorSettingData.SaveFile();
                }
            }

            if (!EditorPrefs.GetBool(SettingExistedKey, true))
                AssetDatabase.DeleteAsset("Assets/BundleCollectorSetting.asset");

            if (!string.IsNullOrEmpty(packageRoot) && Directory.Exists(packageRoot))
                Directory.Delete(packageRoot, true);

            EditorPrefs.DeleteKey(PackageNameKey);
            EditorPrefs.DeleteKey(PackageRootKey);
            EditorPrefs.DeleteKey(SettingExistedKey);
            AssetDatabase.Refresh();
        }
    }
#endif

#if UNITY_EDITOR
    [PrebuildSetup(typeof(UIImageLoaderPackageSetup))]
#endif
    public sealed class UIImageLoaderTests
    {
        const string Location = "UIImageLoaderTestSprite";

        GameObject _object;
        Image _image;
        UIImageLoader _loader;
        ResourcePackage _package;
        Sprite _placeholder;
        Sprite _error;
        Texture2D _placeholderTexture;
        Texture2D _errorTexture;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
#if UNITY_EDITOR
            if (!YooAssets.IsInitialized)
                YooAssets.Initialize();

            _package = YooAssets.CreatePackage(UIImageLoaderPackageSetup.PackageName);
            var options = new EditorSimulateModeOptions
            {
                EditorFileSystemParameters =
                    FileSystemParameters.CreateDefaultEditorFileSystemParameters(
                        EditorPrefs.GetString(UIImageLoaderPackageSetup.PackageRootKey))
            };
            var initialize = _package.InitializePackageAsync(options);
            yield return initialize;
            Assert.That(initialize.Status, Is.EqualTo(EOperationStatus.Succeeded), initialize.Error);

            var version = _package.RequestPackageVersionAsync();
            yield return version;
            Assert.That(version.Status, Is.EqualTo(EOperationStatus.Succeeded), version.Error);

            var manifest = _package.LoadPackageManifestAsync(
                new LoadPackageManifestOptions(version.PackageVersion, 60));
            yield return manifest;
            Assert.That(manifest.Status, Is.EqualTo(EOperationStatus.Succeeded), manifest.Error);

            UI.Init(_package);
#else
            Assert.Ignore("UIImageLoader tests require the Unity Editor YooAsset simulation mode.");
#endif

            _placeholderTexture = new Texture2D(2, 2);
            _errorTexture = new Texture2D(2, 2);
            _placeholder = Sprite.Create(_placeholderTexture, new Rect(0, 0, 2, 2), Vector2.one * 0.5f);
            _error = Sprite.Create(_errorTexture, new Rect(0, 0, 2, 2), Vector2.one * 0.5f);

            _object = new GameObject("UIImageLoaderTest", typeof(RectTransform), typeof(Image));
            _image = _object.GetComponent<Image>();
            _loader = _object.AddComponent<UIImageLoader>();
            _loader.Placeholder = _placeholder;
            _loader.Error = _error;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_object != null)
                UnityEngine.Object.Destroy(_object);
            if (_placeholder != null)
                UnityEngine.Object.Destroy(_placeholder);
            if (_error != null)
                UnityEngine.Object.Destroy(_error);
            if (_placeholderTexture != null)
                UnityEngine.Object.Destroy(_placeholderTexture);
            if (_errorTexture != null)
                UnityEngine.Object.Destroy(_errorTexture);
            yield return null;

            if (UI.IsInited)
                UI.Shutdown();
            if (_package != null)
            {
                var destroy = _package.DestroyPackageAsync();
                yield return destroy;
            }
            if (YooAssets.IsInitialized)
                YooAssets.Destroy();
        }

#if UNITY_EDITOR
        [OneTimeTearDown]
        public void CleanupCollectorSetting()
        {
            UIImageLoaderPackageSetup.CleanupCollectorSetting();
        }
#endif

        [UnityTest]
        public IEnumerator LoadingUsesPlaceholderThenAssignsSprite()
        {
            var task = _loader.LoadAsync(Location);
            Assert.AreSame(_placeholder, _image.sprite);

            Exception error = null;
            yield return task.ToCoroutine(exception => error = exception);

            Assert.IsNull(error);
            Assert.IsNotNull(_image.sprite);
            Assert.AreNotSame(_placeholder, _image.sprite);
        }

        [UnityTest]
        public IEnumerator FailedLoadUsesErrorSprite()
        {
            ExpectMissingImageLogs();
            Exception error = null;
            yield return _loader.LoadAsync("missing-image", CancellationToken.None)
                .ToCoroutine(exception => error = exception);

            Assert.IsInstanceOf<Exception>(error);
            Assert.AreSame(_error, _image.sprite);
        }

        [UnityTest]
        public IEnumerator CancelledLoadDoesNotWriteBack()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Exception error = null;
            yield return _loader.LoadAsync(Location, cancellation.Token)
                .ToCoroutine(exception => error = exception);

            Assert.IsInstanceOf<OperationCanceledException>(error);
            Assert.AreSame(_placeholder, _image.sprite);
        }

        [UnityTest]
        public IEnumerator CancellingInFlightLoadStopsAwaitAndKeepsPlaceholder()
        {
            using var cancellation = new CancellationTokenSource();
            var task = _loader.LoadAsync(Location, cancellation.Token);
            cancellation.Cancel();

            Exception error = null;
            yield return task.ToCoroutine(exception => error = exception);

            Assert.IsInstanceOf<OperationCanceledException>(error);
            Assert.AreSame(_placeholder, _image.sprite);
        }

        [UnityTest]
        public IEnumerator OlderFailedRequestCannotOverwriteNewRequest()
        {
            ExpectMissingImageLogs();
            Exception firstError = null;
            var first = _loader.LoadAsync("missing-image", CancellationToken.None);
            var second = _loader.LoadAsync(Location, CancellationToken.None);

            yield return first.ToCoroutine(exception => firstError = exception);
            yield return second.ToCoroutine();

            Assert.IsNotNull(firstError);
            Assert.IsNotNull(_image.sprite);
            Assert.AreNotSame(_error, _image.sprite);
        }

        static void ExpectMissingImageLogs()
        {
            LogAssert.Expect(LogType.Warning, new Regex("Failed to map location.*missing-image"));
            LogAssert.Expect(LogType.Error, new Regex("Failed to load asset.*missing-image"));
        }

        [UnityTest]
        public IEnumerator ScopeCleanupRestoresPlaceholder()
        {
            var scope = new UIFrameScope(default);
            yield return _loader.LoadAsync(Location, scope).ToCoroutine();

            Assert.AreNotSame(_placeholder, _image.sprite);
            scope.Dispose();

            Assert.AreSame(_placeholder, _image.sprite);
        }

        [UnityTest]
        public IEnumerator OlderScopeCleanupDoesNotClearNewerRequest()
        {
            var firstScope = new UIFrameScope(default);
            yield return _loader.LoadAsync(Location, firstScope).ToCoroutine();

            var secondScope = new UIFrameScope(default);
            yield return _loader.LoadAsync(Location, secondScope).ToCoroutine();
            firstScope.Dispose();

            Assert.AreNotSame(_placeholder, _image.sprite);
            secondScope.Dispose();
        }

        [UnityTest]
        public IEnumerator ScopeCleanupAfterLoaderDestroyIsSafe()
        {
            var scope = new UIFrameScope(default);
            yield return _loader.LoadAsync(Location, scope).ToCoroutine();

            UnityEngine.Object.Destroy(_object);
            yield return null;

            Assert.DoesNotThrow(() => scope.Dispose());
        }

        [UnityTest]
        public IEnumerator DestroyReleasesCurrentHandle()
        {
            yield return _loader.LoadAsync(Location).ToCoroutine();
            var field = typeof(UIImageLoader).GetField(
                "_handle",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field.GetValue(_loader));

            UnityEngine.Object.Destroy(_object);
            yield return null;

            Assert.IsNull(field.GetValue(_loader));
        }
    }
}
