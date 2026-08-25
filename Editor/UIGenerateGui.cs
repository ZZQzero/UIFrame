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

            // GameObject 标题保持原生，不画任何生成/绑定 UI。
            if (editor.target is GameObject)
            {
                return;
            }

            if (editor.target is Component component)
            {
                // 尚未生成脚本时，生成表单挂在根 RectTransform 上。
                if (component is RectTransform rect
                    && UICodeGenUtil.IsUiPrefabRoot(rect.gameObject)
                    && UICodeGenUtil.FindBindHostOn(rect.gameObject) == null)
                {
                    DrawRootGenerate(rect.gameObject);
                }

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

        static void DrawRootGenerate(GameObject go)
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
            _folder = EditorGUILayout.TextField("目录", _folder);
            if (GUILayout.Button("…", EditorStyles.miniButton, GUILayout.Width(24)))
            {
                var picked = EditorUtility.OpenFolderPanel("脚本目录", Application.dataPath, "");
                if (!string.IsNullOrEmpty(picked) && picked.Replace('\\', '/').Contains("/Assets"))
                {
                    var index = picked.Replace('\\', '/').IndexOf("/Assets");
                    _folder = picked.Replace('\\', '/').Substring(index + 1);
                }
            }

            EditorGUILayout.EndHorizontal();
            _namespaceName = EditorGUILayout.TextField("命名空间", _namespaceName);

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
            }

            EditorGUILayout.EndVertical();
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
}
