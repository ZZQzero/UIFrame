using System;
using System.Collections.Generic;

namespace Game.Media.Backup
{
    public sealed class BackupTaskQuery
    {
        public BackupState? State { get; set; }
        public int PageSize { get; set; } = 100;
        public BackupTaskCursor Cursor { get; set; }
    }
    public sealed class BackupTaskCursor
    {
        internal readonly string Store;
        internal readonly BackupState? State;
        internal readonly long After, Upper;
        internal BackupTaskCursor(string store,BackupState? state,long after,long upper)
        { Store=store; State=state; After=after; Upper=upper; }
    }
    public sealed class BackupTaskPage
    {
        public IReadOnlyList<BackupTaskInfo> Items { get; }
        public BackupTaskCursor Next { get; }
        internal BackupTaskPage(IReadOnlyList<BackupTaskInfo> items,BackupTaskCursor next) { Items=items;Next=next; }
    }
    public sealed class BackupSummary
    {
        readonly long[] counts;
        public bool QueuePaused { get; }
        public long this[BackupState state] => (int)state>=0 && (int)state<counts.Length?counts[(int)state]:throw new ArgumentOutOfRangeException(nameof(state));
        internal BackupSummary(long[] counts,bool paused) { this.counts=counts; QueuePaused=paused; }
    }
    public sealed class BackupRecord
    {
        public string BackupId { get; internal set; }
        public string Source { get; internal set; }
        public string Version { get; internal set; }
        public string Sha256 { get; internal set; }
        public long ByteCount { get; internal set; }
        public string Name { get; internal set; }
        public string MimeType { get; internal set; }
        public DateTime ConfirmedUtc { get; internal set; }
    }
    public sealed class BackupReceiptCursor
    {
        internal readonly string Store,AfterId;
        internal readonly long AfterTime;
        internal BackupReceiptCursor(string store,long time,string id) { Store=store;AfterTime=time;AfterId=id; }
    }
    public sealed class BackupReceiptPage
    {
        public IReadOnlyList<BackupRecord> Items { get; }
        public BackupReceiptCursor Next { get; }
        internal BackupReceiptPage(IReadOnlyList<BackupRecord> items,BackupReceiptCursor next) { Items=items; Next=next; }
    }
    public sealed class BackupChangeCursor
    {
        internal string Store;
        internal long Sequence;
    }
    public sealed class BackupChange
    {
        public long Sequence {get;internal set;}
        public string TaskId {get;internal set;}
        public long Generation {get;internal set;}
        public bool Removed {get;internal set;}
    }
    public sealed class BackupChangePage
    {
        public IReadOnlyList<BackupChange> Items {get;internal set;}
        public BackupChangeCursor Next {get;internal set;}
        public bool RequiresRefresh {get;internal set;}
    }

}
