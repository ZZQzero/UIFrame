using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Command = Game.Media.Backup.BackupRepository.Command;

namespace Game.Media.Backup
{
    public enum BackupAction { Resume, Pause, Cancel, Retry, RetryCleanup, ClearHistory }
    public enum BackupOperationPhase { Selecting, Running, Completed, Failed }
    public enum BackupOperationOutcome { Pending, Applied, NotApplicable, Missing, Failed, WaitingForRelease, AlreadySatisfied }
    public sealed class BackupOperationCommand
    {
        public string OperationId { get; set; }
        public BackupAction Action { get; set; }
        public BackupState? State { get; set; }
        public IReadOnlyList<string> TaskIds { get; set; }
    }
    public sealed class BackupOperationItem
    {
        public string TaskId { get; internal set; }
        public BackupOperationOutcome Outcome { get; internal set; }
        public string Reason { get; internal set; }
    }
    public sealed class BackupOperationCursor
    {
        internal string Store,Operation,After;
    }
    public sealed class BackupOperationStatus
    {
        public string OperationId { get; internal set; }
        public BackupAction Action { get; internal set; }
        public BackupOperationPhase Phase { get; internal set; }
        public long SelectedCount { get; internal set; }
        public long AppliedCount { get; internal set; }
        public long SelectionCursor { get; internal set; }
        public bool DetailsExpired { get; internal set; }
        public string Error { get; internal set; }
        public IReadOnlyList<BackupOperationItem> Items { get; internal set; }
        public BackupOperationCursor Next { get; internal set; }
        public bool IsFinished => Phase==BackupOperationPhase.Completed || Phase==BackupOperationPhase.Failed;
    }
    public sealed class BackupPreparationStatus
    {
        public string OperationId { get; internal set; }
        public bool Accepted { get; internal set; }
        public bool Abandoned { get; internal set; }
        public string Error { get; internal set; }
        public IReadOnlyList<string> TaskIds { get; internal set; }
    }
    public sealed partial class ImageBackupService
    {
        public async UniTask<BackupChangePage> ReadChangesAsync(BackupChangeCursor cursor=null,int pageSize=100,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            if(pageSize<1 || pageSize>199)throw new ArgumentOutOfRangeException(nameof(pageSize));
            if(cursor!=null && cursor.Store!=StoreId)throw new ArgumentException("Cursor belongs to another backup repository.");
            long after=cursor?.Sequence??0;var result=await Db(Command.Changes,cancellationToken,after,pageSize);
            var header=result.Tables[0][0];bool refresh=header.Flag("requires_refresh");
            var items=refresh?Array.Empty<BackupChange>():(IReadOnlyList<BackupChange>)result.Rows.Select(row=>new BackupChange {Sequence=row.Number("sequence"),TaskId=row.Text("object_id"),Generation=row.Number("generation"),Removed=row.Number("kind")==2}).ToList().AsReadOnly();
            return new BackupChangePage {Items=items,RequiresRefresh=refresh,Next=new BackupChangeCursor {Store=StoreId,Sequence=refresh?header.Number("retained_after_seq"):items.Count==0?after:items[items.Count-1].Sequence}};
        }
        public async UniTask RecoverNativeAsync(CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();cancellationToken.ThrowIfCancellationRequested();if(!nativeEnabled)throw new InvalidOperationException("Native background transfer is not enabled.");
            await Platform(new NativeBackupRequest {op="recover",repository=StoreId});
        }
        bool drivingOperations;
        long operationWakeVersion;
        Exception operationDriveFailure;
        internal static void ValidateId(string value,string parameter)
        {
            if(string.IsNullOrEmpty(value) || value.Length>64 || value.Any(c=>!(c>='0' && c<='9' || c>='a' && c<='f')))
                throw new ArgumentException("A lowercase hexadecimal identity (1-64 characters) is required.",parameter);
        }
        public async UniTask<IReadOnlyList<string>> EnqueueAsync(string operationId,IReadOnlyList<ImageReference> images,CancellationToken cancellationToken=default)
        {
            ValidateId(operationId,nameof(operationId));
            try { return await EnqueueBatchAsync(images,cancellationToken,operationId); }
            catch(BackupSourceFailure failure) { failure.Original.Throw();throw; }
        }
        /// <summary>Reconcile an uncertain acceptance using the original caller-owned ID, including after task history is pruned.</summary>
        public async UniTask<BackupPreparationStatus> QueryPreparationAsync(string operationId,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            ValidateId(operationId,nameof(operationId));var result=await Db(Command.Preparation,cancellationToken,operationId);
            if(result.Tables[0].Count==0)return null;var row=result.Tables[0][0];
            return new BackupPreparationStatus { OperationId=operationId,Accepted=row.Number("phase")==1,Abandoned=row.Number("phase")==2,
                Error=row.Text("error"),TaskIds=result.Rows.Select(r=>r.Text("id")).ToList().AsReadOnly() };
        }
        static BackupOperationStatus Operation(BackupRepository.Row row) => new BackupOperationStatus {
            OperationId=row.Text("id"),Action=(BackupAction)row.Number("action"),Phase=(BackupOperationPhase)row.Number("phase"),
            SelectedCount=row.Number("selected_count"),AppliedCount=row.Number("applied_count"),SelectionCursor=row.Number("selection_cursor"),
            DetailsExpired=row.Flag("details_expired"),Error=row.Text("error"),Items=Array.Empty<BackupOperationItem>() };
        /// <summary>Durable admission. The caller owns OperationId before this call; cancellation after admission cannot undo it.</summary>
        public async UniTask<string> SubmitOperationAsync(BackupOperationCommand command,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            Check();if(command==null)throw new ArgumentNullException(nameof(command));ValidateId(command.OperationId,nameof(command.OperationId));
            if(!Enum.IsDefined(typeof(BackupAction),command.Action) || command.State.HasValue && !Enum.IsDefined(typeof(BackupState),command.State.Value))throw new ArgumentOutOfRangeException(nameof(command));
            if(command.TaskIds!=null && (command.TaskIds.Count<1 || command.TaskIds.Count>128 || command.State.HasValue))throw new ArgumentException("Use either a state filter or 1-128 explicit task identities.");
            string id=command.OperationId;var action=command.Action;int state=command.State.HasValue?(int)command.State.Value:-1;
            var targets=command.TaskIds?.ToArray()??Array.Empty<string>();foreach(string target in targets)ValidateId(target,nameof(command.TaskIds));
            if(targets.Distinct(StringComparer.Ordinal).Count()!=targets.Length)throw new ArgumentException("Duplicate operation targets.");
            // Target set order does not change the meaning or identity of a command.
            Array.Sort(targets,StringComparer.Ordinal);
            var values=new List<object>{(int)action,state};values.AddRange(targets);
            byte[] fingerprint;using(var sha=SHA256.Create())fingerprint=sha.ComputeHash(BackupRepository.Encode(values.ToArray()));
            values=new List<object>{id,(int)action,state,Now,targets.Length,fingerprint};values.AddRange(targets);
            await Db(Command.Operation,cancellationToken,values.ToArray());StartOperationDriver();return id;
        }
        public async UniTask<BackupOperationStatus> QueryOperationAsync(string operationId,BackupOperationCursor cursor=null,int pageSize=100,CancellationToken cancellationToken=default)
        {using var operation=EnterOperation();return await QueryOperationCoreAsync(operationId,cursor,pageSize,cancellationToken);}
        async UniTask<BackupOperationStatus> QueryOperationCoreAsync(string operationId,BackupOperationCursor cursor,int pageSize,CancellationToken cancellationToken)
        {
            ValidateId(operationId,nameof(operationId));if(pageSize<1 || pageSize>200)throw new ArgumentOutOfRangeException(nameof(pageSize));
            if(cursor!=null && (cursor.Store!=StoreId || cursor.Operation!=operationId))throw new ArgumentException("Cursor belongs to another operation.");
            var rows=(await Db(Command.OperationStatus,cancellationToken,operationId)).Rows;if(rows.Count==0)return null;
            var status=Operation(rows[0]);if(status.DetailsExpired)return status;
            var items=(await Db(Command.OperationItems,cancellationToken,operationId,cursor?.After??"",pageSize)).Rows;
            status.Items=items.Select(r=>new BackupOperationItem {TaskId=r.Text("task_id"),Outcome=(BackupOperationOutcome)r.Number("outcome"),Reason=r.Text("error")}).ToList().AsReadOnly();
            if(items.Count==pageSize)status.Next=new BackupOperationCursor {Store=StoreId,Operation=operationId,After=status.Items[items.Count-1].TaskId};
            return status;
        }
        public async UniTask<BackupOperationStatus> WaitOperationAsync(string operationId,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);cancellationToken=linked.Token;
            Check();for(;;)
            {
                var result=await QueryOperationCoreAsync(operationId,null,1,cancellationToken);
                if(result==null)throw new ArgumentException("Operation does not exist.",nameof(operationId));if(result.IsFinished)return result;
                if(operationDriveFailure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(operationDriveFailure).Throw();
                await UniTask.Delay(100,ignoreTimeScale:true,cancellationToken:cancellationToken);
            }
        }
        void StartOperationDriver()
        {
            operationWakeVersion++;
            if(drivingOperations || operationDriveFailure!=null || lifetime.IsCancellationRequested)return;
            DriveOperations().Forget(error=>Debug.LogException(error));
        }
        async UniTask DriveOperations()
        {
            drivingOperations=true;
            try
            {
                for(;;)
                {
                    long observedWake=operationWakeVersion;
                    bool work=false;
                    for(int phase=0;phase<=1;phase++)
                    {
                        string after="";
                        for(;;)
                        {
                            var page=(await Db(Command.Operations,lifetime.Token,after,phase,32)).Rows;
                            foreach(var row in page)
                            {
                                work=true;after=row.Text("id");
                                try {await Db(phase==0?Command.SelectOperation:Command.ApplyOperation,lifetime.Token,after,Now,phase==0?128:32);}
                                catch(OperationCanceledException) when(lifetime.IsCancellationRequested){throw;}
                                catch(Exception failure)
                                {
                                    // A durably failed operation has a fixed boundary;
                                    // later independent operations can still progress.
                                    BackupRepository.Result persisted;
                                    try{persisted=await Db(Command.OperationStatus,default,after);}
                                    catch(Exception diagnostic){Debug.LogException(diagnostic);System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();throw;}
                                    if(persisted.Rows.Count!=1 || persisted.Single.Number("phase")!=(int)BackupOperationPhase.Failed)throw;
                                    Debug.LogException(failure);continue;
                                }
                                if(phase==1)
                                {
                                    await SynchronizeNativeAsync();
                                }
                                await UniTask.Yield(PlayerLoopTiming.Update,lifetime.Token);
                            }
                            if(page.Count<32)break;
                        }
                    }
                    if(!work && observedWake==operationWakeVersion)break;
                    await UniTask.Delay(100,ignoreTimeScale:true,cancellationToken:lifetime.Token);
                }
            }
            catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
            catch(Exception error){operationDriveFailure=error;throw;}
            finally {drivingOperations=false;}
        }
        /// <summary>Owns the server identity while checking and closing an incomplete session. Retry intent may be queued; a new attempt waits for this check to end.</summary>
        public async UniTask ReconcileTaskAsync(string taskId,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);cancellationToken=linked.Token;
            Check();var record=TaskInfo((await Db(Command.BeginReconcile,cancellationToken,taskId)).Single);
            metadataReads++;var cleanup=new UIFrame.CleanupFailure();
            try
            {
                UploadResponse response=null;
                try {response=await JsonRequest<UploadResponse>(System.Net.Http.HttpMethod.Get,"/v1/uploads/"+record.key,null,cancellationToken);}
                catch(BackupHttpException error) when(error.StatusCode==404 || error.StatusCode==410) { }
                if(response!=null)
                {
                    ValidateResponse(record,response);
                    if(response.completed)await Confirm(record,response);
                    else
                        // The repository lease prevents a new attempt using this
                        // identity for the entire remote check, including DELETE.
                        await JsonRequest<UploadResponse>(System.Net.Http.HttpMethod.Delete,"/v1/uploads/"+record.key,null,cancellationToken);
                }
                if(response==null || !response.completed)await Finish(record,5,"Server confirmed no committed backup; previous session closed");
                await CleanupFilesAsync(cancellationToken);
            }
            catch(Exception error){cleanup.Capture(error);}
            finally
            {
                try{await Db(Command.EndReconcile,default,record.key);}catch(Exception error){cleanup.Capture(error);}
                metadataReads--;
            }
            cleanup.Throw();
        }
    }
}
