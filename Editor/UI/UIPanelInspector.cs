using UnityEditor;

namespace UIFrame.Editor
{
    [CustomEditor(typeof(UIPanel), true)]
    sealed class UIPanelInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            UIBindInspectorGui.Draw((UIPanel)target, showOpenScript: true);
            DrawDefaultInspector();
        }
    }

    [CustomEditor(typeof(UIItem), true)]
    sealed class UIItemInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            UIBindInspectorGui.Draw((UIItem)target, showOpenScript: true);
            DrawDefaultInspector();
        }
    }
}
