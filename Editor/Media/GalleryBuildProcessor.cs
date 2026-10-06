using System.IO;
using System.Xml;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
#if UNITY_ANDROID
using UnityEditor.Android;
#endif
#if UNITY_IOS
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
#endif

namespace UIFrame.Editor
{
    [FilePath("ProjectSettings/UIFrameMediaSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class GalleryBuildSettings : ScriptableSingleton<GalleryBuildSettings>
    {
        public bool enableLibraryRead;
        public string photoLibraryUsageDescription = "选择并备份您授权的照片";
        public void SaveSettings() => Save(true);
    }

    public sealed class GalleryBuildProcessor : IPreprocessBuildWithReport
#if UNITY_ANDROID
        , IPostGenerateGradleAndroidProject
#endif
    {
        internal static string LocateRoot()
        {
            foreach(string guid in AssetDatabase.FindAssets("UIFrame.Runtime t:AssemblyDefinitionAsset"))
            {
                string asset=AssetDatabase.GUIDToAssetPath(guid);if(Path.GetFileName(asset)!="UIFrame.Runtime.asmdef")continue;
                var package=UnityEditor.PackageManager.PackageInfo.FindForAssetPath(asset);
                return package!=null?package.resolvedPath:Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(asset))).FullName;
            }
            throw new BuildFailedException("UIFrame runtime assembly not found.");
        }
        public int callbackOrder => 100;
        public void OnPreprocessBuild(BuildReport report)
        {
            if ((report.summary.platform == BuildTarget.Android || report.summary.platform == BuildTarget.iOS)
                && EditorUserBuildSettings.activeBuildTarget != report.summary.platform)
                throw new BuildFailedException("UIFrame media: switch the active build target to the mobile target before building so native configuration callbacks are compiled.");
            VerifyBackupArtifact(report.summary.platform);
            var settings = GalleryBuildSettings.instance;
            if (report.summary.platform == BuildTarget.iOS && settings.enableLibraryRead && string.IsNullOrWhiteSpace(settings.photoLibraryUsageDescription))
                throw new BuildFailedException("UIFrame: photo library usage description is required.");
        }
        [System.Serializable] sealed class SourceHash {public string path,sha256;}
        [System.Serializable] sealed class BackupArtifact {public int abi;public string binary,sha256,sqlite_build_id;public SourceHash[] sources;}
        [System.Serializable] sealed class CoreArtifact {public string build_id;}
        static void VerifyBackupArtifact(BuildTarget target)
        {
            string folder=target==BuildTarget.Android?"Android/arm64-v8a":target==BuildTarget.iOS?"iOS":target==BuildTarget.StandaloneOSX?"macOS":target==BuildTarget.StandaloneWindows64?"Windows/x86_64":null;
            if(folder==null)throw new BuildFailedException("No backup repository artifact for "+target);
            string root=LocateRoot(),native=Path.Combine(root,"Runtime/MediaBackup/Native~"),plugins=Path.Combine(root,"Runtime/MediaBackup/Plugins",folder);
            var artifact=UnityEngine.JsonUtility.FromJson<BackupArtifact>(File.ReadAllText(Path.Combine(plugins,"artifact.json")));
            var core=UnityEngine.JsonUtility.FromJson<CoreArtifact>(File.ReadAllText(Path.Combine(root,"Runtime/Sqlite/Plugins",folder,"artifact.json")));
            if(artifact.abi!=4 || artifact.sqlite_build_id!=core.build_id || artifact.sources==null || Path.GetFileName(artifact.binary)!=artifact.binary)throw new BuildFailedException("Backup repository artifact/dependency mismatch.");
            VerifyHash(Path.Combine(plugins,artifact.binary),artifact.sha256);
            foreach(var source in artifact.sources)VerifyHash(Path.Combine(native,source.path),source.sha256);
        }
        static void VerifyHash(string path,string expected)
        {
            using var sha=System.Security.Cryptography.SHA256.Create();using var input=File.OpenRead(path);
            string actual=System.BitConverter.ToString(sha.ComputeHash(input)).Replace("-","").ToLowerInvariant();
            if(actual!=expected)throw new BuildFailedException("Backup source or artifact changed; rebuild before shipping: "+path);
        }
#if UNITY_ANDROID
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string keepPath = Path.Combine(path, "proguard-unity.txt");
            File.AppendAllText(keepPath, "\n-keep class com.zzq.uiframe.media.BackupBridge { public static *; }\n-keep class com.zzq.uiframe.media.BackupRepository { *; }\n-keep class com.zzq.uiframe.media.BackupRepository$* { *; }\n-keep class com.zzq.uiframe.media.GalleryIndex { *; }\n-keep class com.zzq.uiframe.media.BackupJobService { *; }\n-keep class com.zzq.uiframe.media.GalleryBridge { public static *; }\n-keep class com.zzq.uiframe.media.GalleryActivity { *; }\n");
            if (!GalleryBuildSettings.instance.enableLibraryRead) return;
            string manifest = Path.Combine(path, "src/main/AndroidManifest.xml");
            var doc = new XmlDocument(); doc.Load(manifest);
            const string ns = "http://schemas.android.com/apk/res/android";
            foreach (var name in new[] { "android.permission.READ_EXTERNAL_STORAGE", "android.permission.READ_MEDIA_IMAGES", "android.permission.READ_MEDIA_VISUAL_USER_SELECTED" })
            {
                var node = doc.CreateElement("uses-permission"); node.SetAttribute("name", ns, name);
                if (name.EndsWith("READ_EXTERNAL_STORAGE")) node.SetAttribute("maxSdkVersion", ns, "32");
                bool exists = false;
                foreach (XmlNode candidate in doc.GetElementsByTagName("uses-permission"))
                    if (candidate is XmlElement element && element.GetAttribute("name", ns) == name) { exists = true; break; }
                if (!exists) doc.DocumentElement.AppendChild(node);
            }
            doc.Save(manifest);
        }
#endif
#if UNITY_IOS
        [PostProcessBuild(100)]
        static void ConfigureIos(BuildTarget target, string path)
        {
            if (target != BuildTarget.iOS) return;
            string projectPath = PBXProject.GetPBXProjectPath(path); var project = new PBXProject(); project.ReadFromFile(projectPath);
            string framework = project.GetUnityFrameworkTargetGuid();
            foreach (var name in new[] { "Photos.framework", "PhotosUI.framework", "ImageIO.framework", "UniformTypeIdentifiers.framework", "Network.framework", "Security.framework" }) project.AddFrameworkToProject(framework, name, true);
            string packageRoot=LocateRoot();string headers=Path.Combine(path,"Libraries/UIFrameBackupHeaders");Directory.CreateDirectory(headers);
            File.Copy(Path.Combine(packageRoot,"Runtime/MediaBackup/Native~/include/ufbackup.h"),Path.Combine(headers,"ufbackup.h"),true);
            foreach(string header in new[]{"ufsqlite.h","ufsqlite_client.hpp"})File.Copy(Path.Combine(packageRoot,"Runtime/Sqlite/Native~/include",header),Path.Combine(headers,header),true);
            project.AddBuildProperty(framework,"HEADER_SEARCH_PATHS","$(SRCROOT)/Libraries/UIFrameBackupHeaders");
            project.SetBuildProperty(framework,"CLANG_ENABLE_OBJC_ARC","YES");
            project.SetBuildProperty(framework,"GCC_ENABLE_OBJC_EXCEPTIONS","YES");
            project.SetBuildProperty(framework,"GCC_ENABLE_CPP_EXCEPTIONS","YES");
            project.SetBuildProperty(framework,"CLANG_ENABLE_OBJC_ARC_EXCEPTIONS","YES");
            project.WriteToFile(projectPath);
            var settings = GalleryBuildSettings.instance;
            if (settings.enableLibraryRead)
            {
                string plistPath = Path.Combine(path, "Info.plist"); var plist = new PlistDocument(); plist.ReadFromFile(plistPath);
                plist.root.SetString("NSPhotoLibraryUsageDescription", settings.photoLibraryUsageDescription); plist.WriteToFile(plistPath);
            }
        }
#endif
    }
}
