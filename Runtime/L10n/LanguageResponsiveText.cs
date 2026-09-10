using System;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace Game.L10n
{
    [Serializable]
    public struct LanguageAutoSize
    {
        [Tooltip("勾选后用 Min/Max 自动缩放；不勾选则用 Font Size（0 表示 Prefab 默认）")]
        public bool autoSize;
        [Tooltip("未勾选 Auto Size 时的字号，0 表示用 Prefab 默认")]
        public float fontSize;
        [Tooltip("仅 Auto Size 时有效")]
        public float min;
        [Tooltip("仅 Auto Size 时有效")]
        public float max;
    }

    [DisallowMultipleComponent]
    [RequireComponent(typeof(TMP_Text))]
    public sealed class LanguageResponsiveText : MonoBehaviour
    {
        [SerializeField] LanguageAutoSize zhCN;
        [SerializeField] LanguageAutoSize enUS;
        [SerializeField] LanguageAutoSize arSA;

        TMP_Text text;
        ContentSizeFitter fitter;
        bool captured;
        bool originalAutoSizing;
        float originalFontSize;
        float originalFontSizeMin;
        float originalFontSizeMax;

        public void Set(GameLanguage language, LanguageAutoSize settings)
        {
            WriteSlot(language, settings);
            if (isActiveAndEnabled)
            {
                Apply();
            }
        }

        public void SetAutoSize(GameLanguage language, bool autoSize, float min, float max)
        {
            var slot = ReadSlot(language);
            slot.autoSize = autoSize;
            slot.min = min;
            slot.max = max;
            Set(language, slot);
        }

        public void SetFontSize(GameLanguage language, float fontSize)
        {
            var slot = ReadSlot(language);
            slot.autoSize = false;
            slot.fontSize = fontSize;
            Set(language, slot);
        }

        void Awake()
        {
            text = GetComponent<TMP_Text>();
            fitter = GetComponentInParent<ContentSizeFitter>();
        }

        void OnEnable()
        {
            LanguageManager.Register(this);
            Apply();
        }

        void OnDisable()
        {
            LanguageManager.Unregister(this);
            Restore();
        }

        internal void Apply()
        {
            if (text == null || !LanguageManager.IsInited)
            {
                return;
            }

            var slot = ReadSlot(LanguageManager.Current);
            if (slot.autoSize)
            {
                EnsureCaptured();
                ApplyAutoSize(slot);
            }
            else if (slot.fontSize > 0f)
            {
                EnsureCaptured();
                ApplyFixedSize(slot.fontSize);
            }
            else
            {
                WriteOriginal();
            }

            if (fitter != null)
            {
                text.ForceMeshUpdate();
                LayoutRebuilder.MarkLayoutForRebuild(text.rectTransform);
            }
        }

        LanguageAutoSize ReadSlot(GameLanguage language)
        {
            switch (language)
            {
                case GameLanguage.EnUS:
                    return enUS;
                case GameLanguage.ArSA:
                    return arSA;
                default:
                    return zhCN;
            }
        }

        void WriteSlot(GameLanguage language, LanguageAutoSize slot)
        {
            switch (language)
            {
                case GameLanguage.EnUS:
                    enUS = slot;
                    break;
                case GameLanguage.ArSA:
                    arSA = slot;
                    break;
                default:
                    zhCN = slot;
                    break;
            }
        }

        void EnsureCaptured()
        {
            if (captured || text == null)
            {
                return;
            }

            originalAutoSizing = text.enableAutoSizing;
            originalFontSize = text.fontSize;
            originalFontSizeMin = text.fontSizeMin;
            originalFontSizeMax = text.fontSizeMax;
            captured = true;
        }

        void ApplyAutoSize(LanguageAutoSize slot)
        {
            float max = slot.max > 0f ? slot.max : DesignFontSizeMax();
            float min = slot.min > 0f ? slot.min : Mathf.Max(12f, max * 0.5f);
            if (min > max)
            {
                min = max;
            }

            if (text.fontSizeMax != max)
            {
                text.fontSizeMax = max;
            }

            if (text.fontSizeMin != min)
            {
                text.fontSizeMin = min;
            }

            if (!text.enableAutoSizing)
            {
                text.enableAutoSizing = true;
            }
        }

        void ApplyFixedSize(float size)
        {
            if (text.enableAutoSizing)
            {
                text.enableAutoSizing = false;
            }

            if (text.fontSizeMin != originalFontSizeMin)
            {
                text.fontSizeMin = originalFontSizeMin;
            }

            if (text.fontSizeMax != originalFontSizeMax)
            {
                text.fontSizeMax = originalFontSizeMax;
            }

            if (text.fontSize != size)
            {
                text.fontSize = size;
            }
        }

        float DesignFontSizeMax()
        {
            if (originalAutoSizing && originalFontSizeMax > 0f)
            {
                return originalFontSizeMax;
            }

            if (originalFontSize > 0f)
            {
                return originalFontSize;
            }

            if (originalFontSizeMax > 0f)
            {
                return originalFontSizeMax;
            }

            return 36f;
        }

        void WriteOriginal()
        {
            if (!captured || text == null)
            {
                return;
            }

            text.enableAutoSizing = originalAutoSizing;
            text.fontSizeMin = originalFontSizeMin;
            text.fontSizeMax = originalFontSizeMax;
            if (!originalAutoSizing)
            {
                text.fontSize = originalFontSize;
            }
        }

        void Restore()
        {
            WriteOriginal();
            captured = false;
        }
    }
}
