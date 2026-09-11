using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using TMPro;
using UnityEngine;

namespace Game.L10n
{
    public static class LanguageManager
    {
        const string PrefsKey = "game.language";

        static Dictionary<string, LanguageTexts> table;
        static readonly List<LocalizedText> ActiveTexts = new();
        static readonly List<LanguageResponsiveText> ActiveLayouts = new();
        static readonly ConditionalWeakTable<TMP_Text, AlignmentState> savedAlignment = new();
#if UNITY_EDITOR
        static readonly HashSet<string> WarnedKeys = new();
#endif

        public static event Action<GameLanguage> LanguageChanged;

        public static bool IsInited => table != null;

        public static GameLanguage Current { get; private set; } = GameLanguage.ZhCN;

        public static void Init(Dictionary<string, LanguageTexts> rows)
        {
            table = rows ?? throw new ArgumentNullException(nameof(rows));
            Current = ReadSavedLanguage();
#if UNITY_EDITOR
            WarnedKeys.Clear();
#endif
            RefreshTexts();
            LanguageChanged?.Invoke(Current);
            RefreshLayouts();
        }

        public static void Shutdown()
        {
            table = null;
            Current = GameLanguage.ZhCN;
            ActiveTexts.Clear();
            ActiveLayouts.Clear();
            LanguageChanged = null;
            savedAlignment.Clear();
#if UNITY_EDITOR
            WarnedKeys.Clear();
#endif
        }

        public static void SetLanguage(GameLanguage language)
        {
            EnsureInited();
            if (Current == language)
            {
                return;
            }

            Current = language;
            PlayerPrefs.SetInt(PrefsKey, (int)language);
            RefreshTexts();
            LanguageChanged?.Invoke(language);
            RefreshLayouts();
        }

        public static string Get(string key)
        {
            EnsureInited();
            if (string.IsNullOrEmpty(key))
            {
                return string.Empty;
            }

            if (!table.TryGetValue(key, out var texts))
            {
                WarnMissing(key, "missing or empty");
                return key;
            }

            return Pick(key, in texts);
        }

        public static string Format(string key, params object[] args)
        {
            var template = Get(key);
            if (args == null || args.Length == 0)
            {
                return template;
            }

            try
            {
                return string.Format(template, args);
            }
            catch (FormatException)
            {
                return template;
            }
        }

        public static void SetText(TMP_Text text, string key)
        {
            if (text == null)
            {
                return;
            }

            text.text = Get(key);
            ApplyRtl(text);
        }

        public static void SetFormat(TMP_Text text, string key, params object[] args)
        {
            if (text == null)
            {
                return;
            }

            text.text = Format(key, args);
            ApplyRtl(text);
        }

        public static void ApplyRtl(TMP_Text text)
        {
            if (text == null)
            {
                return;
            }

            bool rtl = Current == GameLanguage.ArSA;
            if (text.isRightToLeftText != rtl)
            {
                text.isRightToLeftText = rtl;
            }

            if (rtl)
            {
                if (!savedAlignment.TryGetValue(text, out var saved))
                {
                    saved = new AlignmentState(text.alignment);
                    savedAlignment.Add(text, saved);
                }

                var mapped = ToRtlAlignment(saved.Original);
                if (text.alignment != mapped)
                {
                    text.alignment = mapped;
                }

                return;
            }

            if (savedAlignment.TryGetValue(text, out var original))
            {
                if (text.alignment != original.Original)
                {
                    text.alignment = original.Original;
                }

                savedAlignment.Remove(text);
            }
        }

        internal static void ReleaseRtl(TMP_Text text)
        {
            if (text == null)
            {
                return;
            }

            if (savedAlignment.TryGetValue(text, out var original))
            {
                if (text.alignment != original.Original)
                {
                    text.alignment = original.Original;
                }

                savedAlignment.Remove(text);
            }

            if (text.isRightToLeftText)
            {
                text.isRightToLeftText = false;
            }
        }

        static TextAlignmentOptions ToRtlAlignment(TextAlignmentOptions alignment)
        {
            switch (alignment)
            {
                case TextAlignmentOptions.Left:
                    return TextAlignmentOptions.Right;
                case TextAlignmentOptions.TopLeft:
                    return TextAlignmentOptions.TopRight;
                case TextAlignmentOptions.MidlineLeft:
                    return TextAlignmentOptions.MidlineRight;
                case TextAlignmentOptions.BottomLeft:
                    return TextAlignmentOptions.BottomRight;
                case TextAlignmentOptions.BaselineLeft:
                    return TextAlignmentOptions.BaselineRight;
                case TextAlignmentOptions.CaplineLeft:
                    return TextAlignmentOptions.CaplineRight;
                default:
                    return alignment;
            }
        }

        internal static void Register(LocalizedText text)
        {
            if (text == null || ActiveTexts.Contains(text))
            {
                return;
            }

            ActiveTexts.Add(text);
        }

        internal static void Unregister(LocalizedText text)
        {
            RemoveSwap(ActiveTexts, text);
        }

        internal static void Register(LanguageResponsiveText layout)
        {
            if (layout == null || ActiveLayouts.Contains(layout))
            {
                return;
            }

            ActiveLayouts.Add(layout);
        }

        internal static void Unregister(LanguageResponsiveText layout)
        {
            RemoveSwap(ActiveLayouts, layout);
        }

        static void RefreshTexts()
        {
            for (int i = 0; i < ActiveTexts.Count; i++)
            {
                ActiveTexts[i].Apply();
            }
        }

        static void RefreshLayouts()
        {
            for (int i = 0; i < ActiveLayouts.Count; i++)
            {
                var layout = ActiveLayouts[i];
                if (layout != null)
                {
                    layout.Apply();
                }
            }
        }

        static void RemoveSwap<T>(List<T> list, T item) where T : class
        {
            int index = list.IndexOf(item);
            if (index < 0)
            {
                return;
            }

            int last = list.Count - 1;
            list[index] = list[last];
            list.RemoveAt(last);
        }

        static GameLanguage ReadSavedLanguage()
        {
            if (PlayerPrefs.HasKey(PrefsKey))
            {
                int stored = PlayerPrefs.GetInt(PrefsKey, (int)GameLanguage.ZhCN);
                if (stored == (int)GameLanguage.EnUS)
                {
                    return GameLanguage.EnUS;
                }

                if (stored == (int)GameLanguage.ArSA)
                {
                    return GameLanguage.ArSA;
                }

                return GameLanguage.ZhCN;
            }

            var sys = Application.systemLanguage;
            if (sys == SystemLanguage.Chinese ||
                sys == SystemLanguage.ChineseSimplified ||
                sys == SystemLanguage.ChineseTraditional)
            {
                return GameLanguage.ZhCN;
            }

            if (sys == SystemLanguage.Arabic)
            {
                return GameLanguage.ArSA;
            }

            return GameLanguage.EnUS;
        }

        static string Pick(string key, in LanguageTexts row)
        {
            string primary;
            switch (Current)
            {
                case GameLanguage.EnUS:
                    primary = row.EnUS;
                    break;
                case GameLanguage.ArSA:
                    primary = row.ArSA;
                    break;
                default:
                    primary = row.ZhCN;
                    break;
            }

            if (!string.IsNullOrEmpty(primary))
            {
                return primary;
            }

            WarnMissing(key, "empty text for");
            if (Current != GameLanguage.EnUS && !string.IsNullOrEmpty(row.EnUS))
            {
                return row.EnUS;
            }

            return string.IsNullOrEmpty(row.ZhCN) ? key : row.ZhCN;
        }

        static void EnsureInited()
        {
            if (table == null)
            {
                throw new InvalidOperationException("LanguageManager 未初始化。");
            }
        }

        static void WarnMissing(string key, string reason)
        {
#if UNITY_EDITOR
            if (!WarnedKeys.Add(key + "/" + Current + "/" + reason))
            {
                return;
            }

            Debug.LogWarning($"[Language] {reason} key: {key}, lang={Current}");
#endif
        }

        sealed class AlignmentState
        {
            public readonly TextAlignmentOptions Original;

            public AlignmentState(TextAlignmentOptions original)
            {
                Original = original;
            }
        }
    }
}
