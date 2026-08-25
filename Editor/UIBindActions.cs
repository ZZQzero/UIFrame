using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UIFrame.Editor
{
    static class UIBindActions
    {
        static readonly Regex GenFieldRegex = new Regex(
            @"\[SerializeField\]\s+private\s+([^\s;]+)\s+([A-Za-z_][A-Za-z0-9_]*)\s*;",
            RegexOptions.Compiled);

        public static bool TryGenerate(
            GameObject root,
            string className,
            string folder,
            string namespaceName,
            bool isItem,
            out string error)
        {
            error = null;
            var prefabPath = UICodeGenUtil.GetPrefabAssetPath(root);
            var storeKey = UICodeGenUtil.GetStoreKey(root);
            if (string.IsNullOrEmpty(prefabPath) && string.IsNullOrEmpty(storeKey))
            {
                error = "请选中 Prefab 资源、Prefab 根、带 Canvas 的界面，或在 Prefab 编辑模式中操作。";
                return false;
            }

            if (UICodeGenUtil.FindBindHostOn(root) != null)
            {
                error = "该 Prefab 已经挂有生成脚本。";
                return false;
            }

            className = UICodeGenUtil.ToPascalIdentifier(className);
            if (!isItem && !className.EndsWith("Panel"))
            {
                className += "Panel";
            }

            namespaceName = string.IsNullOrWhiteSpace(namespaceName) ? "Game" : namespaceName.Trim();
            if (!UICodeGenUtil.IsValidNamespace(namespaceName))
            {
                error = $"命名空间无效: {namespaceName}";
                return false;
            }

            if (!TryNormalizeScriptFolder(folder, out folder, out error))
            {
                return false;
            }

            var scriptPath = folder + "/" + className + ".cs";
            var genPath = folder + "/" + className + ".Gen.cs";
            if (!ValidateExistingFiles(scriptPath, genPath, namespaceName, className, isItem, out error))
            {
                return false;
            }

            UIScriptWriter.EnsureFolder(folder);

            var guid = !string.IsNullOrEmpty(prefabPath)
                ? AssetDatabase.AssetPathToGUID(prefabPath)
                : storeKey;
            var state = UIBindStore.instance.GetOrCreate(guid, "");
            state.ClassName = className;
            state.NamespaceName = namespaceName;
            state.ScriptPath = scriptPath;
            state.GenPath = genPath;
            state.IsItem = isItem;
            state.HostPath = "";
            state.PendingAttach = true;
            state.PendingAssign = false;

            if (!File.Exists(UIScriptWriter.ToFullPath(scriptPath)))
            {
                if (isItem)
                {
                    UIScriptWriter.WriteItemScript(scriptPath, state.NamespaceName, className);
                }
                else
                {
                    UIScriptWriter.WritePanelScript(scriptPath, state.NamespaceName, className);
                }
            }

            if (!File.Exists(UIScriptWriter.ToFullPath(genPath)))
            {
                UIScriptWriter.WriteGenScript(state);
            }

            UICodeGenPrefs.Folder = folder;
            UICodeGenPrefs.NamespaceName = state.NamespaceName;
            UIBindStore.instance.Persist();
            AssetDatabase.ImportAsset(scriptPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(genPath, ImportAssetOptions.ForceUpdate);
            EditorApplication.delayCall += UICompileHook.ProcessJobs;
            var kind = isItem ? "Item/Cell" : "Panel";
            Debug.Log($"[UIFrame] 已生成 {kind} {className}，编译完成后会自动挂到 Prefab。");
            return true;
        }

        public static bool TryAddComponent(Component component, out string error)
        {
            return TryAddComponent(component, null, out error);
        }

        public static bool TryAddComponent(Component component, MonoBehaviour host, out string error)
        {
            if (UICodeGenUtil.IsBlockedComponent(component))
            {
                error = "该组件不能绑定到脚本。";
                return false;
            }

            return TryAdd(component, component.GetType(), isGameObject: false, host, out error);
        }

        public static bool TryAddGameObject(Component component, out string error)
        {
            return TryAddGameObject(component, null, out error);
        }

        public static bool TryAddGameObject(Component component, MonoBehaviour host, out string error)
        {
            if (component == null)
            {
                error = "没有可用节点。";
                return false;
            }

            return TryAdd(component, typeof(GameObject), isGameObject: true, host, out error);
        }

        static bool TryAdd(Component component, Type type, bool isGameObject, MonoBehaviour host, out string error)
        {
            error = null;
            if (host == null)
            {
                host = UICodeGenUtil.FindBindTargetHost(component);
            }

            if (host == null)
            {
                error = "向上找不到可写入的外层脚本。";
                return false;
            }

            if (host == component)
            {
                error = "不能把脚本绑定到自己身上。";
                return false;
            }

            var state = GetOrCreateState(host);
            if (state == null)
            {
                error = "当前对象不是 Prefab，无法写入绑定。";
                return false;
            }

            if (state.PendingAttach)
            {
                error = "脚本还在编译/挂载，请稍后再绑定。";
                return false;
            }

            EnsureStateFromHost(state, host);

            var path = UICodeGenUtil.HierarchyPath(host.transform, component.transform);
            if (path == null)
            {
                error = "该节点不在脚本根下。";
                return false;
            }

            var typeName = isGameObject ? "UnityEngine.GameObject" : UICodeGenUtil.ToCsTypeName(type);
            for (var i = 0; i < state.Binds.Count; i++)
            {
                var bind = state.Binds[i];
                if (bind.HierarchyPath == path && bind.IsGameObject == isGameObject && bind.TypeName == typeName)
                {
                    error = $"已经绑定过 {bind.FieldName}。";
                    return false;
                }
            }

            var field = UICodeGenUtil.MakeFieldName(state, component.gameObject.name, isGameObject, type);
            state.Binds.Add(new UIBindEntry
            {
                FieldName = field,
                TypeName = typeName,
                HierarchyPath = path,
                IsGameObject = isGameObject,
                LocalFileId = UICodeGenUtil.GetLocalFileId(isGameObject ? (UnityEngine.Object)component.gameObject : component),
            });
            state.BindsInitialized = true;
            UIBindStore.instance.Persist();
            Debug.Log($"[UIFrame] 已记录绑定 {host.GetType().Name}.{field}（待写入脚本）");
            return true;
        }

        public static void RemoveBind(UIPrefabBindState state, int index)
        {
            if (state == null || state.Binds == null || index < 0 || index >= state.Binds.Count)
            {
                return;
            }

            state.Binds.RemoveAt(index);
            UIBindStore.instance.Persist();
        }

        public static bool TryWriteGen(GameObject root, out string error)
        {
            return TryWriteGen(root != null ? UICodeGenUtil.FindBindHostOn(root) : null, out error);
        }

        public static bool TryWriteGen(MonoBehaviour host, out string error)
        {
            error = null;
            if (host == null)
            {
                error = "根上没有生成脚本。";
                return false;
            }

            var state = GetOrCreateState(host);
            if (state == null)
            {
                error = "当前对象不是 Prefab，无法写入。";
                return false;
            }

            if (state.PendingAttach)
            {
                error = "脚本还在挂载，请等编译完成。";
                return false;
            }

            EnsureStateFromHost(state, host);
            if (string.IsNullOrEmpty(state.GenPath))
            {
                error = "找不到 .Gen.cs 路径。";
                return false;
            }

            UIScriptWriter.WriteGenScript(state);
            state.PendingAssign = true;
            UIBindStore.instance.Persist();
            AssetDatabase.ImportAsset(state.GenPath, ImportAssetOptions.ForceUpdate);
            EditorApplication.delayCall += UICompileHook.ProcessJobs;
            Debug.Log($"[UIFrame] 已写入 {state.GenPath}，编译完成后会回填引用。");
            return true;
        }

        public static UIPrefabBindState GetState(MonoBehaviour host)
        {
            if (host == null)
            {
                return null;
            }

            var guid = UICodeGenUtil.GetStoreKey(host.gameObject);
            if (string.IsNullOrEmpty(guid))
            {
                return null;
            }

            return UIBindStore.instance.Get(guid, UICodeGenUtil.GetHostPath(host));
        }

        public static UIPrefabBindState GetOrCreateState(MonoBehaviour host)
        {
            if (host == null)
            {
                return null;
            }

            var guid = UICodeGenUtil.GetStoreKey(host.gameObject);
            if (string.IsNullOrEmpty(guid))
            {
                return null;
            }

            return UIBindStore.instance.GetOrCreate(guid, UICodeGenUtil.GetHostPath(host));
        }

        public static void EnsureStateFromHost(UIPrefabBindState state, MonoBehaviour host)
        {
            if (state == null || host == null)
            {
                return;
            }

            var type = host.GetType();
            state.ClassName = type.Name;
            state.IsItem = host is UIItem || !(host is UIPanel);
            state.HostPath = UICodeGenUtil.GetHostPath(host);
            if (type.Namespace != null)
            {
                state.NamespaceName = type.Namespace;
            }

            var mono = MonoScript.FromMonoBehaviour(host);
            var scriptPath = AssetDatabase.GetAssetPath(mono);
            if (string.IsNullOrEmpty(scriptPath))
            {
                return;
            }

            if (scriptPath.EndsWith(".Gen.cs", StringComparison.OrdinalIgnoreCase))
            {
                state.GenPath = scriptPath;
                state.ScriptPath = scriptPath.Substring(0, scriptPath.Length - ".Gen.cs".Length) + ".cs";
            }
            else
            {
                state.ScriptPath = scriptPath;
                var dir = Path.GetDirectoryName(scriptPath)?.Replace('\\', '/') ?? "Assets";
                state.GenPath = dir + "/" + type.Name + ".Gen.cs";
            }

            SyncBindsFromHost(state, host);
        }

        public static void EnsureStateFromPanel(UIPrefabBindState state, UIPanel panel)
        {
            EnsureStateFromHost(state, panel);
        }

        public static void CancelPendingAttach(UIPrefabBindState state)
        {
            if (state == null)
            {
                return;
            }

            state.PendingAttach = false;
            UIBindStore.instance.Persist();
        }

        public static void CancelPendingAssign(UIPrefabBindState state)
        {
            if (state == null)
            {
                return;
            }

            state.PendingAssign = false;
            UIBindStore.instance.Persist();
        }

        public static void OpenScript(MonoBehaviour host)
        {
            if (host == null)
            {
                return;
            }

            var mono = MonoScript.FromMonoBehaviour(host);
            if (mono != null)
            {
                AssetDatabase.OpenAsset(mono);
            }
        }

        static void SyncBindsFromHost(UIPrefabBindState state, MonoBehaviour host)
        {
            if (state.Binds == null)
            {
                state.Binds = new List<UIBindEntry>();
            }

            var genFields = ParseGenFields(state.GenPath);
            var so = new SerializedObject(host);

            if (!state.BindsInitialized)
            {
                foreach (var pair in genFields)
                {
                    if (FindBindByField(state, pair.Key) != null)
                    {
                        continue;
                    }

                    state.Binds.Add(new UIBindEntry
                    {
                        FieldName = pair.Key,
                        TypeName = pair.Value,
                        IsGameObject = pair.Value == "UnityEngine.GameObject",
                        HierarchyPath = null,
                    });
                }

                state.BindsInitialized = true;
                UIBindStore.instance.Persist();
            }

            for (var i = 0; i < state.Binds.Count; i++)
            {
                var bind = state.Binds[i];
                var prop = so.FindProperty(bind.FieldName);
                if (prop == null
                    || prop.propertyType != SerializedPropertyType.ObjectReference
                    || prop.objectReferenceValue == null)
                {
                    continue;
                }

                var path = PathFromReference(host.transform, prop.objectReferenceValue);
                if (path != null)
                {
                    bind.HierarchyPath = path;
                }

                var localId = UICodeGenUtil.GetLocalFileId(prop.objectReferenceValue);
                if (localId != 0)
                {
                    bind.LocalFileId = localId;
                }
            }
        }

        static UIBindEntry FindBindByField(UIPrefabBindState state, string fieldName)
        {
            for (var i = 0; i < state.Binds.Count; i++)
            {
                if (state.Binds[i].FieldName == fieldName)
                {
                    return state.Binds[i];
                }
            }

            return null;
        }

        static string PathFromReference(Transform root, Object obj)
        {
            Transform t = null;
            if (obj is GameObject go)
            {
                t = go.transform;
            }
            else if (obj is Component component)
            {
                t = component.transform;
            }

            return t == null ? null : UICodeGenUtil.HierarchyPath(root, t);
        }

        static string _cachedGenPath;
        static System.DateTime _cachedGenTime;
        static Dictionary<string, string> _cachedGenFields;

        static Dictionary<string, string> ParseGenFields(string genPath)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(genPath))
            {
                return result;
            }

            var full = UIScriptWriter.ToFullPath(genPath);
            if (!File.Exists(full))
            {
                return result;
            }

            var writeTime = File.GetLastWriteTimeUtc(full);
            if (_cachedGenPath == genPath && _cachedGenTime == writeTime && _cachedGenFields != null)
            {
                return _cachedGenFields;
            }

            var text = File.ReadAllText(full);
            var matches = GenFieldRegex.Matches(text);
            for (var i = 0; i < matches.Count; i++)
            {
                var field = matches[i].Groups[2].Value;
                var typeName = matches[i].Groups[1].Value;
                if (!result.ContainsKey(field))
                {
                    result.Add(field, typeName);
                }
            }

            _cachedGenPath = genPath;
            _cachedGenTime = writeTime;
            _cachedGenFields = result;
            return result;
        }

        static bool TryNormalizeScriptFolder(string folder, out string normalized, out string error)
        {
            error = null;
            normalized = string.IsNullOrWhiteSpace(folder)
                ? "Assets/Scripts/UI"
                : folder.Replace('\\', '/').Trim().TrimEnd('/');
            if (normalized == "Assets")
            {
                normalized = "Assets/Scripts/UI";
            }

            try
            {
                var assetsRoot = Path.GetFullPath(Application.dataPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var full = UIScriptWriter.ToFullPath(normalized)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var assetsPrefix = assetsRoot + Path.DirectorySeparatorChar;
                if (!string.Equals(full, assetsRoot, StringComparison.OrdinalIgnoreCase)
                    && !full.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    error = "脚本目录必须位于当前项目的 Assets 下。";
                    return false;
                }

                var relative = Path.GetRelativePath(assetsRoot, full).Replace('\\', '/');
                normalized = relative == "."
                    ? "Assets"
                    : "Assets/" + relative.TrimStart('/');
                return true;
            }
            catch (Exception ex)
            {
                error = $"脚本目录无效: {ex.Message}";
                return false;
            }
        }

        static bool ValidateExistingFiles(
            string scriptPath,
            string genPath,
            string namespaceName,
            string className,
            bool isItem,
            out string error)
        {
            error = null;
            if (File.Exists(UIScriptWriter.ToFullPath(scriptPath)))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
                var type = script != null ? script.GetClass() : null;
                if (type == null)
                {
                    error = $"已有脚本无法解析类型，请先修复编译错误或更换类名: {scriptPath}";
                    return false;
                }

                var expectedBase = isItem ? typeof(UIItem) : typeof(UIPanel);
                if (type.Name != className
                    || !string.Equals(type.Namespace ?? "", namespaceName, StringComparison.Ordinal)
                    || !expectedBase.IsAssignableFrom(type)
                    || type.IsAbstract)
                {
                    error =
                        $"已有脚本类型不匹配: {scriptPath}\n"
                        + $"期望可实例化的 {namespaceName}.{className} 继承 {expectedBase.Name}，实际为 {type.FullName}。";
                    return false;
                }

                var scriptText = File.ReadAllText(UIScriptWriter.ToFullPath(scriptPath));
                if (!HasPartialClassDeclaration(scriptText, className))
                {
                    error = $"已有脚本必须将 {className} 声明为 partial class: {scriptPath}";
                    return false;
                }
            }

            var genFullPath = UIScriptWriter.ToFullPath(genPath);
            if (!File.Exists(genFullPath))
            {
                return true;
            }

            var text = File.ReadAllText(genFullPath);
            if (!text.Contains("// UIFrame 生成")
                || !HasNamespaceDeclaration(text, namespaceName)
                || !HasPartialClassDeclaration(text, className))
            {
                error = $"已有 .Gen.cs 不是匹配的 UIFrame 生成文件: {genPath}";
                return false;
            }

            return true;
        }

        static bool HasNamespaceDeclaration(string text, string namespaceName)
        {
            return Regex.IsMatch(
                text ?? "",
                @"\bnamespace\s+" + Regex.Escape(namespaceName) + @"\s*(?:\r?\n|\{)");
        }

        static bool HasPartialClassDeclaration(string text, string className)
        {
            return Regex.IsMatch(
                text ?? "",
                @"\bpartial\s+class\s+" + Regex.Escape(className) + @"\b");
        }
    }
}
