using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UIFrame.Regression.Editor")]

namespace UIFrame.Editor
{
    [InitializeOnLoad]
    static class UICompileHook
    {
        static UICompileHook()
        {
            QueueJobs();
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        static void OnScriptsReloaded()
        {
            UIBindInspectorGui.ClearSyncedHosts();
            QueueJobs();
        }

        static void QueueJobs()
        {
            EditorApplication.delayCall -= ProcessJobs;
            EditorApplication.delayCall += ProcessJobs;
        }

        internal static void ProcessJobs()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                QueueJobs();
                return;
            }
            var store = UIBindStore.instance;
            var dirty = false;
            try
            {
                foreach (var state in store.All)
                {
                    if (state.PendingAttach)
                    {
                        state.PendingAttach = false;
                        dirty = true;
                        try
                        {
                            RequireFile(state.ScriptPath);
                            if (!TryAttach(state))
                                throw new System.InvalidOperationException($"挂载失败: {state.ClassName}。");
                        }
                        catch
                        {
                            state.PendingAssign = false;
                            throw;
                        }
                    }
                    if (state.PendingAssign)
                    {
                        state.PendingAssign = false;
                        dirty = true;
                        RequireFile(state.GenPath);
                        if (!TryAssign(state))
                            throw new System.InvalidOperationException($"回填引用失败: {state.ClassName}。请修正配置后重新生成。");
                    }
                }
            }
            finally
            {
                if (dirty) store.Persist();
            }
        }

        static void RequireFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(UIScriptWriter.ToFullPath(path)))
                throw new FileNotFoundException($"[UIFrame] 生成文件不存在: {path}", path);
        }

        static bool TryAttach(UIPrefabBindState state)
        {
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(state.ScriptPath);
            var type = script != null ? script.GetClass() : null;
            if (type == null || !typeof(MonoBehaviour).IsAssignableFrom(type) || type.IsAbstract)
            {
                return false;
            }

            if (!state.IsItem && !typeof(UIPanel).IsAssignableFrom(type))
            {
                return false;
            }

            var prefabPath = AssetDatabase.GUIDToAssetPath(state.PrefabGuid);
            if (string.IsNullOrEmpty(prefabPath) || !prefabPath.EndsWith(".prefab"))
            {
                return TryAttachToObject(state, type);
            }

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && PathsEqual(stage.assetPath, prefabPath))
            {
                if (!TryAddToPrefabStage(prefabPath, type, state.HostPath))
                {
                    return false;
                }

                Debug.LogWarning($"[UIFrame] 已挂载 {type.Name} 到 Prefab Stage，请保存 Prefab 以写入资产。");
                return true;
            }

            if (PrefabAlreadyHasHost(prefabPath, type, state.HostPath))
            {
                return true;
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var target = ResolveHostObject(root, state.HostPath);
                if (target == null)
                {
                    return false;
                }

                var attached = TryAddHostComponent(target, type);
                if (!attached)
                {
                    return false;
                }

                PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            Debug.Log($"[UIFrame] 已挂载 {type.Name}");
            return true;
        }

        static bool PrefabAlreadyHasHost(string prefabPath, System.Type type, string hostPath)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (asset == null)
            {
                return false;
            }

            var target = ResolveHostObject(asset, hostPath);
            return target != null && target.GetComponent(type) != null;
        }

        static bool TryAttachToObject(UIPrefabBindState state, System.Type type)
        {
            GameObject go = null;
            if (GlobalObjectId.TryParse(state.PrefabGuid, out var gid))
            {
                go = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as GameObject;
            }

            if (go == null)
            {
                return false;
            }

            if (!TryAddHostComponent(go, type))
            {
                return false;
            }

            EditorUtility.SetDirty(go);
            Debug.Log($"[UIFrame] 已挂载 {type.Name}");
            return true;
        }

        static bool TryAssign(UIPrefabBindState state)
        {
            if (state.Binds == null)
            {
                return true;
            }

            var prefabPath = AssetDatabase.GUIDToAssetPath(state.PrefabGuid);
            if (string.IsNullOrEmpty(prefabPath) || !prefabPath.EndsWith(".prefab"))
            {
                if (!GlobalObjectId.TryParse(state.PrefabGuid, out var gid))
                {
                    return false;
                }

                var go = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as GameObject;
                return go != null && AssignOnRoot(go, state);
            }

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && PathsEqual(stage.assetPath, prefabPath))
            {
                var ok = AssignOnRoot(stage.prefabContentsRoot, state);
                if (ok)
                {
                    EditorSceneManager.MarkSceneDirty(stage.scene);
                    Debug.LogWarning("[UIFrame] 已在 Prefab Stage 回填引用，请保存 Prefab 以写入资产。");
                }

                return ok;
            }

            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                var ok = AssignOnRoot(root, state);
                if (ok)
                {
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                }

                return ok;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        static bool TryAddToPrefabStage(string prefabPath, System.Type type, string hostPath)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null || !PathsEqual(stage.assetPath, prefabPath))
            {
                return false;
            }

            var target = ResolveHostObject(stage.prefabContentsRoot, hostPath);
            if (target == null)
            {
                return false;
            }

            var attached = TryAddHostComponent(target, type);
            EditorSceneManager.MarkSceneDirty(stage.scene);
            return attached;
        }

        static bool TryAddHostComponent(GameObject target, System.Type type)
        {
            if (target == null || type == null)
            {
                return false;
            }

            if (target.GetComponent(type) != null)
            {
                return true;
            }

            return target.AddComponent(type) != null;
        }

        static bool PathsEqual(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }

            return string.Equals(
                a.Replace('\\', '/'),
                b.Replace('\\', '/'),
                System.StringComparison.OrdinalIgnoreCase);
        }

        static GameObject ResolveHostObject(GameObject root, string hostPath)
        {
            if (root == null)
            {
                return null;
            }

            if (string.IsNullOrEmpty(hostPath))
            {
                return root;
            }

            var node = UICodeGenUtil.FindByPath(root.transform, hostPath);
            return node != null ? node.gameObject : null;
        }

        static bool AssignOnRoot(GameObject root, UIPrefabBindState state)
        {
            var host = FindHost(root, state);
            if (host == null)
            {
                return false;
            }

            var so = new SerializedObject(host);
            var props = new List<SerializedProperty>();
            var values = new List<UnityEngine.Object>();
            for (var i = 0; i < state.Binds.Count; i++)
            {
                var bind = state.Binds[i];
                var prop = so.FindProperty(bind.FieldName);
                if (prop == null)
                {
                    throw new System.InvalidOperationException($"[UIFrame] 回填失败: Host={host.GetType().FullName}, Field={bind.FieldName}: 找不到序列化字段。");
                }

                if (bind.HierarchyPath == null && bind.LocalFileId == 0)
                {
                    throw new System.InvalidOperationException($"[UIFrame] 回填失败: Host={host.GetType().FullName}, Field={bind.FieldName}: 缺少节点路径和 LocalFileId，请显式重新绑定。");
                }

                var node = UICodeGenUtil.FindBindNode(host.transform, bind);
                if (node == null)
                {
                    throw new System.InvalidOperationException($"[UIFrame] 找不到节点: {bind.HierarchyPath ?? bind.FieldName}");
                }

                if (bind.IsGameObject)
                {
                    props.Add(prop);
                    values.Add(node.gameObject);
                    continue;
                }

                var found = FindComponent(node, bind.TypeName);
                if (found == null)
                {
                    throw new System.InvalidOperationException($"[UIFrame] 找不到组件 {bind.TypeName}: {bind.HierarchyPath ?? bind.FieldName}");
                }

                props.Add(prop);
                values.Add(found);
            }

            for (var i = 0; i < props.Count; i++)
            {
                props[i].objectReferenceValue = values[i];
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(host);
            if (PrefabUtility.IsPartOfPrefabInstance(host))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(host);
            }

            return true;
        }

        static Component FindHost(GameObject root, UIPrefabBindState state)
        {
            var target = ResolveHostObject(root, state.HostPath);
            if (target == null)
            {
                Debug.LogWarning($"[UIFrame] 找不到绑定宿主路径: {state.HostPath}");
                return null;
            }

            var fullName = string.IsNullOrEmpty(state.NamespaceName)
                ? state.ClassName
                : state.NamespaceName + "." + state.ClassName;
            var behaviours = target.GetComponents<MonoBehaviour>();
            for (var i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] != null && behaviours[i].GetType().FullName == fullName)
                {
                    return behaviours[i];
                }
            }

            Debug.LogWarning($"[UIFrame] 找不到绑定宿主: {state.ClassName}");
            return null;
        }

        static Component FindComponent(Transform node, string typeName)
        {
            var components = node.GetComponents<Component>();
            for (var i = 0; i < components.Length; i++)
            {
                var c = components[i];
                if (c != null && UICodeGenUtil.ToCsTypeName(c.GetType()) == typeName)
                {
                    return c;
                }
            }

            var type = UICodeGenUtil.FindType(typeName);
            return type != null && typeof(Component).IsAssignableFrom(type)
                ? node.GetComponent(type)
                : null;
        }
    }
}
