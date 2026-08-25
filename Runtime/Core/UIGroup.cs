namespace UIFrame
{
    /// <summary>
    /// 卸载分组。切场景只关 Scene 组：<c>UI.CloseGroup(Scene)</c> 进缓存；
    /// 要释放内存用 <c>UI.CloseGroup(Scene, destroy: true)</c>。Hud / Persistent 不会被 Scene 组关掉。
    /// </summary>
    public enum UIGroup
    {
        Persistent = 0,
        Hud,
        Scene,
    }
}
