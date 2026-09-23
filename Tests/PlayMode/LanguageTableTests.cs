using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Game.L10n;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.TestTools;

namespace UIFrame.Regression
{
    public class LanguageTableTests
    {
        const string PrefsKey = "game.language";
        bool hadSavedLanguage;
        int savedLanguage;
        readonly List<GameObject> objects = new();

        static LanguageTexts Row(string value) => new(value + "-zh", value + "-en", value + "-ar");

        [SetUp] public void Setup()
        {
            LanguageManager.Shutdown();
            hadSavedLanguage = PlayerPrefs.HasKey(PrefsKey);
            savedLanguage = PlayerPrefs.GetInt(PrefsKey);
            PlayerPrefs.SetInt(PrefsKey, (int)GameLanguage.EnUS);
        }

        [UnityTearDown] public IEnumerator Cleanup()
        {
            foreach (var obj in objects)
                if (obj != null) UnityEngine.Object.Destroy(obj);
            objects.Clear();
            yield return null;
            LanguageManager.Shutdown();
            if (hadSavedLanguage) PlayerPrefs.SetInt(PrefsKey, savedLanguage);
            else PlayerPrefs.DeleteKey(PrefsKey);
        }

        [Test] public void AddRequiresInitialization()
        {
            Assert.Throws<InvalidOperationException>(() => LanguageManager.AddTable(new() { ["new"] = Row("new") }));
            Assert.IsFalse(LanguageManager.IsInited);
        }

        [Test] public void NullTableIsRejected()
        {
            LanguageManager.Init(new() { ["old"] = Row("old") });
            Assert.Throws<ArgumentNullException>(() => LanguageManager.AddTable(null));
            Assert.AreEqual("old-en", LanguageManager.Get("old"));
        }

        [TestCase(GameLanguage.ZhCN, "zh")]
        [TestCase(GameLanguage.EnUS, "en")]
        [TestCase(GameLanguage.ArSA, "ar")]
        public void MultipleBatchesRetainAllTables(GameLanguage language, string suffix)
        {
            LanguageManager.Init(new() { ["old"] = Row("old") });
            LanguageManager.SetLanguage(language);
            LanguageManager.AddTable(new() { ["scene"] = Row("scene") });
            LanguageManager.AddTable(new() { ["activity"] = Row("activity") });
            Assert.AreEqual("old-" + suffix, LanguageManager.Get("old"));
            Assert.AreEqual("scene-" + suffix, LanguageManager.Get("scene"));
            Assert.AreEqual("activity-" + suffix, LanguageManager.Get("activity"));
            Assert.AreEqual(language, LanguageManager.Current);
        }

        [Test] public void AddDoesNotReadOrWriteLanguagePreferenceOrPublishLanguageChange()
        {
            LanguageManager.Init(new() { ["old"] = Row("old") });
            PlayerPrefs.SetInt(PrefsKey, (int)GameLanguage.ZhCN);
            int notifications = 0;
            LanguageManager.LanguageChanged += _ => notifications++;
            LanguageManager.AddTable(new() { ["new"] = Row("new") });
            Assert.AreEqual(GameLanguage.EnUS, LanguageManager.Current);
            Assert.AreEqual((int)GameLanguage.ZhCN, PlayerPrefs.GetInt(PrefsKey));
            Assert.AreEqual(0, notifications);
            Assert.AreEqual("new-en", LanguageManager.Get("new"));
        }

        [Test] public void DuplicateAgainstExistingTableRejectsTheEntireBatch()
        {
            LanguageManager.Init(new(StringComparer.OrdinalIgnoreCase) { ["old"] = Row("old") });
            var error = Assert.Throws<ArgumentException>(() => LanguageManager.AddTable(new()
            {
                ["new"] = Row("new"),
                ["OLD"] = Row("replacement"),
            }));
            StringAssert.Contains("OLD", error.Message);
            Assert.AreEqual("old-en", LanguageManager.Get("OLD"));
            AssertMissing("new");
        }

        [Test] public void DuplicateInsideIncomingBatchUsesExistingComparerAndRejectsAllRows()
        {
            LanguageManager.Init(new(StringComparer.OrdinalIgnoreCase) { ["old"] = Row("old") });
            var rows = new Dictionary<string, LanguageTexts>(StringComparer.Ordinal)
            {
                ["new"] = Row("first"),
                ["NEW"] = Row("second"),
            };
            Assert.Throws<ArgumentException>(() => LanguageManager.AddTable(rows));
            AssertMissing("new");
            Assert.AreEqual("old-en", LanguageManager.Get("old"));
            Assert.AreEqual(2, rows.Count);
        }

        [Test] public void OrdinalComparerAllowsDistinctCaseKeys()
        {
            LanguageManager.Init(new(StringComparer.Ordinal) { ["old"] = Row("old") });
            LanguageManager.AddTable(new() { ["new"] = Row("lower"), ["NEW"] = Row("upper") });
            Assert.AreEqual("lower-en", LanguageManager.Get("new"));
            Assert.AreEqual("upper-en", LanguageManager.Get("NEW"));
        }

        [Test] public void EmptyKeyRejectsTheEntireBatch()
        {
            LanguageManager.Init(new() { ["old"] = Row("old") });
            Assert.Throws<ArgumentException>(() => LanguageManager.AddTable(new()
            {
                ["new"] = Row("new"),
                [""] = Row("invalid"),
            }));
            AssertMissing("new");
            Assert.AreEqual("old-en", LanguageManager.Get("old"));
        }

        [Test] public void MergeDoesNotMutateCallerTablesOrShareTheirFutureChanges()
        {
            var initial = new Dictionary<string, LanguageTexts> { ["old"] = Row("old") };
            var additional = new Dictionary<string, LanguageTexts> { ["new"] = Row("new") };
            LanguageManager.Init(initial);
            LanguageManager.AddTable(additional);
            CollectionAssert.AreEquivalent(new[] { "old" }, initial.Keys);
            CollectionAssert.AreEquivalent(new[] { "new" }, additional.Keys);
            initial.Clear();
            additional["new"] = Row("changed");
            Assert.AreEqual("old-en", LanguageManager.Get("old"));
            Assert.AreEqual("new-en", LanguageManager.Get("new"));
        }

        [Test] public void RegisteredTextAndLayoutRefreshAfterAdd()
        {
            LanguageManager.Init(new() { ["old"] = Row("old") });
            var go = TextObject();
            var text = go.GetComponent<TMP_Text>();
            var layout = go.AddComponent<LanguageResponsiveText>();
            layout.SetFontSize(GameLanguage.EnUS, 17f);
            var localized = go.AddComponent<LocalizedText>();
            LogAssert.Expect(LogType.Warning, "[Language] missing or empty key: new, lang=EnUS");
            localized.SetKey("new");
            Assert.AreEqual("new", text.text);
            text.fontSize = 23f;
            LanguageManager.AddTable(new() { ["new"] = Row("new") });
            Assert.AreEqual("new-en", text.text);
            Assert.AreEqual(17f, text.fontSize);
            Assert.AreEqual("old-en", LanguageManager.Get("old"));
        }

        [Test] public void EmptyTableDoesNotRefreshOrPublishChange()
        {
            LanguageManager.Init(new() { ["old"] = Row("old") });
            var go = TextObject();
            go.AddComponent<LocalizedText>().SetKey("old");
            go.GetComponent<TMP_Text>().text = "unchanged";
            int notifications = 0;
            LanguageManager.LanguageChanged += _ => notifications++;
            LanguageManager.AddTable(new());
            Assert.AreEqual("unchanged", go.GetComponent<TMP_Text>().text);
            Assert.AreEqual(0, notifications);
            Assert.AreEqual("old-en", LanguageManager.Get("old"));
        }

        [Test] public void RefreshFailurePropagatesAfterDataCommit()
        {
            LanguageManager.Init(new() { ["old"] = Row("old") });
            var layout = TextObject().AddComponent<LanguageResponsiveText>();
            // 模拟 Inspector 中的非法配置，验证刷新错误不会被 AddTable 吞掉。
            typeof(LanguageResponsiveText).GetField("enUS", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(layout, new LanguageAutoSize { fontSize = float.NaN });
            Assert.Throws<ArgumentException>(() => LanguageManager.AddTable(new() { ["new"] = Row("new") }));
            Assert.AreEqual("new-en", LanguageManager.Get("new"));
            Assert.AreEqual("old-en", LanguageManager.Get("old"));
        }

        GameObject TextObject()
        {
            var go = new GameObject("language-table-test", typeof(RectTransform), typeof(TextMeshProUGUI));
            objects.Add(go);
            return go;
        }

        static void AssertMissing(string key)
        {
            LogAssert.Expect(LogType.Warning, $"[Language] missing or empty key: {key}, lang=EnUS");
            Assert.AreEqual(key, LanguageManager.Get(key));
        }
    }
}
