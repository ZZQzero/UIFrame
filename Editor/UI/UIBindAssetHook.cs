using UnityEditor;

namespace UIFrame.Editor
{
    sealed class UIBindAssetHook : AssetPostprocessor
    {
        static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (!UIBindActions.ClearPendingForDeletedScripts(deletedAssets))
            {
                return;
            }

            EditorApplication.delayCall += UICompileHook.ProcessJobs;
        }
    }
}
