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
        public int callbackOrder => 100;
        public void OnPreprocessBuild(BuildReport report)
        {
            if ((report.summary.platform == BuildTarget.Android || report.summary.platform == BuildTarget.iOS)
                && EditorUserBuildSettings.activeBuildTarget != report.summary.platform)
                throw new BuildFailedException("UIFrame media: switch the active build target to the mobile target before building so native configuration callbacks are compiled.");
            var settings = GalleryBuildSettings.instance;
            if (report.summary.platform == BuildTarget.iOS && settings.enableLibraryRead && string.IsNullOrWhiteSpace(settings.photoLibraryUsageDescription))
                throw new BuildFailedException("UIFrame: photo library usage description is required.");
        }
#if UNITY_ANDROID
        public void OnPostGenerateGradleAndroidProject(string path)
        {
            string keepPath = Path.Combine(path, "proguard-unity.txt");
            File.AppendAllText(keepPath, "\n-keep class com.zzq.uiframe.media.BackupBridge { public static *; }\n-keep class com.zzq.uiframe.media.BackupJobService { *; }\n-keep class com.zzq.uiframe.media.GalleryBridge { public static *; }\n-keep class com.zzq.uiframe.media.GalleryActivity { *; }\n");
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
            project.AddBuildProperty(framework, "CLANG_ENABLE_OBJC_ARC", "YES"); project.WriteToFile(projectPath);
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
