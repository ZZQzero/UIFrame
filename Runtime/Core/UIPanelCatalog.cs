using System;
using System.Collections.Generic;
using UnityEngine;

namespace UIFrame
{
    /// <summary>
    /// Type → Prefab 地址目录。无反射。层由 Hud/Push/Popup/Toast 决定。
    /// 关闭默认进缓存（不 Destroy、不 Release Handle）；仅显式 destroy 才释放。正式包必须 Register。
    /// </summary>
    static class UIPanelCatalog
    {
        struct Entry : IEquatable<Entry>
        {
            public string Location;
            public UIGroup Group;
            public bool Cache;

            public bool Equals(Entry other)
            {
                return Group == other.Group
                       && Cache == other.Cache
                       && string.Equals(Location, other.Location, StringComparison.Ordinal);
            }
        }

        static readonly Dictionary<Type, Entry> Map = new Dictionary<Type, Entry>();

        internal static void Register<TPanel>(string location, UIGroup group = UIGroup.Scene)
            where TPanel : UIPanel
        {
            Register(typeof(TPanel), location, group, cache: true);
        }

        internal static void Register<TPanel>(string location, UIGroup group, bool cache)
            where TPanel : UIPanel
        {
            Register(typeof(TPanel), location, group, cache);
        }

        static void Register(Type panelType, string location, UIGroup group, bool cache)
        {
            if (string.IsNullOrWhiteSpace(location))
            {
                Debug.LogError($"[UIFrame] Register 失败：{panelType.Name} 的 Location 为空。");
                return;
            }

            var entry = new Entry
            {
                Location = location.Trim(),
                Group = group,
                Cache = cache,
            };
            if (Map.TryGetValue(panelType, out var existing))
            {
                if (existing.Equals(entry))
                {
                    return;
                }

                Debug.LogWarning(
                    $"[UIFrame] 重复注册 {panelType.Name}：{existing.Location} -> {entry.Location}");
            }

            Map[panelType] = entry;
        }

        internal static bool TryResolve(Type panelType, UIOpenMode mode, out UIPanelBind bind)
        {
            if (panelType == null)
            {
                Debug.LogError("[UIFrame] Resolve 失败：panelType 为空。");
                bind = default;
                return false;
            }

            if (!Map.TryGetValue(panelType, out var entry))
            {
                Debug.LogError($"[UIFrame] 未注册 {panelType.Name}，请先 UI.Register<{panelType.Name}>(location)。");
                bind = default;
                return false;
            }

            bind = new UIPanelBind(entry.Location, InferLayer(mode), entry.Group, entry.Cache);
            return true;
        }

        internal static UIPanelBind WithMode(UIPanelBind bind, UIOpenMode mode)
        {
            return new UIPanelBind(bind.Location, InferLayer(mode), bind.Group, bind.Cache);
        }

        static UILayer InferLayer(UIOpenMode mode)
        {
            switch (mode)
            {
                case UIOpenMode.Hud:
                    return UILayer.Hud;
                case UIOpenMode.Popup:
                    return UILayer.Popup;
                case UIOpenMode.Toast:
                case UIOpenMode.Tips:
                    return UILayer.Tips;
                case UIOpenMode.Guide:
                    return UILayer.Guide;
                default:
                    return UILayer.Window;
            }
        }
    }
}
