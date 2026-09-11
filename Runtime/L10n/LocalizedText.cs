using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.L10n
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TMP_Text))]
    public sealed class LocalizedText : MonoBehaviour
    {
        [SerializeField] string key;

        TMP_Text text;
        ContentSizeFitter fitter;
        LanguageResponsiveText layout;

        public void SetKey(string newKey)
        {
            key = newKey;
            Apply();
        }

        void Awake()
        {
            text = GetComponent<TMP_Text>();
            fitter = GetComponentInParent<ContentSizeFitter>();
            layout = GetComponent<LanguageResponsiveText>();
        }

        void OnEnable()
        {
            LanguageManager.Register(this);
            Apply();
        }

        void OnDisable()
        {
            LanguageManager.Unregister(this);
            LanguageManager.ReleaseRtl(text);
        }

        internal void Apply()
        {
            if (text == null || !LanguageManager.IsInited || string.IsNullOrEmpty(key))
            {
                return;
            }

            LanguageManager.SetText(text, key);
            if (fitter != null && (layout == null || !layout.isActiveAndEnabled))
            {
                text.ForceMeshUpdate();
                LayoutRebuilder.MarkLayoutForRebuild(text.rectTransform);
            }
        }
    }
}
