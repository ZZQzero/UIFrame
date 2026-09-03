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
            Debug.Assert(result > 0f);
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
            Debug.Assert(result > 0f);
            return result;
        }
    }
}