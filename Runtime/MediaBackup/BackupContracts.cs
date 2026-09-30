using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Game.Media.Backup
{
    public enum BackupState { Queued, Uploading, Verifying, Completed, Paused, RetryScheduled, NeedsAttention, Failed, Canceled, Preparing }
    internal sealed class BackupSourceFailure : Exception
    {
        internal readonly ExceptionDispatchInfo Original;
        internal BackupSourceFailure(Exception error) : base(error.Message, error) { Original = ExceptionDispatchInfo.Capture(error); }
    }
    public sealed class BackupBudgetExceededException : IOException
    {
        internal BackupBudgetExceededException() : base("Backup staging budget exceeded. Free space in the queue before continuing.") { }
    }

    public sealed class BackupConfiguration
    {
        public string ServerUrl { get; set; }
        public string Account { get; set; }
        public string StorageDirectory { get; set; }
        public Func<string> AccessToken { get; set; }
        public bool AllowDevelopmentHttp { get; set; }
        public bool EnableNativeBackgroundTransfer { get; set; }
        public bool NativeWifiOnly { get; set; } = true;
        public long DiskBudgetBytes { get; set; } = 512L * 1024 * 1024;
        public long MaxFileBytes { get; set; } = 512L * 1024 * 1024;
        public bool EnableTransientRetries { get; set; }
        public int MaxRetries { get; set; } = 5;
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);
    }

    [Serializable]
    public sealed class BackupTaskInfo
    {
        public string id, batchId, key, source, version, name, mime, sha256, backupId, error;
        public long sequence, generation, size, confirmedBytes, nextAttemptUtcTicks;
        internal int desiredAction;
        internal string relativePath;
        public int retries;
        public BackupState state;
        public bool nativeOwned, cleanupPending;
        public string cleanupError;
        internal BackupTaskInfo Snapshot() => (BackupTaskInfo)MemberwiseClone();
    }

    [Serializable] internal sealed class UploadRequest { public string key, sha256, name, mime, source; public long size; }
    [Serializable] internal sealed class UploadResponse { public string uploadId, sha256, backupId; public long offset, size; public bool completed; public ServerCapabilities capabilities; }
    [Serializable] internal sealed class ServerCapabilities { public int protocolVersion, chunkBytes; public long maxFileBytes; public string account; public bool backgroundUpload; }
    public sealed class BackupHttpException : Exception
    {
        public int StatusCode { get; }
        public TimeSpan? RetryAfter { get; }
        internal BackupHttpException(int status, string message, TimeSpan? retryAfter) : base(message) { StatusCode = status; RetryAfter = retryAfter; }
    }

}
