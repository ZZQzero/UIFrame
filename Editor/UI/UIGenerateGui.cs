using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UIFrame.Editor
{
    [InitializeOnLoad]
    static class UIGenerateGui
    {
        static string _className = "";
        static string _folder;
        static string _namespaceName;
        static string _lastKey;
        static bool _isItem;
        static bool _itemTouched;
        static readonly List<MonoBehaviour> BindTargets = new List<MonoBehaviour>(4);

        static UIGenerateGui()
        {
            UnityEditor.Editor.finishedDefaultHeaderGUI += OnHeaderGUI;
        }

        static void OnHeaderGUI(UnityEditor.Editor editor)
        {
            if (editor.targets.Length != 1)
            {
                return;
            }

            // Unity 6 Inspector 对 Component 走 UITK，finishedDefaultHeaderGUI 经常不触发。
            // GameObject 标题仍是 IMGUI，生成表单（含目录选择）必须画在这里。
            if (editor.target is GameObject go)
            {
                if (UICodeGenUtil.IsUiPrefabRoot(go)
                    && UICodeGenUtil.FindBindHostOn(go) == null)
                {
                    DrawRootGenerate(go);
                }

                return;
            }

            if (editor.target is Component component)
            {
                if (component is MonoBehaviour host
                    && host is not UIPanel
                    && host is not UIItem
                    && UICodeGenUtil.IsGeneratedBindHost(host))
                {
                    UIBindInspectorGui.Draw(host, showOpenScript: true);
                }

                DrawAddButtons(component);
            }
        }

        public static void OpenGenerateWindow(GameObject go)
        {
            UIGenerateWindow.Open(go);
        }

        internal static void DrawRootGenerate(GameObject go)
        {
            var host = UICodeGenUtil.FindBindHostOn(go);
            var key = UICodeGenUtil.GetStoreKey(go);
            var state = string.IsNullOrEmpty(key) ? null : UIBindStore.instance.Get(key);

            EditorGUILayout.BeginVertical();
            GUILayout.Space(4);
            EditorGUILayout.LabelField("UIFrame", EditorStyles.boldLabel);

            if (state != null && state.PendingAttach)
            {
                EditorGUILayout.HelpBox($"正在编译并挂载 {state.ClassName}…", MessageType.Info);
                if (GUILayout.Button("取消等待"))
                {
                    UIBindActions.CancelPendingAttach(state);
                }

                EditorGUILayout.EndVertical();
                return;
            }

            if (host != null)
            {
                EditorGUILayout.EndVertical();
                return;
            }

            if (key != _lastKey)
            {
                _lastKey = key;
                _itemTouched = false;
                _isItem = UICodeGenUtil.GuessIsItem(go.name);
                _className = UICodeGenUtil.ToClassName(go.name, _isItem);
                _folder = UICodeGenPrefs.Folder;
                _namespaceName = UICodeGenPrefs.NamespaceName;
            }

            EditorGUI.BeginChangeCheck();
            var nextItem = EditorGUILayout.ToggleLeft("Item/Cell（继承 UIItem，不进 UI 栈）", _isItem);
            if (EditorGUI.EndChangeCheck())
            {
                _isItem = nextItem;
                _itemTouched = true;
                _className = UICodeGenUtil.ToClassName(go.name, _isItem);
            }
            else if (!_itemTouched)
            {
                _isItem = UICodeGenUtil.GuessIsItem(go.name);
            }

            _className = EditorGUILayout.TextField("类名", _className);

            EditorGUILayout.BeginHorizontal();
            EditorGUI.BeginChangeCheck();
            _folder = EditorGUILayout.TextField("目录", _folder);
            if (EditorGUI.EndChangeCheck())
            {
                UICodeGenPrefs.Folder = _folder;
            }

            var folderAsset = AssetDatabase.LoadAssetAtPath<DefaultAsset>(_folder);
            EditorGUI.BeginChangeCheck();
            var nextFolder = (DefaultAsset)EditorGUILayout.ObjectField(
                folderAsset,
                typeof(DefaultAsset),
                false,
                GUILayout.Width(36));
            if (EditorGUI.EndChangeCheck() && nextFolder != null)
            {
                var assetPath = AssetDatabase.GetAssetPath(nextFolder);
                if (AssetDatabase.IsValidFolder(assetPath))
                {
                    _folder = assetPath;
                    UICodeGenPrefs.Folder = assetPath;
                }
            }

            if (GUILayout.Button("…", EditorStyles.miniButton, GUILayout.Width(24)))
            {
                var start = string.IsNullOrWhiteSpace(_folder)
                    ? Application.dataPath
                    : UIScriptWriter.ToFullPath(_folder);
                EditorApplication.delayCall += () => PickScriptFolder(start);
            }

            EditorGUILayout.EndHorizontal();

            EditorGUI.BeginChangeCheck();
            _namespaceName = EditorGUILayout.TextField("命名空间", _namespaceName);
            if (EditorGUI.EndChangeCheck())
            {
                UICodeGenPrefs.NamespaceName = _namespaceName;
            }

            if (_isItem)
            {
                EditorGUILayout.HelpBox("将生成 UIItem + .Gen.cs，不注册到 UIFrame。绑定列表在脚本 Inspector 上。", MessageType.Info);
            }

            if (GUILayout.Button("生成脚本"))
            {
                if (!UIBindActions.TryGenerate(go, _className, _folder, _namespaceName, _isItem, out var error))
                {
                    EditorUtility.DisplayDialog("UIFrame", error, "确定");
                }
                else
                {
                    UIGenerateWindow.CloseIfOpen();
                }
            }

            EditorGUILayout.EndVertical();
        }

        static void PickScriptFolder(string start)
        {
            var picked = EditorUtility.OpenFolderPanel("脚本目录", start, "");
            if (string.IsNullOrEmpty(picked))
            {
                return;
            }

            if (!UIBindActions.TryNormalizeScriptFolder(picked, out var folder, out var error))
            {
                EditorUtility.DisplayDialog("UIFrame", error, "确定");
                return;
            }

            _folder = folder;
            UICodeGenPrefs.Folder = folder;
        }

        static void DrawAddButtons(Component component)
        {
            if (UICodeGenUtil.IsBlockedComponent(component))
            {
                return;
            }

            UICodeGenUtil.CollectBindTargetHosts(component, BindTargets);
            if (BindTargets.Count == 0)
            {
                return;
            }

            for (var i = 0; i < BindTargets.Count; i++)
            {
                var host = BindTargets[i];
                var state = UIBindActions.GetState(host);
                if (state != null && state.PendingAttach)
                {
                    continue;
                }

                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button($"添加到 {host.GetType().Name}", EditorStyles.miniButton))
                {
                    if (!UIBindActions.TryAddComponent(component, host, out var error))
                    {
                        EditorUtility.DisplayDialog("UIFrame", error, "确定");
                    }
                }

                if (GUILayout.Button("+Obj", EditorStyles.miniButton, GUILayout.Width(44)))
                {
                    if (!UIBindActions.TryAddGameObject(component, host, out var error))
                    {
                        EditorUtility.DisplayDialog("UIFrame", error, "确定");
                    }
                }

                EditorGUILayout.EndHorizontal();
            }
        }
    }

    sealed class UIGenerateWindow : EditorWindow
    {
        GameObject _root;

        public static void Open(GameObject go)
        {
            var window = GetWindow<UIGenerateWindow>(true, "生成 UI 脚本", true);
            window._root = go;
            window.minSize = new Vector2(420, 200);
            window.ShowUtility();
            window.Focus();
        }

        public static void CloseIfOpen()
        {
            var windows = Resources.FindObjectsOfTypeAll<UIGenerateWindow>();
            for (var i = 0; i < windows.Length; i++)
            {
                windows[i].Close();
            }
        }

        void OnGUI()
        {
            if (_root == null)
            {
                EditorGUILayout.HelpBox("Prefab 已丢失。", MessageType.Warning);
                return;
            }

            UIGenerateGui.DrawRootGenerate(_root);
        }
    }
}
