using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UIFrame.Editor
{
    [Serializable]
    class UIBindEntry
    {
        public string FieldName;
        public string TypeName;
        public string HierarchyPath;
        public bool IsGameObject;
        public long LocalFileId;
    }

    [Serializable]
    class UIPrefabBindState
    {
        public string PrefabGuid;
        /// <summary>宿主相对 Prefab 根的路径。根上的 Panel/Item 为空，避免和同 Prefab 里的子 Item 抢一条记录。</summary>
        public string HostPath;
        public string ClassName;
        public string NamespaceName;
        public string ScriptPath;
        public string GenPath;
        public bool PendingAttach;
        public bool PendingAssign;
        public bool IsItem;
        public bool BindsInitialized;
        public List<UIBindEntry> Binds = new List<UIBindEntry>();
    }

    [FilePath("Library/UIFrameCodeGen/BindStore.asset", FilePathAttribute.Location.ProjectFolder)]
    sealed class UIBindStore : ScriptableSingleton<UIBindStore>
    {
        [SerializeField] List<UIPrefabBindState> _items = new List<UIPrefabBindState>();

        public UIPrefabBindState Get(string prefabGuid, string hostPath = null)
        {
            if (string.IsNullOrEmpty(prefabGuid))
            {
                return null;
            }

            hostPath ??= "";
            for (var i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (item.PrefabGuid == prefabGuid && (item.HostPath ?? "") == hostPath)
                {
                    return item;
                }
            }

            return null;
        }

        public UIPrefabBindState GetOrCreate(string prefabGuid, string hostPath = null)
        {
            hostPath ??= "";
            var state = Get(prefabGuid, hostPath);
            if (state != null)
            {
                return state;
            }

            state = new UIPrefabBindState { PrefabGuid = prefabGuid, HostPath = hostPath };
            _items.Add(state);
            return state;
        }

        public IReadOnlyList<UIPrefabBindState> All => _items;

        public void Persist()
        {
            Save(true);
        }
    }

    static class UICodeGenPrefs
    {
        const string FolderKey = "UIFrame.Gen.Folder";
        const string NamespaceKey = "UIFrame.Gen.Namespace";

        public static string Folder
        {
            get => EditorPrefs.GetString(FolderKey, "Assets/Scripts/UI");
            set => EditorPrefs.SetString(FolderKey, value);
        }

        public static string NamespaceName
        {
            get => EditorPrefs.GetString(NamespaceKey, "Game");
            set => EditorPrefs.SetString(NamespaceKey, value);
        }
    }
}
