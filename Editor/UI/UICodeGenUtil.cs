using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UIFrame.Editor
{
    internal static class UICodeGenUtil
    {
        const string EncodedPathPrefix = "v2:";

        static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
            "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
            "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
            "virtual", "void", "volatile", "while",
        };
        static readonly Dictionary<string, System.Type> TypeCache =
            new Dictionary<string, System.Type>(System.StringComparer.Ordinal);

        public static bool IsUiPrefabRoot(GameObject go)
        {
            if (go == null || go.GetComponent<RectTransform>() == null)
            {
                return false;
            }

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot == go)
            {
                return true;
            }

            if (PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                return true;
            }

            if (PrefabUtility.IsPartOfPrefabAsset(go))
            {
                return go.transform.parent == null;
            }

            var path = GetPrefabAssetPath(go);
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".prefab"))
            {
                return go.transform.parent == null;
            }

            return go.GetComponent<Canvas>() != null;
        }

        public static string GetPrefabAssetPath(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null)
            {
                var stageRoot = stage.prefabContentsRoot;
                if (go == stageRoot || go.transform.IsChildOf(stageRoot.transform))
                {
                    return stage.assetPath;
                }
            }

            var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            if (!string.IsNullOrEmpty(path))
            {
                return path;
            }

            path = AssetDatabase.GetAssetPath(go);
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".prefab"))
            {
                return path;
            }

            return null;
        }

        public static string GetPrefabGuid(GameObject go)
        {
            var path = GetPrefabAssetPath(go);
            return string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
        }

        public static string GetStoreKey(GameObject go)
        {
            var guid = GetPrefabGuid(go);
            if (!string.IsNullOrEmpty(guid))
            {
                return guid;
            }

            return go == null ? null : GlobalObjectId.GetGlobalObjectIdSlow(go).ToString();
        }

        public static string GetHostPath(MonoBehaviour host)
        {
            if (host == null)
            {
                return "";
            }

            var go = host.gameObject;
            if (IsOnPrefabRoot(go))
            {
                return "";
            }

            var root = GetPrefabRoot(go);
            if (root == null || root == go)
            {
                return "";
            }

            return HierarchyPath(root.transform, go.transform) ?? go.name;
        }

        public static bool IsOnPrefabRoot(GameObject go)
        {
            if (go == null)
            {
                return false;
            }

            if (IsUiPrefabRoot(go))
            {
                return true;
            }

            var nested = PrefabUtility.GetNearestPrefabInstanceRoot(go);
            if (nested == go)
            {
                return true;
            }

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot == go)
            {
                return true;
            }

            return go.transform.parent == null;
        }

        public static GameObject GetPrefabRoot(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && stage.prefabContentsRoot != null)
            {
                var stageRoot = stage.prefabContentsRoot;
                if (go == stageRoot || go.transform.IsChildOf(stageRoot.transform))
                {
                    var nested = PrefabUtility.GetNearestPrefabInstanceRoot(go);
                    if (nested != null && nested != stageRoot)
                    {
                        return nested;
                    }

                    return stageRoot;
                }
            }

            var nearest = PrefabUtility.GetNearestPrefabInstanceRoot(go);
            if (nearest != null)
            {
                return nearest;
            }

            var t = go.transform;
            while (t.parent != null)
            {
                t = t.parent;
            }

            return t.gameObject;
        }

        public static UIPanel FindNearestPanel(Component component)
        {
            return FindNearestBindHost(component) as UIPanel;
        }

        /// <summary>向上找最近的绑定宿主，包含组件所在节点（Item 子节点绑到 Item）。</summary>
        public static MonoBehaviour FindNearestBindHost(Component component)
        {
            if (component == null)
            {
                return null;
            }

            var t = component.transform;
            while (t != null)
            {
                var host = FindBindHostOn(t.gameObject);
                if (host != null)
                {
                    return host;
                }

                t = t.parent;
            }

            return null;
        }

        public static MonoBehaviour FindNearestBindHost(GameObject go)
        {
            return go == null ? null : FindNearestBindHost(go.transform);
        }

        /// <summary>
        /// 此组件作为字段要写进哪一个脚本。跳过自身，所以 Panel 下的 Item 脚本可以加到 Panel 上。
        /// </summary>
        public static MonoBehaviour FindBindTargetHost(Component component)
        {
            if (component == null)
            {
                return null;
            }

            var t = component.transform;
            while (t != null)
            {
                var host = FindBindHostOn(t.gameObject);
                if (host != null && host != component)
                {
                    return host;
                }

                t = t.parent;
            }

            return null;
        }

        public static UIPanel FindOuterPanel(Component component)
        {
            if (component == null)
            {
                return null;
            }

            CollectBindTargetHosts(component, OuterHosts);
            for (var i = 0; i < OuterHosts.Count; i++)
            {
                if (OuterHosts[i] is UIPanel panel)
                {
                    return panel;
                }
            }

            return null;
        }

        static readonly List<MonoBehaviour> OuterHosts = new List<MonoBehaviour>(4);

        public static void CollectBindTargetHosts(Component component, List<MonoBehaviour> results)
        {
            results.Clear();
            if (component == null)
            {
                return;
            }

            var t = component.transform;
            while (t != null)
            {
                var host = FindBindHostOn(t.gameObject);
                if (host != null && host != component && !results.Contains(host))
                {
                    results.Add(host);
                }

                t = t.parent;
            }
        }

        public static MonoBehaviour FindBindHostOn(GameObject go)
        {
            if (go == null)
            {
                return null;
            }

            var panel = go.GetComponent<UIPanel>();
            if (panel != null)
            {
                return panel;
            }

            var item = go.GetComponent<UIItem>();
            if (item != null)
            {
                return item;
            }

            var behaviours = go.GetComponents<MonoBehaviour>();
            var key = GetStoreKey(go);
            if (!string.IsNullOrEmpty(key))
            {
                for (var i = 0; i < behaviours.Length; i++)
                {
                    var mb = behaviours[i];
                    if (mb == null)
                    {
                        continue;
                    }

                    var state = UIBindStore.instance.Get(key, GetHostPath(mb))
                                ?? UIBindStore.instance.Get(key, "");
                    if (state != null && state.ClassName == mb.GetType().Name)
                    {
                        return mb;
                    }
                }
            }

            for (var i = 0; i < behaviours.Length; i++)
            {
                var mb = behaviours[i];
                if (IsGeneratedBindHost(mb))
                {
                    return mb;
                }
            }

            return null;
        }

        public static bool IsGeneratedBindHost(MonoBehaviour mb)
        {
            if (mb == null)
            {
                return false;
            }

            if (mb is UIPanel || mb is UIItem)
            {
                return true;
            }

            var key = GetStoreKey(mb.gameObject);
            var state = string.IsNullOrEmpty(key)
                ? null
                : UIBindStore.instance.Get(key, GetHostPath(mb)) ?? UIBindStore.instance.Get(key, "");
            if (state != null && state.ClassName == mb.GetType().Name)
            {
                return true;
            }

            var script = MonoScript.FromMonoBehaviour(mb);
            var path = AssetDatabase.GetAssetPath(script);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", System.StringComparison.Ordinal))
            {
                return false;
            }

            if (path.EndsWith(".Gen.cs", System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var dir = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "Assets";
            var genPath = dir + "/" + mb.GetType().Name + ".Gen.cs";
            return System.IO.File.Exists(UIScriptWriter.ToFullPath(genPath))
                   || AssetDatabase.LoadAssetAtPath<MonoScript>(genPath) != null;
        }

        public static bool GuessIsItem(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName))
            {
                return false;
            }

            var id = ToIdentifier(prefabName);
            return id.EndsWith("Item", System.StringComparison.Ordinal)
                   || id.EndsWith("Cell", System.StringComparison.Ordinal);
        }

        public static string ToClassName(string prefabName)
        {
            return ToClassName(prefabName, GuessIsItem(prefabName));
        }

        public static string ToClassName(string prefabName, bool isItem)
        {
            var id = ToPascalIdentifier(prefabName);
            if (!isItem && !id.EndsWith("Panel"))
            {
                id += "Panel";
            }

            return id;
        }

        public static string ToPascalIdentifier(string raw)
        {
            var id = ToIdentifier(raw);
            if (string.IsNullOrEmpty(id))
            {
                return "Node";
            }

            var start = 0;
            while (start < id.Length && id[start] == '_')
            {
                start++;
            }

            if (start >= id.Length)
            {
                return "Node";
            }

            if (char.IsLower(id[start]))
            {
                id = id.Substring(0, start)
                     + char.ToUpperInvariant(id[start])
                     + id.Substring(start + 1);
            }

            return id;
        }

        public static string HierarchyPath(Transform root, Transform target)
        {
            if (root == null || target == null || target == root)
            {
                return string.Empty;
            }

            var parts = new List<string>(8);
            var t = target;
            while (t != null && t != root)
            {
                parts.Add(EncodePathSegment(t));
                t = t.parent;
            }

            if (t != root)
            {
                return null;
            }

            parts.Reverse();
            return EncodedPathPrefix + string.Join("/", parts);
        }

        public static Transform FindByPath(Transform root, string path)
        {
            if (root == null)
            {
                return null;
            }

            // null = 尚未定位；空字符串 = 绑定宿主自己。
            if (path == null)
            {
                return null;
            }

            if (path.Length == 0)
            {
                return root;
            }

            var encoded = path.StartsWith(EncodedPathPrefix, System.StringComparison.Ordinal);
            var rawPath = encoded ? path.Substring(EncodedPathPrefix.Length) : path;
            var current = root;
            var parts = rawPath.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                current = FindChild(current, parts[i], encoded);
                if (current == null)
                {
                    break;
                }
            }

            if (current != null)
            {
                return current;
            }

            var unityFind = root.Find(StripPathIndices(path));
            if (unityFind != null)
            {
                return unityFind;
            }

            return FindDeepByName(root, LeafName(path));
        }

        public static Transform FindBindNode(Transform host, UIBindEntry bind)
        {
            if (host == null || bind == null)
            {
                return null;
            }

            if (bind.LocalFileId != 0)
            {
                var byId = FindByLocalFileId(host, bind.LocalFileId);
                if (byId != null)
                {
                    return byId;
                }
            }

            if (bind.HierarchyPath != null)
            {
                var byPath = FindByPath(host, bind.HierarchyPath);
                if (byPath != null)
                {
                    return byPath;
                }
            }

            if (!bind.IsGameObject)
            {
                var byType = FindByComponentType(host, bind.TypeName, LeafName(bind.HierarchyPath));
                if (byType != null)
                {
                    return byType;
                }
            }

            return bind.HierarchyPath != null
                ? FindDeepByName(host, LeafName(bind.HierarchyPath))
                : null;
        }

        public static long GetLocalFileId(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return 0;
            }

            return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out _, out long id) ? id : 0;
        }

        static Transform FindByLocalFileId(Transform host, long localId)
        {
            var components = host.GetComponentsInChildren<Component>(true);
            for (var i = 0; i < components.Length; i++)
            {
                var c = components[i];
                if (c == null)
                {
                    continue;
                }

                if (GetLocalFileId(c) == localId)
                {
                    return c.transform;
                }

                if (GetLocalFileId(c.gameObject) == localId)
                {
                    return c.transform;
                }
            }

            return null;
        }

        static Transform FindByComponentType(Transform host, string typeName, string objectName)
        {
            var type = FindType(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                return null;
            }

            var comps = host.GetComponentsInChildren(type, true);
            Transform fallback = null;
            var fallbackCount = 0;
            for (var i = 0; i < comps.Length; i++)
            {
                var c = comps[i] as Component;
                if (c == null || c.transform == host)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(objectName) && c.gameObject.name == objectName)
                {
                    return c.transform;
                }

                fallback = c.transform;
                fallbackCount++;
            }

            return fallbackCount == 1 ? fallback : null;
        }

        static Transform FindDeepByName(Transform host, string name)
        {
            if (host == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            var transforms = host.GetComponentsInChildren<Transform>(true);
            Transform match = null;
            var count = 0;
            for (var i = 0; i < transforms.Length; i++)
            {
                var t = transforms[i];
                if (t == null || t == host || t.name != name)
                {
                    continue;
                }

                match = t;
                count++;
            }

            return count == 1 ? match : null;
        }

        static string LeafName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            var encoded = path.StartsWith(EncodedPathPrefix, System.StringComparison.Ordinal);
            var rawPath = encoded ? path.Substring(EncodedPathPrefix.Length) : path;
            var slash = rawPath.LastIndexOf('/');
            var leaf = slash >= 0 ? rawPath.Substring(slash + 1) : rawPath;
            ParsePathSegment(leaf, encoded, out var name, out _);
            return name;
        }

        static string StripPathIndices(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            var encoded = path.StartsWith(EncodedPathPrefix, System.StringComparison.Ordinal);
            var rawPath = encoded ? path.Substring(EncodedPathPrefix.Length) : path;
            if (rawPath.IndexOf('#') < 0 && (!encoded || rawPath.IndexOf('%') < 0))
            {
                return rawPath;
            }

            var parts = rawPath.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                ParsePathSegment(parts[i], encoded, out var name, out _);
                parts[i] = name;
            }

            return string.Join("/", parts);
        }

        public static System.Type FindType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            if (TypeCache.TryGetValue(typeName, out var cached))
            {
                return cached;
            }

            var type = System.Type.GetType(typeName);
            if (type != null)
            {
                TypeCache[typeName] = type;
                return type;
            }

            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                type = assemblies[i].GetType(typeName);
                if (type != null)
                {
                    TypeCache[typeName] = type;
                    return type;
                }

                var dot = typeName.LastIndexOf('.');
                while (dot > 0)
                {
                    var nested = typeName.Substring(0, dot)
                                 + "+"
                                 + typeName.Substring(dot + 1).Replace('.', '+');
                    type = assemblies[i].GetType(nested);
                    if (type != null)
                    {
                        TypeCache[typeName] = type;
                        return type;
                    }

                    dot = typeName.LastIndexOf('.', dot - 1);
                }
            }

            TypeCache[typeName] = null;
            return null;
        }

        static string EncodePathSegment(Transform t)
        {
            var name = EncodePathName(t.name);
            var parent = t.parent;
            if (parent == null)
            {
                return name;
            }

            var same = 0;
            var index = 0;
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name != t.name)
                {
                    continue;
                }

                if (child == t)
                {
                    index = same;
                }

                same++;
            }

            return same > 1 ? name + "#" + index : name;
        }

        static Transform FindChild(Transform parent, string segment, bool encoded)
        {
            ParsePathSegment(segment, encoded, out var name, out var index);
            var same = 0;
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child.name != name)
                {
                    continue;
                }

                if (index < 0 || same == index)
                {
                    return child;
                }

                same++;
            }

            return null;
        }

        static void ParsePathSegment(string segment, bool encoded, out string name, out int index)
        {
            index = -1;
            name = segment;
            if (string.IsNullOrEmpty(segment))
            {
                return;
            }

            var hash = segment.LastIndexOf('#');
            if (hash <= 0 || hash == segment.Length - 1)
            {
                name = encoded ? DecodePathName(segment) : segment;
                return;
            }

            if (int.TryParse(segment.Substring(hash + 1), out var parsed) && parsed >= 0)
            {
                var rawName = segment.Substring(0, hash);
                name = encoded ? DecodePathName(rawName) : rawName;
                index = parsed;
                return;
            }

            name = encoded ? DecodePathName(segment) : segment;
        }

        static string EncodePathName(string name)
        {
            return (name ?? "")
                .Replace("%", "%25")
                .Replace("/", "%2F")
                .Replace("#", "%23");
        }

        static string DecodePathName(string name)
        {
            return (name ?? "")
                .Replace("%2F", "/")
                .Replace("%2f", "/")
                .Replace("%23", "#")
                .Replace("%25", "%");
        }

        public static string ToIdentifier(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return "Node";
            }

            var sb = new StringBuilder(raw.Length);
            for (var i = 0; i < raw.Length; i++)
            {
                var ch = raw[i];
                if (char.IsLetterOrDigit(ch) || ch == '_')
                {
                    sb.Append(ch);
                }
            }

            if (sb.Length == 0)
            {
                return "Node";
            }

            if (char.IsDigit(sb[0]))
            {
                sb.Insert(0, '_');
            }

            var id = sb.ToString();
            if (Keywords.Contains(id))
            {
                return "_" + id;
            }

            return id;
        }

        public static bool IsValidNamespace(string namespaceName)
        {
            if (string.IsNullOrWhiteSpace(namespaceName))
            {
                return false;
            }

            var parts = namespaceName.Split('.');
            for (var i = 0; i < parts.Length; i++)
            {
                if (!IsValidIdentifier(parts[i]))
                {
                    return false;
                }
            }

            return true;
        }

        static bool IsValidIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || Keywords.Contains(value))
            {
                return false;
            }

            if (!(char.IsLetter(value[0]) || value[0] == '_'))
            {
                return false;
            }

            for (var i = 1; i < value.Length; i++)
            {
                if (!char.IsLetterOrDigit(value[i]) && value[i] != '_')
                {
                    return false;
                }
            }

            return true;
        }

        public static string ToCsTypeName(System.Type type)
        {
            if (type == null)
            {
                return "UnityEngine.Object";
            }

            return type.FullName.Replace('+', '.');
        }

        public static bool IsBlockedComponent(Component component)
        {
            if (component == null)
            {
                return true;
            }

            return component is CanvasRenderer
                   || component is Canvas
                   || component is UnityEngine.UI.GraphicRaycaster;
        }

        public static string MakeFieldName(UIPrefabBindState state, string nodeName, bool isGameObject, System.Type componentType)
        {
            var baseName = ToCamelCaseIdentifier(nodeName);
            if (isGameObject)
            {
                baseName += "Obj";
            }
            else if (typeof(RectTransform).IsAssignableFrom(componentType))
            {
                baseName += "Trans";
            }

            var name = baseName;
            var index = 1;
            while (ContainsField(state, name))
            {
                name = baseName + index;
                index++;
            }

            return name;
        }

        /// <summary>节点名清洗后转 camelCase，供 private 字段使用。</summary>
        public static string ToCamelCaseIdentifier(string raw)
        {
            var id = ToIdentifier(raw);
            if (string.IsNullOrEmpty(id))
            {
                return "node";
            }

            var start = 0;
            while (start < id.Length && id[start] == '_')
            {
                start++;
            }

            if (start >= id.Length)
            {
                return "node";
            }

            if (char.IsUpper(id[start]))
            {
                id = id.Substring(0, start)
                     + char.ToLowerInvariant(id[start])
                     + id.Substring(start + 1);
            }

            if (Keywords.Contains(id))
            {
                return "_" + id;
            }

            return id;
        }

        static bool ContainsField(UIPrefabBindState state, string fieldName)
        {
            if (state?.Binds == null)
            {
                return false;
            }

            for (var i = 0; i < state.Binds.Count; i++)
            {
                if (state.Binds[i].FieldName == fieldName)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
