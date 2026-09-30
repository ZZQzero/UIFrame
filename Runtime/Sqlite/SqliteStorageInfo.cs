namespace UIFrame.Sqlite
{
    public enum SqliteCheckpointMode
    {
        Passive,
        Truncate
    }

    /// <summary>One maintenance observation. Concurrent writes can change file sizes immediately.</summary>
    public readonly struct SqliteStorageInfo
    {
        public long DatabaseBytes { get; }
        public long WalBytes { get; }
        public long AvailableDiskBytes { get; }
        public long LogPages { get; }
        public long CheckpointedPages { get; }
        public bool CheckpointBlocked { get; }
        internal SqliteStorageInfo(SqliteRow row)
        {
            DatabaseBytes = row.GetInt64(0);
            WalBytes = row.GetInt64(1);
            AvailableDiskBytes = row.GetInt64(2);
            LogPages = row.GetInt64(3);
            CheckpointedPages = row.GetInt64(4);
            CheckpointBlocked = row.GetInt64(5) != 0;
        }
    }
}
