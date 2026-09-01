using System;
using Cysharp.Threading.Tasks;

namespace UIFrame
{
    class UILoadRequest
    {
        public bool Cancelled;
        public Type PanelType;
    }

    /// <summary>加载队列项。object 只活在队列里，不会写到面板字段。</summary>
    sealed class UIOpenRequest : UILoadRequest
    {
        public UIOpenMode Mode;
        public object Args;
        public readonly UniTaskCompletionSource<UIPanel> Completion = new UniTaskCompletionSource<UIPanel>();
    }
}
