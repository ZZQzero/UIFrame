using System;
using System.Runtime.ExceptionServices;
using UnityEngine;

namespace UIFrame
{
    // 清理必须继续；只向调用方传播第一个错误，次级错误记录一次。
    internal struct CleanupFailure
    {
        ExceptionDispatchInfo first;

        public void Capture(Exception exception)
        {
            if (first == null) first = ExceptionDispatchInfo.Capture(exception);
            else Debug.LogException(exception);
        }

        public void Run(Action cleanup)
        {
            try { cleanup(); }
            catch (Exception exception) { Capture(exception); }
        }

        public void Throw() => first?.Throw();
    }
}
