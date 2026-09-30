using System;
using UnityEditor;
using UnityEngine;

namespace UIFrame.Sqlite.Editor
{
    [InitializeOnLoad]
    internal static class SqliteEditorLifecycle
    {
        static SqliteEditorLifecycle()
        {
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
            EditorApplication.playModeStateChanged += OnPlayMode;
        }
        static void OnPlayMode(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
                Stop();
            if (state == PlayModeStateChange.EnteredEditMode)
                SqliteRuntime.StartEditorLifetime();
        }
        static void Stop()
        {
            try
            {
                SqliteRuntime.ShutdownDomainAsync().GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
        }
    }
}
