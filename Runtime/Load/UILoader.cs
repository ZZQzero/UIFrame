using System;
using Cysharp.Threading.Tasks;
using System.Threading;
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
                throw new InvalidOperationException(
                    $"[UIFrame] ResourcePackage 为空，无法加载 {location}。请先 UI.SetPackage。");
            }

            AssetHandle handle = null;
            GameObject instance = null;
            try
            {
                handle = _package.LoadAssetAsync<GameObject>(location);
                await handle;
                if (handle.Status != EOperationStatus.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"[UIFrame] 加载失败: Package={_package.PackageName}, Panel={panelType.FullName}, Location={location}, Status={handle.Status}, Error={handle.Error}");
                }

                if (req is { Cancelled: true })
                {
                    throw new OperationCanceledException();
                }

                var op = handle.InstantiateAsync(new InstantiateOptions(false, parent, false));
                await op;
                instance = op.Result;
                if (instance == null)
                {
                    throw new InvalidOperationException(
                        $"[UIFrame] InstantiateAsync 失败: {location}");
                }

                if (req is { Cancelled: true })
                {
                    throw new OperationCanceledException();
                }

                instance.name = panelType.Name;

                var panel = instance.GetComponent(panelType) as UIPanel;
                if (panel == null || panel.GetType() != panelType)
                {
                    throw new InvalidOperationException(
                        $"[UIFrame] Prefab 根节点缺少精确面板类型 {panelType.FullName}: {location}");
                }

                panel.AssetHandle = handle;
                return panel;
            }
            catch
            {
                if (instance != null)
                {
                    UnityEngine.Object.Destroy(instance);
                    instance = null;
                }

                Release(handle);
                handle = null;
                throw;
            }
        }

        public static void Release(AssetHandle handle)
        {
            if (handle == null || !handle.IsValid)
            {
                return;
            }

            handle.Release();
        }

        public async UniTask<AssetHandle> LoadAsset<T>(
            string location,
            CancellationToken cancellationToken)
            where T : UnityEngine.Object
        {
            if (_package == null)
            {
                throw new InvalidOperationException(
                    $"[UIFrame] ResourcePackage 为空，无法加载 {location}。请先 UI.SetPackage。");
            }

            if (string.IsNullOrWhiteSpace(location))
                throw new ArgumentException("[UIFrame] 资源地址为空。", nameof(location));

            cancellationToken.ThrowIfCancellationRequested();
            AssetHandle handle = _package.LoadAssetAsync<T>(location);
            try
            {
                await handle;
                cancellationToken.ThrowIfCancellationRequested();
                if (handle.Status != EOperationStatus.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"[UIFrame] 图片加载失败: Package={_package.PackageName}, Location={location}, Status={handle.Status}, Error={handle.Error}");
                }

                return handle;
            }
            catch
            {
                Release(handle);
                throw;
            }
        }
    }
}
