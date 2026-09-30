using System;
using System.IO;

namespace UIFrame.Sqlite
{
    public sealed class SqliteSnapshotOptions
    {
        public string Destination { get; }
        public long MaxTemporaryBytes { get; }
        public long MaxWalBytes { get; }
        public TimeSpan Timeout { get; }
        public SqliteSnapshotOptions(string destination, long maxTemporaryBytes = 512L * 1024 * 1024,
                                     long maxWalBytes = 128L * 1024 * 1024, TimeSpan? timeout = null)
        {
            if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathRooted(destination) ||
                destination.IndexOf('\0') >= 0)
                throw new ArgumentException("An absolute, new snapshot destination is required.",
                                            nameof(destination));
            if (maxTemporaryBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxTemporaryBytes));
            if (maxWalBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxWalBytes));
            var duration = timeout ?? TimeSpan.FromSeconds(30);
            if (duration.TotalMilliseconds < 1 || duration.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            Destination = Path.GetFullPath(destination);
            MaxTemporaryBytes = maxTemporaryBytes;
            MaxWalBytes = maxWalBytes;
            Timeout = duration;
        }
    }
}
