namespace Game.Audio
{
    internal sealed class BgmRequestGate
    {
        private uint version;

        internal uint BeginRequest()
        {
            version++;
            if (version == 0)
            {
                version = 1;
            }

            return version;
        }

        internal bool IsCurrent(uint requestVersion) =>
            requestVersion != 0 && requestVersion == version;

        internal void Invalidate()
        {
            _ = BeginRequest();
        }
    }
}
