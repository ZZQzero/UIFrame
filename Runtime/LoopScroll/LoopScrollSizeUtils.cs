using UnityEngine;
using UnityEngine.UI;

namespace UnityEngine.UI
{
    public static class LoopScrollSizeUtils
    {
        public static float GetPreferredHeight(RectTransform item)
        {
            var minHeight = LayoutUtility.GetLayoutProperty(item, e => e.minHeight, 0, out _);
            var preferredHeight = LayoutUtility.GetLayoutProperty(item, e => e.preferredHeight, 0, out _);
            var result = Mathf.Max(minHeight, preferredHeight);
            if (result <= 0f)
            {
                result = item.rect.height;
            }
            if (!float.IsFinite(result) || result <= 0f)
            {
                throw new System.InvalidOperationException(
                    $"[LoopScrollRect] Cell 布局尺寸必须为正有限值: Cell={item.name}, Size={result}。");
            }
            return result;
        }
        
        public static float GetPreferredWidth(RectTransform item)
        {
            var minWidth = LayoutUtility.GetLayoutProperty(item, e => e.minWidth, 0, out _);
            var preferredWidth = LayoutUtility.GetLayoutProperty(item, e => e.preferredWidth, 0, out _);
            var result = Mathf.Max(minWidth, preferredWidth);
            if (result <= 0f)
            {
                result = item.rect.width;
            }
            if (!float.IsFinite(result) || result <= 0f)
            {
                throw new System.InvalidOperationException(
                    $"[LoopScrollRect] Cell 布局尺寸必须为正有限值: Cell={item.name}, Size={result}。");
            }
            return result;
        }
    }
}