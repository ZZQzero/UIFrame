using UnityEngine;

namespace UnityEngine.UI
{
    [AddComponentMenu("UI/Loop Horizontal Scroll Rect (Multi Prefab)", 52)]
    [DisallowMultipleComponent]
    public class LoopHorizontalScrollRectMulti : LoopScrollRectMulti
    {
        protected LoopHorizontalScrollRectMulti()
        {
            direction = LoopScrollRectDirection.Horizontal;
        }

        protected override float GetSize(RectTransform item, bool includeSpacing)
        {
            var size = includeSpacing ? contentSpacing : 0f;
            size += m_GridLayout != null
                ? m_GridLayout.cellSize.x
                : LoopScrollSizeUtils.GetPreferredWidth(item);
            return size * m_Content.localScale.x;
        }

        protected override float GetDimension(Vector2 vector)
        {
            return -vector.x;
        }

        protected override float GetAbsDimension(Vector2 vector)
        {
            return vector.x;
        }

        protected override Vector2 GetVector(float value)
        {
            return new Vector2(-value, 0f);
        }

        protected override void Awake()
        {
            base.Awake();
            if (m_Content == null)
            {
                return;
            }

            var layout = m_Content.GetComponent<GridLayoutGroup>();
            if (layout != null && layout.constraint != GridLayoutGroup.Constraint.FixedRowCount)
            {
                Debug.LogError("[LoopScrollRect] Horizontal GridLayoutGroup requires FixedRowCount.", this);
            }
        }

        protected override bool UpdateItems(ref Bounds viewBounds, ref Bounds contentBounds)
        {
            var changed = HandlePageJump(ref viewBounds, ref contentBounds);

            if (viewBounds.max.x > contentBounds.max.x - m_ContentRightPadding)
            {
                var size = NewItemAtEnd();
                var totalSize = size;
                while (size > 0f
                       && viewBounds.max.x > contentBounds.max.x - m_ContentRightPadding + totalSize)
                {
                    size = NewItemAtEnd();
                    totalSize += size;
                }

                changed |= totalSize > 0f;
            }
            else if (itemTypeEnd % contentConstraintCount != 0
                     && (itemTypeEnd < totalCount || totalCount < 0))
            {
                NewItemAtEnd();
            }

            if (viewBounds.min.x < contentBounds.min.x + m_ContentLeftPadding)
            {
                var size = NewItemAtStart();
                var totalSize = size;
                while (size > 0f
                       && viewBounds.min.x < contentBounds.min.x + m_ContentLeftPadding - totalSize)
                {
                    size = NewItemAtStart();
                    totalSize += size;
                }

                changed |= totalSize > 0f;
            }

            if (viewBounds.max.x < contentBounds.max.x - threshold - m_ContentRightPadding
                && viewBounds.size.x < contentBounds.size.x - threshold)
            {
                var size = DeleteItemAtEnd();
                var totalSize = size;
                while (size > 0f
                       && viewBounds.max.x
                       < contentBounds.max.x - threshold - m_ContentRightPadding - totalSize)
                {
                    size = DeleteItemAtEnd();
                    totalSize += size;
                }

                changed |= totalSize > 0f;
            }

            if (viewBounds.min.x > contentBounds.min.x + threshold + m_ContentLeftPadding
                && viewBounds.size.x < contentBounds.size.x - threshold)
            {
                var size = DeleteItemAtStart();
                var totalSize = size;
                while (size > 0f
                       && viewBounds.min.x
                       > contentBounds.min.x + threshold + m_ContentLeftPadding + totalSize)
                {
                    size = DeleteItemAtStart();
                    totalSize += size;
                }

                changed |= totalSize > 0f;
            }

            if (changed)
            {
                ClearTempPool();
            }

            return changed;
        }

        bool HandlePageJump(ref Bounds viewBounds, ref Bounds contentBounds)
        {
            if (itemTypeEnd <= itemTypeStart)
            {
                return false;
            }

            if (viewBounds.size.x < contentBounds.min.x - viewBounds.max.x)
            {
                var currentSize = contentBounds.size.x;
                var elementSize = EstimiateElementSize();
                ReturnToTempPool(false, itemTypeEnd - itemTypeStart);
                itemTypeEnd = itemTypeStart;

                var offsetCount = Mathf.FloorToInt(
                    (contentBounds.min.x - viewBounds.max.x) / (elementSize + contentSpacing));
                if (totalCount >= 0 && itemTypeStart - offsetCount * contentConstraintCount < 0)
                {
                    offsetCount = Mathf.FloorToInt((float)itemTypeStart / contentConstraintCount);
                }

                itemTypeStart -= offsetCount * contentConstraintCount;
                if (totalCount >= 0)
                {
                    itemTypeStart = Mathf.Max(itemTypeStart, 0);
                }

                itemTypeEnd = itemTypeStart;
                itemTypeSize = 0f;

                var offset = offsetCount * (elementSize + contentSpacing);
                m_Content.anchoredPosition -= new Vector2(
                    offset + (reverseDirection ? currentSize : 0f),
                    0f);
                contentBounds.center -= new Vector3(offset + currentSize * 0.5f, 0f, 0f);
                contentBounds.size = Vector3.zero;
                return true;
            }

            if (viewBounds.min.x - contentBounds.max.x <= viewBounds.size.x)
            {
                return false;
            }

            var maxStart = -1;
            if (totalCount >= 0)
            {
                maxStart = Mathf.Max(0, totalCount - (itemTypeEnd - itemTypeStart));
                maxStart = maxStart / contentConstraintCount * contentConstraintCount;
            }

            var sizeBeforeJump = contentBounds.size.x;
            var estimatedSize = EstimiateElementSize();
            ReturnToTempPool(true, itemTypeEnd - itemTypeStart);
            itemTypeStart = itemTypeEnd;

            var count = Mathf.FloorToInt(
                (viewBounds.min.x - contentBounds.max.x) / (estimatedSize + contentSpacing));
            if (maxStart >= 0 && itemTypeStart + count * contentConstraintCount > maxStart)
            {
                count = Mathf.FloorToInt(
                    (float)(maxStart - itemTypeStart) / contentConstraintCount);
            }

            itemTypeStart += count * contentConstraintCount;
            if (totalCount >= 0)
            {
                itemTypeStart = Mathf.Max(itemTypeStart, 0);
            }

            itemTypeEnd = itemTypeStart;
            itemTypeSize = 0f;

            var jumpOffset = count * (estimatedSize + contentSpacing);
            m_Content.anchoredPosition += new Vector2(
                jumpOffset + (reverseDirection ? 0f : sizeBeforeJump),
                0f);
            contentBounds.center += new Vector3(jumpOffset + sizeBeforeJump * 0.5f, 0f, 0f);
            contentBounds.size = Vector3.zero;
            return true;
        }
    }
}
