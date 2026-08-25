using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UIFrame.Editor
{
    static class UIBindInspectorGui
    {
        static readonly HashSet<int> SyncedHosts = new HashSet<int>();

        public static void Draw(MonoBehaviour host, bool showOpenScript)
        {
            var state = UIBindActions.GetOrCreateState(host);
            if (state != null && SyncedHosts.Add(host.GetInstanceID()))
            {
                UIBindActions.EnsureStateFromHost(state, host);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("UIFrame 绑定", EditorStyles.boldLabel);

            if (state != null && state.PendingAssign)
            {
                EditorGUILayout.HelpBox("已写入脚本，等待编译后回填引用…", MessageType.Info);
                if (GUILayout.Button("取消回填"))
                {
                    UIBindActions.CancelPendingAssign(state);
                }
            }

            DrawBindList(state);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(state == null || state.PendingAttach))
            {
                if (GUILayout.Button("写入脚本"))
                {
                    if (!UIBindActions.TryWriteGen(host, out var error))
                    {
                        EditorUtility.DisplayDialog("UIFrame", error, "确定");
                    }
                }
            }

            if (showOpenScript && GUILayout.Button("打开脚本", GUILayout.Width(80)))
            {
                UIBindActions.OpenScript(host);
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(6);
        }

        static void DrawBindList(UIPrefabBindState state)
        {
            if (state == null || state.Binds == null || state.Binds.Count == 0)
            {
                EditorGUILayout.HelpBox("在子节点组件上点「添加到…」或右键添加。Panel 下的 Item 脚本可以加到外层 Panel。不会立刻编译。", MessageType.None);
                return;
            }

            for (var i = 0; i < state.Binds.Count; i++)
            {
                var bind = state.Binds[i];
                EditorGUILayout.BeginHorizontal();
                var label = bind.IsGameObject
                    ? $"{bind.FieldName}  (GameObject)"
                    : $"{bind.FieldName}  ({ShortType(bind.TypeName)})";
                EditorGUILayout.LabelField(label, GUILayout.MinWidth(80));
                EditorGUILayout.LabelField(
                    bind.HierarchyPath == null ? "(未定位)" :
                    bind.HierarchyPath.Length == 0 ? "." : bind.HierarchyPath,
                    EditorStyles.miniLabel);
                if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(22)))
                {
                    UIBindActions.RemoveBind(state, i);
                    GUIUtility.ExitGUI();
                }

                EditorGUILayout.EndHorizontal();
            }
        }

        static string ShortType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return "";
            }

            var index = typeName.LastIndexOf('.');
            return index >= 0 ? typeName.Substring(index + 1) : typeName;
        }
    }
}
