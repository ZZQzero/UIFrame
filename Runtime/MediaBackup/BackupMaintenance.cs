using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Cysharp.Threading.Tasks;
using Command = Game.Media.Backup.BackupRepository.Command;

namespace Game.Media.Backup
{
    public sealed class BackupControlCleanupInfo
    {
        public string RequestId { get; internal set; }
        public long AccountedBytes { get; internal set; }
        public string Error { get; internal set; }
        public DateTime CreatedUtc { get; internal set; }
    }
    public sealed partial class ImageBackupService
    {
        /// <summary>Bounded failure page. Continue with the last RequestId; an empty page ends enumeration.</summary>
        public async UniTask<IReadOnlyList<BackupControlCleanupInfo>> GetControlCleanupFailuresAsync(string afterRequestId="",int pageSize=100,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            if(afterRequestId==null)throw new ArgumentNullException(nameof(afterRequestId));
            if(afterRequestId.Length!=0)ValidateId(afterRequestId,nameof(afterRequestId));
            if(pageSize<1 || pageSize>200)throw new ArgumentOutOfRangeException(nameof(pageSize));
            var rows=(await Db(Command.ControlCleanupFailures,cancellationToken,afterRequestId,pageSize)).Rows;
            var result=new List<BackupControlCleanupInfo>(rows.Count);
            foreach(var row in rows)result.Add(new BackupControlCleanupInfo {RequestId=row.Text("id"),AccountedBytes=row.Number("byte_count"),Error=row.Text("cleanup_error"),CreatedUtc=new DateTime(row.Number("created_utc"),DateTimeKind.Utc)});
            return result.AsReadOnly();
        }
        /// <summary>Explicitly retries only this released control file. A failure remains queryable.</summary>
        public async UniTask RetryControlCleanupAsync(string requestId,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();ValidateId(requestId,nameof(requestId));
            await Db(Command.ControlCleanupRetry,cancellationToken,requestId,Now);
        }
    }
    public sealed class BackupRetentionPolicy
    {
        public TimeSpan HistoryAge { get; set; }=TimeSpan.FromDays(30);
        public int KeepHistoryCount { get; set; }=10000;
        public int MaximumItems { get; set; }=200;
        public TimeSpan TimeSlice { get; set; }=TimeSpan.FromMilliseconds(100);
        public bool Checkpoint { get; set; }
        internal BackupRetentionPolicy Snapshot()
        {
            if(HistoryAge<TimeSpan.Zero || HistoryAge.Ticks>DateTime.UtcNow.Ticks || KeepHistoryCount<0 || KeepHistoryCount>1000000 || MaximumItems<1 || MaximumItems>200 || TimeSlice<=TimeSpan.Zero || TimeSlice>TimeSpan.FromSeconds(10))throw new ArgumentOutOfRangeException(nameof(BackupRetentionPolicy));
            return (BackupRetentionPolicy)MemberwiseClone();
        }
    }
    public sealed class BackupStorageSummary
    {
        public long UploadBytes { get; internal set; }
        public long PreparationBytes { get; internal set; }
        /// <summary>Upper bound from pending cleanup intents; executors may still hold these files.</summary>
        public long ReclaimableBytes { get; internal set; }
        public long CleanupFailedBytes { get; internal set; }
        public long DatabaseBytes { get; internal set; }
        public long WalBytes { get; internal set; }
        public long AvailableBytes { get; internal set; }
        public int HistoryCandidatesInPage { get; internal set; }
    }
    public sealed class BackupCleanupResult
    {
        public int FilesDeleted { get; internal set; }
        public long FileBytesFreed { get; internal set; }
        public int HistoryRemoved { get; internal set; }
        public int OperationItemsRemoved { get; internal set; }
        public int MetadataRowsRemoved { get; internal set; }
        public int Skipped { get; internal set; }
        public bool TimeSliceEnded { get; internal set; }
        public IReadOnlyList<BackupFileCleanupFailure> Failures { get; internal set; }=Array.Empty<BackupFileCleanupFailure>();
    }
    public sealed class BackupFileCleanupFailure
    {
        public string FileId { get; }
        public BackupRepositoryException Error { get; }
        internal BackupFileCleanupFailure(string id,BackupRepositoryException error){FileId=id;Error=error;}
    }
    public sealed class BackupFileCleanupInfo
    {
        public string FileId { get; internal set; }
        /// <summary>The owning task, including a failed preparation.</summary>
        public string TaskId { get; internal set; }
        /// <summary>Bytes accounted against the staging budget; interrupted preparations may retain a reservation.</summary>
        public long AccountedBytes { get; internal set; }
        public string Error { get; internal set; }
        public DateTime UpdatedUtc { get; internal set; }
    }
    public sealed class BackupFileCleanupCursor
    {
        internal string Store,After;
    }
    public sealed class BackupFileCleanupPage
    {
        public IReadOnlyList<BackupFileCleanupInfo> Items { get; internal set; }
        public BackupFileCleanupCursor Next { get; internal set; }
    }
    public sealed class BackupCleanupException : Exception
    {
        public BackupCleanupResult Result { get; }
        internal BackupCleanupException(BackupCleanupResult result)
            :base("One or more files could not be cleaned. See Result.Failures and the persisted file state.",result.Failures[0].Error){Result=result;}
    }
    /// <summary>Bounded maintenance. Preview is advisory; native transactions recheck all ownership before deleting.</summary>
    public sealed class BackupMaintenance
    {
        readonly ImageBackupService service;
        bool running;
        public BackupMaintenance(ImageBackupService service){this.service=service??throw new ArgumentNullException(nameof(service));}
        public async UniTask<BackupStorageSummary> PreviewAsync(BackupRetentionPolicy policy=null,CancellationToken token=default)
        {
            using var operation=service.EnterOperation();
            var p=(policy??new BackupRetentionPolicy()).Snapshot();var result=new BackupStorageSummary();
            foreach(var row in (await service.Db(Command.FileSummary,token)).Rows)
            {
                switch(row.Number("state"))
                {
                    case 0:result.PreparationBytes+=row.Number("bytes");break;
                    case 1:result.UploadBytes+=row.Number("bytes");break;
                    case 2:case 5:result.ReclaimableBytes+=row.Number("bytes");break;
                    case 4:result.CleanupFailedBytes+=row.Number("bytes");break;
                }
            }
            var storage=(await service.Db(Command.Storage,token)).Single;
            result.DatabaseBytes=storage.Number("database_bytes");result.WalBytes=storage.Number("wal_bytes");result.AvailableBytes=storage.Number("available_bytes");
            result.HistoryCandidatesInPage=(await service.Db(Command.HistoryPage,token,0,ImageBackupService.Now-p.HistoryAge.Ticks,p.KeepHistoryCount,p.MaximumItems)).Rows.Count;
            return result;
        }
        public async UniTask<BackupCleanupResult> RunAsync(BackupRetentionPolicy policy=null,CancellationToken token=default)
        {
            using var operation=service.EnterOperation();
            MediaThread.Check();var p=(policy??new BackupRetentionPolicy()).Snapshot();if(running)throw new InvalidOperationException("This maintenance runner is already active.");running=true;
            var result=new BackupCleanupResult();var elapsed=Stopwatch.StartNew();long before=ImageBackupService.Now-p.HistoryAge.Ticks;int remaining=p.MaximumItems;
            var failures=new List<BackupFileCleanupFailure>();
            // Selection and filesystem calls cannot be preempted. A selected first
            // item gets one attempt even if selection used the entire soft slice.
            bool SliceEnded()=>remaining<p.MaximumItems && elapsed.Elapsed>=p.TimeSlice;
            BackupCleanupResult Complete()
            {
                if(failures.Count==0)return result;
                result.Failures=failures.AsReadOnly();throw new BackupCleanupException(result);
            }
            try
            {
                var files=(await service.Db(Command.CleanupPage,token,"",remaining)).Rows;
                foreach(var row in files)
                {
                    if(SliceEnded()){result.TimeSliceEnded=true;return Complete();}
                    try {
                        var deleted=(await service.Db(Command.CleanupRun,token,row.Text("id"),row.Number("updated_utc"),ImageBackupService.Now)).Single;
                        result.FilesDeleted+=(int)deleted.Number("deleted");result.FileBytesFreed+=deleted.Number("freed_bytes");if(deleted.Number("deleted")==0)result.Skipped++;
                    }
                    catch(BackupRepositoryException error) when(error.IsIsolatedCleanupFailure){failures.Add(new BackupFileCleanupFailure(row.Text("id"),error));}
                    remaining--;
                }
                if(remaining>0)
                {
                    if(SliceEnded()){result.TimeSliceEnded=true;return Complete();}
                    var history=(await service.Db(Command.HistoryPage,token,0,before,p.KeepHistoryCount,remaining)).Rows;
                    foreach(var row in history)
                    {
                        if(SliceEnded()){result.TimeSliceEnded=true;return Complete();}
                        var deletion=await service.Db(Command.PruneTask,token,row.Text("id"),row.Number("updated_utc"));
                        if(deletion.Tables[2].Count!=0)result.HistoryRemoved++;else result.Skipped++;remaining--;
                    }
                }
                if(remaining>0 && !SliceEnded())
                {
                    var operations=(await service.Db(Command.OperationCleanupPage,token,before,Math.Min(32,remaining))).Rows;
                    foreach(var row in operations)
                    {
                        if(SliceEnded()){result.TimeSliceEnded=true;return Complete();}
                        var pruned=await service.Db(Command.PruneOperation,token,row.Text("id"),before,remaining);
                        int removed=checked((int)pruned.Tables[1][0].Number("removed"));result.OperationItemsRemoved+=removed;
                        remaining-=Math.Max(1,removed);if(remaining==0)break;
                    }
                    if(remaining>0 && !SliceEnded())result.MetadataRowsRemoved=(int)(await service.Db(Command.PruneScans,token,remaining)).Single.Number("removed");
                }
                if(p.Checkpoint && elapsed.Elapsed<p.TimeSlice)await service.Db(Command.Checkpoint,token);
                result.TimeSliceEnded=elapsed.Elapsed>=p.TimeSlice;
                return Complete();
            }
            finally {running=false;}
        }
        /// <summary>Explicitly truncate notification history; older consumers must refresh pages.</summary>
        public async UniTask PruneChangesAsync(long throughSequence,CancellationToken token=default)
        {
            using var operation=service.EnterOperation(); if(throughSequence<0)throw new ArgumentOutOfRangeException(nameof(throughSequence));await service.Db(Command.PruneChanges,token,throughSequence,200); }
    }
}
