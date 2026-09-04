namespace UIFrame
{
    /// <summary>带类型化打开参数的面板。Args 只在打开时赋值一次。</summary>
    public abstract class UIPanel<TArgs> : UIPanel
    {
        public TArgs Args { get; private set; }

        internal override void ApplyArgs(object args)
        {
            if (args is TArgs typed)
            {
                Args = typed;
                return;
            }

            if (args == null && typeof(TArgs) == typeof(UINone))
            {
                Args = (TArgs)(object)UINone.Value;
                return;
            }

            var actual = args == null ? "null" : args.GetType().Name;
            throw new System.InvalidOperationException(
                $"[UIFrame] 打开参数类型不匹配: {PanelType.Name}, 期望 {typeof(TArgs).Name}, 实际 {actual}");
        }

        internal sealed override void DispatchOpenCore()
        {
            OnOpen(Args);
        }

        protected abstract void OnOpen(TArgs args);
    }
}
