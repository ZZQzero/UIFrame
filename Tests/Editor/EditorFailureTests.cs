using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NUnit.Framework;
using UIFrame.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace UIFrame.Regression.First
{
    public class Host : MonoBehaviour { public GameObject First; public GameObject Second; }
}
namespace UIFrame.Regression.Second
{
    public class Host : MonoBehaviour { public GameObject First; }
}
namespace UIFrame.Regression
{
    public class EditorFailureTests
    {
        static object Call(string method, params object[] args) => typeof(UICompileHook)
            .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, args);

        [Test] public void HostResolutionUsesNamespace()
        {
            var root = new GameObject("binding-test");
            try
            {
                root.AddComponent<First.Host>();
                var expected = root.AddComponent<Second.Host>();
                var state = new UIPrefabBindState { ClassName = "Host", NamespaceName = typeof(Second.Host).Namespace };
                Assert.AreSame(expected, Call("FindHost", root, state));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test] public void UnresolvedFieldFailsWholeAssignmentAndKeepsOldReferences()
        {
            var root = new GameObject("binding-test");
            var child = new GameObject("old-reference");
            child.transform.SetParent(root.transform);
            try
            {
                var host = root.AddComponent<First.Host>();
                host.First = child;
                host.Second = child;
                var state = new UIPrefabBindState { ClassName = "Host", NamespaceName = typeof(First.Host).Namespace };
                state.Binds.Add(new UIBindEntry { FieldName = "First", IsGameObject = true, HierarchyPath = "" });
                state.Binds.Add(new UIBindEntry { FieldName = "Second", IsGameObject = true });
                LogAssert.Expect(LogType.Error, new Regex("Field=Second.*缺少节点路径"));
                Assert.AreEqual(false, Call("AssignOnRoot", root, state));
                Assert.AreSame(child, host.First, "其它字段也不能部分回填");
                Assert.AreSame(child, host.Second);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test] public void AttachingHostPreservesMissingScript()
        {
            const string path = "Assets/__UIFrameMissingScriptRegression.prefab";
            Assert.IsFalse(File.Exists(path), "测试不覆盖现有资产");
            var root = new GameObject("missing-script-test");
            try
            {
                PrefabUtility.SaveAsPrefabAsset(root, path);
                var yaml = File.ReadAllText(path);
                const long componentId = 9123456789000;
                var gameObjectId = Regex.Match(yaml, @"--- !u!1 &(\d+)").Groups[1].Value;
                yaml = yaml.Replace("  m_Component:\n", "  m_Component:\n  - component: {fileID: " + componentId + "}\n");
                yaml += "\n--- !u!114 &" + componentId + "\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: " + gameObjectId + "}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n  m_Script: {fileID: 11500000, guid: fffffffffffffffffffffffffffffffe, type: 3}\n  m_Name: \n  m_EditorClassIdentifier: \n  preservedData: 42\n";
                File.WriteAllText(path, yaml);
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                var contents = PrefabUtility.LoadPrefabContents(path);
                try
                {
                    Assert.AreEqual(1, GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(contents));
                    Assert.AreEqual(true, Call("TryAddHostComponent", contents, typeof(First.Host)));
                    Assert.AreEqual(1, GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(contents));
                }
                finally { PrefabUtility.UnloadPrefabContents(contents); }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                AssetDatabase.DeleteAsset(path);
            }
        }

        [Test] public async Task GeneratorReportsExitCodeAndBothOutputStreams()
        {
#if UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
            var root = Path.Combine(Path.GetTempPath(), "UIFrame-Assets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "gen.sh"), "#!/bin/bash\nprintf 'stdout-evidence'\nprintf 'stderr-evidence' >&2\nexit 7\n");
                try
                {
                    await LubanGenerateExcels.RunGeneratorAsync(root);
                    Assert.Fail("生成器失败必须报告错误");
                }
                catch (InvalidOperationException error)
                {
                    StringAssert.Contains("ExitCode=7", error.Message);
                    StringAssert.Contains("stdout-evidence", error.Message);
                    StringAssert.Contains("stderr-evidence", error.Message);
                }
            }
            finally { Directory.Delete(root, true); }
#else
            await Task.CompletedTask;
            Assert.Ignore("此脚本 fixture 只覆盖 macOS/Linux；Windows 使用独立 cmd 路径。");
#endif
        }
    }
}
