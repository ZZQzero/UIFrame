using UnityEditor;
using UnityEngine;

namespace UIFrame.Editor
{
    static class UIContextMenus
    {
        [MenuItem("Assets/UIFrame/生成脚本", false, 2000)]
        static void GenerateFromProject()
        {
            var go = Selection.activeGameObject;
            if (go == null)
            {
                var path = AssetDatabase.GetAssetPath(Selection.activeObject);
                go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }

            if (go == null)
            {
                return;
            }

            Generate(go);
        }

        [MenuItem("Assets/UIFrame/生成脚本", true)]
        static bool GenerateFromProjectValidate()
        {
            return CanGenerate(GetSelectedPrefabRoot());
        }

        [MenuItem("CONTEXT/RectTransform/UIFrame 生成脚本", false, 2100)]
        static void GenerateFromRectTransform(MenuCommand command)
        {
            var rect = command.context as RectTransform;
            var go = rect != null ? rect.gameObject : null;
            if (go == null)
            {
                return;
            }

            Generate(go);
        }

        static void Generate(GameObject go)
        {
            UIGenerateGui.OpenGenerateWindow(go);
        }

        [MenuItem("CONTEXT/RectTransform/UIFrame 生成脚本", true)]
        static bool GenerateFromRectTransformValidate(MenuCommand command)
        {
            var rect = command.context as RectTransform;
            return CanGenerate(rect != null ? rect.gameObject : null);
        }

        [MenuItem("CONTEXT/Component/添加到 UI 脚本/此组件", false, 2200)]
        static void AddThisComponent(MenuCommand command)
        {
            var component = command.context as Component;
            if (!UIBindActions.TryAddComponent(component, out var error))
            {
                EditorUtility.DisplayDialog("UIFrame", error, "确定");
            }
        }

        [MenuItem("CONTEXT/Component/添加到 UI 脚本/此组件", true)]
        static bool AddThisComponentValidate(MenuCommand command)
        {
            return CanBind(command.context as Component, allowBlocked: false);
        }

        [MenuItem("CONTEXT/Component/添加到 UI 脚本/GameObject", false, 2201)]
        static void AddThisGameObject(MenuCommand command)
        {
            var component = command.context as Component;
            if (!UIBindActions.TryAddGameObject(component, out var error))
            {
                EditorUtility.DisplayDialog("UIFrame", error, "确定");
            }
        }

        [MenuItem("CONTEXT/Component/添加到 UI 脚本/GameObject", true)]
        static bool AddThisGameObjectValidate(MenuCommand command)
        {
            return CanBind(command.context as Component, allowBlocked: true);
        }

        [MenuItem("CONTEXT/Component/添加到 UI 脚本/外层面板", false, 2202)]
        static void AddToOuterPanel(MenuCommand command)
        {
            var component = command.context as Component;
            var panel = UICodeGenUtil.FindOuterPanel(component);
            if (!UIBindActions.TryAddComponent(component, panel, out var error))
            {
                EditorUtility.DisplayDialog("UIFrame", error, "确定");
            }
        }

        [MenuItem("CONTEXT/Component/添加到 UI 脚本/外层面板", true)]
        static bool AddToOuterPanelValidate(MenuCommand command)
        {
            var component = command.context as Component;
            if (component == null || UICodeGenUtil.IsBlockedComponent(component))
            {
                return false;
            }

            var panel = UICodeGenUtil.FindOuterPanel(component);
            var nearest = UICodeGenUtil.FindBindTargetHost(component);
            return panel != null && panel != nearest;
        }

        static GameObject GetSelectedPrefabRoot()
        {
            var go = Selection.activeGameObject;
            if (go == null)
            {
                var path = AssetDatabase.GetAssetPath(Selection.activeObject);
                go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            }

            return go;
        }

        static bool CanGenerate(GameObject go)
        {
            return go != null
                   && UICodeGenUtil.IsUiPrefabRoot(go)
                   && UICodeGenUtil.FindBindHostOn(go) == null;
        }

        static bool CanBind(Component component, bool allowBlocked)
        {
            if (component == null)
            {
                return false;
            }

            if (!allowBlocked && UICodeGenUtil.IsBlockedComponent(component))
            {
                return false;
            }

            var host = UICodeGenUtil.FindBindTargetHost(component);
            if (host == null)
            {
                return false;
            }

            var state = UIBindActions.GetState(host);
            return state == null || !state.PendingAttach;
        }
    }
}
