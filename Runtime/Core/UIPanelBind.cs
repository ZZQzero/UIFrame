namespace UIFrame
{
    /// <summary>一次打开解析出的绑定：地址、层、分组、关闭是否缓存。</summary>
    readonly struct UIPanelBind
    {
        public readonly string Location;
        public readonly UILayer Layer;
        public readonly UIGroup Group;
        public readonly bool Cache;

        public UIPanelBind(string location, UILayer layer, UIGroup group, bool cache)
        {
            Location = location;
            Layer = layer;
            Group = group;
            Cache = cache;
        }
    }
}
