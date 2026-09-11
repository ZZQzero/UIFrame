namespace Game.L10n
{
    public enum GameLanguage
    {
        ZhCN = 0,
        EnUS = 1,
        ArSA = 2,
    }

    public readonly struct LanguageTexts
    {
        public readonly string ZhCN;
        public readonly string EnUS;
        public readonly string ArSA;

        public LanguageTexts(string zhCN, string enUS, string arSA)
        {
            ZhCN = zhCN;
            EnUS = enUS;
            ArSA = arSA;
        }
    }
}
