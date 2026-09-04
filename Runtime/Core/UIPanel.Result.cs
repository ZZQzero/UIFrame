using Cysharp.Threading.Tasks;

namespace UIFrame
{
    /// <summary>
    /// 带打开参数和返回值的面板。用 <see cref="CloseWithResult"/> 提交结果；
    /// 遮罩关闭、Back、CloseSelf 等未提交时，等待方收到取消。
    /// </summary>
    public abstract class UIPanel<TArgs, TResult> : UIPanel<TArgs>
    {
        UniTaskCompletionSource<TResult> _result;

        protected override void PrepareOpen()
        {
            _result?.TrySetCanceled();
            _result = new UniTaskCompletionSource<TResult>();
        }

        protected override void CompleteOpen()
        {
            var pending = _result;
            if (pending == null)
            {
                return;
            }

            _result = null;
            pending.TrySetCanceled();
        }

        /// <summary>提交结果并关闭。默认进缓存。</summary>
        protected void CloseWithResult(TResult result, bool destroy = false)
        {
            var pending = _result;
            _result = null;
            pending?.TrySetResult(result);
            CloseSelf(destroy);
        }

        internal UniTask<TResult> WaitResultAsync()
        {
            return _result != null
                ? _result.Task
                : UniTask.FromCanceled<TResult>();
        }
    }
}
