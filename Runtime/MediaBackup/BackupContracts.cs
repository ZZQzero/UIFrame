using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace Game.Media.Backup
{
    public enum BackupState { Queued, Uploading, Verifying, Completed, Paused, NeedsAttention=6, Failed, Canceled, Preparing }
    public enum BackupTransferMode { Automatic, UserInitiated }
    public enum BackupProtocolPhase { Registration, FileTransfer, Confirmation, Finished }
    public enum BackupAcceptanceStage { Registered, Preparing, Prepared, NativeAccepted, SystemScheduled, Confirmed }
    internal sealed class BackupSourceFailure : Exception
    {
        internal readonly ExceptionDispatchInfo Original;
        internal BackupSourceFailure(Exception error) : base(error.Message, error) { Original = ExceptionDispatchInfo.Capture(error); }
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
        public long DiskBudgetBytes { get; set; } = 1024L * 1024 * 1024;
        public long MaxFileBytes { get; set; } = 512L * 1024 * 1024;
        public BackupTransferMode TransferMode { get; set; }
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);
    }

    [Serializable]
    public sealed class BackupTaskInfo
    {
        public string id, batchId, source, version, name, mime, sha256, backupId, error;
        public long sequence, generation, size, confirmedBytes;
        internal int desiredAction;
        public BackupState state;
        public bool nativeOwned, cleanupPending;
        public BackupProtocolPhase phase;
        public BackupAcceptanceStage acceptance;
        public string cleanupError;
    }

    public sealed class BackupSubmissionItem
    {
        public int Index { get; internal set; }
        public string TaskId { get; internal set; }
        public bool DetailsExpired { get; internal set; }
        public BackupState? State { get; internal set; }
        public BackupAcceptanceStage? Acceptance { get; internal set; }
        public BackupProtocolPhase? Phase { get; internal set; }
        public string Error { get; internal set; }
    }
    public sealed class BackupSubmissionResult
    {
        public string OperationId { get; internal set; }
        public IReadOnlyList<BackupSubmissionItem> Items { get; internal set; }
        public IReadOnlyList<string> AcceptedTaskIds => Items.Where(x=>x.Acceptance>=BackupAcceptanceStage.NativeAccepted).Select(x=>x.TaskId).ToList().AsReadOnly();
    }
    public sealed class BackupSubmissionException : AggregateException
    {
        public BackupSubmissionResult Result { get; }
        internal IReadOnlyList<Exception> SharedFailures { get; }
        public bool HasSharedFailure => SharedFailures.Count!=0;
        internal BackupSubmissionException(BackupSubmissionResult result,IEnumerable<Exception> errors,IReadOnlyList<Exception> sharedFailures)
            :base("Some photos could not be submitted. Accepted tasks remain owned by their executor.",errors) {Result=result;SharedFailures=sharedFailures;}
    }
    [Serializable] internal sealed class BackupUploadHeader { public string name,value; }
    [Serializable] internal sealed class BackupUploadDescriptor
    {
        public string uploadId,clientTaskId,sha256,url;
        public long attemptGeneration,byteCount,expiresAt;
        public BackupUploadHeader[] headers;
        public int[] successStatusCodes;
    }
    public sealed class BackupHttpException : Exception
    {
        public int StatusCode { get; }
        public TimeSpan? RetryAfter { get; }
        internal BackupHttpException(int status, string message, TimeSpan? retryAfter) : base(message) { StatusCode = status; RetryAfter = retryAfter; }
    }

}
