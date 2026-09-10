using System;

namespace Game.Fsm
{
    public sealed class FsmException : InvalidOperationException
    {
        public FsmException(string message) : base(message)
        {
        }
    }
}
