using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UIFrame.Sqlite.Editor
{
    /// <summary>Reject a build whose engine artifact cannot be traced to its checked-in source.</summary>
    public sealed class SqliteBuildProcessor : IPreprocessBuildWithReport
    {
        [Serializable]
        sealed class Artifact
        {
            public int abi;
            public bool sanitized;
            public string binary, sha256, build_id, core_sha256, header_sha256, cmake_sha256;
        }
        public int callbackOrder => 90;
        public void OnPreprocessBuild(BuildReport report)
        {
            string platform;
            switch (report.summary.platform)
            {
            case BuildTarget.StandaloneWindows64:
                if (PlayerSettings.GetArchitecture(UnityEditor.Build.NamedBuildTarget.Standalone) != 1)
                    throw new BuildFailedException("UIFrame.Sqlite currently provides Windows x86_64 only.");
                platform = "Windows/x86_64";
                break;
            case BuildTarget.StandaloneOSX:
                platform = "macOS";
                break;
            case BuildTarget.Android:
                if (PlayerSettings.Android.targetArchitectures != AndroidArchitecture.ARM64)
                    throw new BuildFailedException("UIFrame.Sqlite currently provides Android ARM64 only. " +
                                                   "Build and validate each requested ABI before shipping.");
                platform = "Android/arm64-v8a";
                break;
            case BuildTarget.iOS:
                if (PlayerSettings.iOS.sdkVersion != iOSSdkVersion.DeviceSDK)
                    throw new BuildFailedException(
                        "UIFrame.Sqlite currently ships the iOS device archive. A simulator archive must " +
                        "be built and validated separately.");
                platform = "iOS";
                break;
            default:
                throw new BuildFailedException("UIFrame.Sqlite has no validated native artifact for " +
                                               report.summary.platform);
            }
            string root = LocateRoot();
            string directory = Path.Combine(root, "Plugins", platform);
            var manifest =
                JsonUtility.FromJson<Artifact>(File.ReadAllText(Path.Combine(directory, "artifact.json")));
            if (manifest == null || manifest.abi != Internal.NativeMethods.Abi || manifest.sanitized ||
                string.IsNullOrEmpty(manifest.binary) ||
                Path.GetFileName(manifest.binary) != manifest.binary ||
                string.IsNullOrEmpty(manifest.build_id))
                throw new BuildFailedException("UIFrame.Sqlite artifact identity is invalid.");
            Verify(Path.Combine(directory, manifest.binary), manifest.sha256);
            Verify(Path.Combine(root, "Native~/src/core.cpp"), manifest.core_sha256);
            Verify(Path.Combine(root, "Native~/include/ufsqlite.h"), manifest.header_sha256);
            Verify(Path.Combine(root, "Native~/CMakeLists.txt"), manifest.cmake_sha256);
        }
        static string LocateRoot()
        {
            foreach (string guid in AssetDatabase.FindAssets("UIFrame.Sqlite t:AssemblyDefinitionAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path) == "UIFrame.Sqlite.asmdef")
                {
                    var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
                    string physical = package == null
                                          ? Path.GetFullPath(path)
                                          : Path.Combine(package.resolvedPath,
                                                         path.Substring(package.assetPath.Length + 1));
                    return Path.GetDirectoryName(physical);
                }
            }
            throw new BuildFailedException("UIFrame.Sqlite assembly definition cannot be located.");
        }
        static void Verify(string path, string expected)
        {
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) if (
                !string.Equals(
                    BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(),
                    expected,
                    StringComparison
                        .Ordinal)) throw new BuildFailedException("UIFrame.Sqlite source or artifact " +
                                                                  "changed; rebuild and install before " +
                                                                  "shipping: " +
                                                                  path);
        }
    }
}
