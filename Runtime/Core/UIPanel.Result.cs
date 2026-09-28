using System;
using Cysharp.Threading.Tasks;

namespace UIFrame
{
    /// <summary>
    /// 带打开参数和返回值的面板。提交结果且关闭成功后交付结果；
    /// 未提交结果的正常关闭取消等待，关闭失败原样传播异常。
    /// </summary>
    public abstract class UIPanel<TArgs, TResult> : UIPanel<TArgs>
    {
        enum ResultState { NotOpened, Open, Closed }

        UniTaskCompletionSource<TResult> _result;
        ResultState _resultState;
        Exception _closeFailure;
        bool _hasResult;
        TResult _submittedResult;

        protected override void PrepareOpen()
        {
            var previous = _result;
            _result = null;
            _resultState = ResultState.Open;
            _closeFailure = null;
            _hasResult = false;
            _submittedResult = default;
            previous?.TrySetCanceled();
        }

        internal override void SettleClose(Exception failure)
        {
            if (_resultState != ResultState.Open) return;
            _resultState = ResultState.Closed;
            _closeFailure = failure;
            if (_result != null) CompleteResult();
        }

        void CompleteResult()
        {
            if (_closeFailure != null) _result.TrySetException(_closeFailure);
            else if (_hasResult) _result.TrySetResult(_submittedResult);
            else _result.TrySetCanceled();
        }

        /// <summary>提交结果并关闭；只有整个关闭操作成功后才交付结果。</summary>
        protected void CloseWithResult(TResult result, bool destroy = false)
        {
            if (_resultState != ResultState.Open || _hasResult)
                throw new InvalidOperationException("[UIFrame] 当前没有可提交结果的打开周期。");
            _hasResult = true;
            _submittedResult = result;
            try { CloseSelf(destroy); }
            catch (Exception exception)
            {
                SettleClose(exception);
                throw;
            }
        }

        internal UniTask<TResult> WaitResultAsync()
        {
            if (_resultState == ResultState.NotOpened)
                throw new InvalidOperationException("[UIFrame] 当前没有结果等待通道。");
            // 普通 Open 不消费结果；只为实际等待者创建任务，避免重复报告同步关闭错误。
            if (_result == null)
            {
                _result = new UniTaskCompletionSource<TResult>();
                if (_resultState == ResultState.Closed) CompleteResult();
            }
            return _result.Task;
        }
    }
}
