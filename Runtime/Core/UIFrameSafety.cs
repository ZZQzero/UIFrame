namespace UIFrame
{
    /// <summary>
    /// 主线程与集合安全检查。默认 Editor / Development 打开，Release 关闭。
    /// 在创建对象池、调用红点之前设置。
    /// </summary>
    public static class UIFrameSafety
    {
        public static bool ThreadChecks { get; set; }
        public static bool CollectionChecks { get; set; }

        static UIFrameSafety()
        {
            ResetToBuildDefaults();
        }

        public static void ResetToBuildDefaults()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            ThreadChecks = true;
            CollectionChecks = true;
#else
            ThreadChecks = false;
            CollectionChecks = false;
#endif
        }
    }
}
