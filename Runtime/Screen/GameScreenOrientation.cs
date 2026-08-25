namespace UIFrame
{
    public enum GameScreenOrientation
    {
        /// <summary>锁定竖屏。</summary>
        Portrait = 0,

        /// <summary>锁定横屏（允许左右横屏自动旋转）。</summary>
        Landscape = 1,

        /// <summary>仅竖屏方向可自动旋转（含倒立）。</summary>
        AutoPortrait = 2,

        /// <summary>仅横屏方向可自动旋转。</summary>
        AutoLandscape = 3,
    }
    
    /// <summary>
    /// Canvas 参考布局（与设备方向配合：竖屏用 Portrait，横屏用 Landscape）。
    /// </summary>
    public enum GameUICanvasLayout
    {
        Portrait = 0,
        Landscape = 1,
    }
}