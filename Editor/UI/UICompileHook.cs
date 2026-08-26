using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UIFrame.Editor
{
    [InitializeOnLoad]
    static class UICompileHook
    {
        static bool _assignRetryQueued;

        static UICompileHook()
        {
            EditorApplication.delayCall += ProcessJobs;
        }

        [UnityEditor.Callbacks.DidReloadScripts]
        static void OnScriptsReloaded()
        {
            EditorApplication.delayCall += ProcessJobs;
        }

        internal static void ProcessJobs()
        {
            ProcessJobs(queueAssignRetry: true);
        }

        static void ProcessJobs(bool queueAssignRetry)
        {
            var store = UIBindStore.instance;
            var dirty = false;
            for (var i = 0; i < store.All.Count; i++)
            {
                var state = store.All[i];
                if (state.PendingAttach)
                {
                    if (TryAttach(state))
                    {
                        state.PendingAttach = false;
                        dirty = true;
                    }
                }

                if (state.PendingAssign && !state.PendingAttach)
                {
                    if (TryAssign(state))
                    {
                        state.PendingAssign = false;
                        dirty = true;
                    }
                    else if (queueAssignRetry && !_assignRetryQueued)
                    {
                        _assignRetryQueued = true;
                        EditorApplication.delayCall += RetryAssignOnce;
                    }
                }
            }

            if (dirty)
            {
                store.Persist();
            }
        }

        static void RetryAssignOnce()
        {
            _assignRetryQueued = false;
            ProcessJobs(queueAssignRetry: false);
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

            if (TryAddToPrefabStage(prefabPath, type, state.HostPath))
            {
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

                if (target.GetComponent(type) == null)
                {
                    target.AddComponent(type);
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

            if (go.GetComponent(type) == null)
            {
                go.AddComponent(type);
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

            if (target.GetComponent(type) == null)
            {
                target.AddComponent(type);
            }

            EditorSceneManager.MarkSceneDirty(stage.scene);
            return true;
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
            var missing = false;
            var unresolved = false;
            for (var i = 0; i < state.Binds.Count; i++)
            {
                var bind = state.Binds[i];
                var prop = so.FindProperty(bind.FieldName);
                if (prop == null)
                {
                    missing = true;
                    continue;
                }

                if (bind.HierarchyPath == null && bind.LocalFileId == 0 && string.IsNullOrEmpty(bind.TypeName))
                {
                    continue;
                }

                var node = UICodeGenUtil.FindBindNode(host.transform, bind);
                if (node == null)
                {
                    unresolved = true;
                    Debug.LogWarning($"[UIFrame] 找不到节点: {bind.HierarchyPath ?? bind.FieldName}");
                    continue;
                }

                if (bind.IsGameObject)
                {
                    prop.objectReferenceValue = node.gameObject;
                    continue;
                }

                var found = FindComponent(node, bind.TypeName);
                if (found == null)
                {
                    unresolved = true;
                    Debug.LogWarning($"[UIFrame] 找不到组件 {bind.TypeName}: {bind.HierarchyPath ?? bind.FieldName}");
                    continue;
                }

                prop.objectReferenceValue = found;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(host);
            if (PrefabUtility.IsPartOfPrefabInstance(host))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(host);
            }

            return !missing && !unresolved;
        }

        static Component FindHost(GameObject root, UIPrefabBindState state)
        {
            var target = ResolveHostObject(root, state.HostPath);
            if (target == null)
            {
                Debug.LogWarning($"[UIFrame] 找不到绑定宿主路径: {state.HostPath}");
                return null;
            }

            var behaviours = target.GetComponents<MonoBehaviour>();
            for (var i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] != null && behaviours[i].GetType().Name == state.ClassName)
                {
                    return behaviours[i];
                }
            }

            return UICodeGenUtil.FindBindHostOn(target);
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
