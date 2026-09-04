using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using YooAsset;

namespace UIFrame
{
    /// <summary>
    /// 加载面板并保留 YooAsset Handle，直到销毁才 Release。
    /// </summary>
    sealed class UILoader
    {
        ResourcePackage _package;

        public void SetPackage(ResourcePackage package)
        {
            _package = package;
        }

        public async UniTask<UIPanel> Load(
            Type panelType,
            string location,
            Transform parent,
            UILoadRequest req)
        {
            if (_package == null)
            {
                Debug.LogError("[UIFrame] ResourcePackage 为空，无法加载 " + location);
                return null;
            }

            AssetHandle handle = null;
            GameObject instance = null;
            try
            {
                handle = _package.LoadAssetAsync<GameObject>(location);
                await handle;
                if (handle == null || handle.Status != EOperationStatus.Succeeded)
                {
                    Debug.LogError($"[UIFrame] 加载失败: {location}, Status={handle?.Status}");
                    Release(handle);
                    return null;
                }

                if (IsCancelled(req))
                {
                    Release(handle);
                    return null;
                }

                var op = handle.InstantiateAsync(new InstantiateOptions(false, parent, false));
                await op;
                instance = op.Result;
                if (instance == null)
                {
                    Debug.LogError($"[UIFrame] InstantiateAsync 失败: {location}");
                    Release(handle);
                    return null;
                }

                if (IsCancelled(req))
                {
                    UnityEngine.Object.Destroy(instance);
                    Release(handle);
                    return null;
                }

                instance.name = panelType.Name;

                var panel = instance.GetComponent(panelType) as UIPanel;
                if (panel == null || panel.GetType() != panelType)
                {
                    Debug.LogError($"[UIFrame] Prefab 根节点缺少精确面板类型 {panelType.FullName}: {location}");
                    UnityEngine.Object.Destroy(instance);
                    Release(handle);
                    return null;
                }

                panel.AssetHandle = handle;
                return panel;
            }
            catch
            {
                if (instance != null)
                {
                    UnityEngine.Object.Destroy(instance);
                }

                Release(handle);
                throw;
            }
        }

        static bool IsCancelled(UILoadRequest req)
        {
            return req != null && req.Cancelled;
        }

        public static void Release(AssetHandle handle)
        {
            if (handle == null || !handle.IsValid)
            {
                return;
            }

            handle.Release();
        }
    }
}
