using System;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
public static class BuildSmoke
{
    public static void Windows()
    {
        if (Application.platform != RuntimePlatform.WindowsEditor)
            throw new PlatformNotSupportedException("Windows IL2CPP validation requires Windows Editor.");
        PlayerSettings.companyName = "UIFrameValidation";
        PlayerSettings.productName = "SqliteSmoke";
        PlayerSettings.SetArchitecture(UnityEditor.Build.NamedBuildTarget.Standalone, 1);
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Standalone,
                                           ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(UnityEditor.Build.NamedBuildTarget.Standalone,
                                                ManagedStrippingLevel.High);
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, "Assets/Smoke.unity");
        var report = BuildPipeline.BuildPlayer(
            new BuildPlayerOptions { scenes = new[] { "Assets/Smoke.unity" },
                                     target = BuildTarget.StandaloneWindows64,
                                     locationPathName = "Build/Windows/SqliteSmoke.exe",
                                     options = BuildOptions.None });
        if (report.summary.result != BuildResult.Succeeded)
            throw new Exception("Isolated SQLite Windows build failed: " + report.summary.result);
        Debug.Log("UIFRAME_SQLITE_WINDOWS_BUILD_PASSED");
    }
    public static void Android()
    {
#if UNITY_ANDROID
        UnityEditor.Android.AndroidExternalToolsSettings.sdkRootPath = Argument("-sqliteSdk");
        UnityEditor.Android.AndroidExternalToolsSettings.ndkRootPath = Argument("-sqliteNdk");
        UnityEditor.Android.AndroidExternalToolsSettings.jdkRootPath = Argument("-sqliteJdk");
        PlayerSettings.companyName = "UIFrameValidation";
        PlayerSettings.productName = "SqliteSmoke";
        PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android,
                                                "com.zzq.sqlitevalidation");
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.Android,
                                           ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(UnityEditor.Build.NamedBuildTarget.Android,
                                                ManagedStrippingLevel.High);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel25;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, "Assets/Smoke.unity");
        var report = BuildPipeline.BuildPlayer(
            new BuildPlayerOptions { scenes = new[] { "Assets/Smoke.unity" }, target = BuildTarget.Android,
                                     locationPathName = "Build/SqliteSmoke.apk",
                                     options = BuildOptions.None });
        if (report.summary.result != BuildResult.Succeeded)
            throw new Exception("Isolated SQLite Android build failed: " + report.summary.result);
        Debug.Log("UIFRAME_SQLITE_ANDROID_BUILD_PASSED");
#else
        throw new PlatformNotSupportedException("Select the Android build target before validation.");
#endif
    }
    public static void Ios()
    {
        PlayerSettings.companyName = "UIFrameValidation";
        PlayerSettings.productName = "SqliteSmoke";
        PlayerSettings.SetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.iOS,
                                                "com.zzq.sqlitevalidation");
        PlayerSettings.SetScriptingBackend(UnityEditor.Build.NamedBuildTarget.iOS,
                                           ScriptingImplementation.IL2CPP);
        PlayerSettings.SetManagedStrippingLevel(UnityEditor.Build.NamedBuildTarget.iOS,
                                                ManagedStrippingLevel.High);
        PlayerSettings.iOS.targetOSVersionString = "15.0";
        PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, "Assets/Smoke.unity");
        var report = BuildPipeline.BuildPlayer(
            new BuildPlayerOptions { scenes = new[] { "Assets/Smoke.unity" }, target = BuildTarget.iOS,
                                     locationPathName = "Build/iOS", options = BuildOptions.None });
        if (report.summary.result != BuildResult.Succeeded)
            throw new Exception("Isolated SQLite iOS export failed: " + report.summary.result);
        Debug.Log("UIFRAME_SQLITE_IOS_EXPORT_PASSED");
    }
    static string Argument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 >= args.Length || !System.IO.Directory.Exists(args[index + 1]))
            throw new ArgumentException("An existing toolchain directory is required for " + name);
        return args[index + 1];
    }
}
