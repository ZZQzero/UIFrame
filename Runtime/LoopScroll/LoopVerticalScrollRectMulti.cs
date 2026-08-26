using UnityEngine;

namespace UnityEngine.UI
{
    [AddComponentMenu("UI/Loop Vertical Scroll Rect (Multi Prefab)", 53)]
    [DisallowMultipleComponent]
    public class LoopVerticalScrollRectMulti : LoopScrollRectMulti
    {
        protected LoopVerticalScrollRectMulti()
        {
            direction = LoopScrollRectDirection.Vertical;
        }

        protected override float GetSize(RectTransform item, bool includeSpacing)
        {
            var size = includeSpacing ? contentSpacing : 0f;
            size += m_GridLayout != null
                ? m_GridLayout.cellSize.y
                : LoopScrollSizeUtils.GetPreferredHeight(item);
            return size * m_Content.localScale.y;
        }

        protected override float GetDimension(Vector2 vector)
        {
            return vector.y;
        }

        protected override float GetAbsDimension(Vector2 vector)
        {
            return vector.y;
        }

        protected override Vector2 GetVector(float value)
        {
            return new Vector2(0f, value);
        }

        protected override void Awake()
        {
            base.Awake();
            if (m_Content == null)
            {
                return;
            }

            var layout = m_Content.GetComponent<GridLayoutGroup>();
            if (layout != null && layout.constraint != GridLayoutGroup.Constraint.FixedColumnCount)
            {
                Debug.LogError("[LoopScrollRect] Vertical GridLayoutGroup requires FixedColumnCount.", this);
            }
        }

        protected override bool UpdateItems(ref Bounds viewBounds, ref Bounds contentBounds)
        {
            var changed = HandlePageJump(ref viewBounds, ref contentBounds);

            if (viewBounds.min.y < contentBounds.min.y + m_ContentBottomPadding)
            {
                var size = NewItemAtEnd();
                var totalSize = size;
                while (size > 0f
                       && viewBounds.min.y < contentBounds.min.y + m_ContentBottomPadding - totalSize)
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

            if (viewBounds.max.y > contentBounds.max.y - m_ContentTopPadding)
            {
                var size = NewItemAtStart();
                var totalSize = size;
                while (size > 0f
                       && viewBounds.max.y > contentBounds.max.y - m_ContentTopPadding + totalSize)
                {
                    size = NewItemAtStart();
                    totalSize += size;
                }

                changed |= totalSize > 0f;
            }

            if (viewBounds.min.y > contentBounds.min.y + threshold + m_ContentBottomPadding
                && viewBounds.size.y < contentBounds.size.y - threshold)
            {
                var size = DeleteItemAtEnd();
                var totalSize = size;
                while (size > 0f
                       && viewBounds.min.y
                       > contentBounds.min.y + threshold + m_ContentBottomPadding + totalSize)
                {
                    size = DeleteItemAtEnd();
                    totalSize += size;
                }

                changed |= totalSize > 0f;
            }

            if (viewBounds.max.y < contentBounds.max.y - threshold - m_ContentTopPadding
                && viewBounds.size.y < contentBounds.size.y - threshold)
            {
                var size = DeleteItemAtStart();
                var totalSize = size;
                while (size > 0f
                       && viewBounds.max.y
                       < contentBounds.max.y - threshold - m_ContentTopPadding - totalSize)
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

            if (viewBounds.size.y < contentBounds.min.y - viewBounds.max.y)
            {
                var maxStart = totalCount >= 0
                    ? Mathf.Max(0, totalCount - (itemTypeEnd - itemTypeStart))
                    : -1;
                var currentSize = contentBounds.size.y;
                var elementSize = EstimiateElementSize();

                ReturnToTempPool(true, itemTypeEnd - itemTypeStart);
                itemTypeStart = itemTypeEnd;

                var offsetCount = Mathf.FloorToInt(
                    (contentBounds.min.y - viewBounds.max.y) / (elementSize + contentSpacing));
                if (maxStart >= 0 && itemTypeStart + offsetCount * contentConstraintCount > maxStart)
                {
                    offsetCount = Mathf.FloorToInt(
                        (float)(maxStart - itemTypeStart) / contentConstraintCount);
                }

                itemTypeStart += offsetCount * contentConstraintCount;
                if (totalCount >= 0)
                {
                    itemTypeStart = Mathf.Max(itemTypeStart, 0);
                }

                itemTypeEnd = itemTypeStart;
                itemTypeSize = 0f;

                var offset = offsetCount * (elementSize + contentSpacing);
                m_Content.anchoredPosition -= new Vector2(
                    0f,
                    offset + (reverseDirection ? 0f : currentSize));
                contentBounds.center -= new Vector3(0f, offset + currentSize * 0.5f, 0f);
                contentBounds.size = Vector3.zero;
                return true;
            }

            if (viewBounds.min.y - contentBounds.max.y <= viewBounds.size.y)
            {
                return false;
            }

            var sizeBeforeJump = contentBounds.size.y;
            var estimatedSize = EstimiateElementSize();
            ReturnToTempPool(false, itemTypeEnd - itemTypeStart);
            itemTypeEnd = itemTypeStart;

            var count = Mathf.FloorToInt(
                (viewBounds.min.y - contentBounds.max.y) / (estimatedSize + contentSpacing));
            if (totalCount >= 0 && itemTypeStart - count * contentConstraintCount < 0)
            {
                count = Mathf.FloorToInt((float)itemTypeStart / contentConstraintCount);
            }

            itemTypeStart -= count * contentConstraintCount;
            if (totalCount >= 0)
            {
                itemTypeStart = Mathf.Max(itemTypeStart, 0);
            }

            itemTypeEnd = itemTypeStart;
            itemTypeSize = 0f;

            var jumpOffset = count * (estimatedSize + contentSpacing);
            m_Content.anchoredPosition += new Vector2(
                0f,
                jumpOffset + (reverseDirection ? sizeBeforeJump : 0f));
            contentBounds.center += new Vector3(0f, jumpOffset + sizeBeforeJump * 0.5f, 0f);
            contentBounds.size = Vector3.zero;
            return true;
        }
    }
}
