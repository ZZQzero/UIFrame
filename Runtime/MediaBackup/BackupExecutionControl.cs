using System;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Command = Game.Media.Backup.BackupRepository.Command;

namespace Game.Media.Backup
{
    public sealed partial class ImageBackupService
    {
        async UniTask AdmitReady(CancellationToken token)
        {
            long after=0,upper=(await Db(Command.Info,token)).Single.Number("upper_sequence");string credential=null;
            for(;;)
            {
                var rows=(await Db(Command.Ready,token,after,upper,32)).Rows;
                foreach(var row in rows)
                {
                    after=row.Number("sequence");credential=credential??GetAccessToken();
                    if(string.IsNullOrWhiteSpace(credential))throw new InvalidOperationException("No access token available.");
                    string id=row.Text("id");long generation=row.Number("current_generation");
                    bool resume=generation>0 && row.Number("protocol_phase")<3;
                    if(!resume)
                    {
                        var claim=await Db(Command.Claim,token,id,Executor,nativeWifiOnly,Now);
                        if(claim.Rows.Count==0)continue;
                        generation=claim.Single.Number("current_generation");
                    }
                    if(nativeEnabled)await Platform(new NativeBackupRequest {op=resume?"resume":"accept",repository=StoreId,id=id,generation=generation,token=credential});
                    else
                    {
                        string reference=StoreDesktopCredential(credential);
                        try
                        {
                            if(resume)await Db(Command.ProtocolResume,default,id,reference,Now);
                            else await Db(Command.Handoff,default,id,generation,reference);
                        }
                        catch(Exception error) {if(!(error is BackupRepositoryException repositoryError && repositoryError.CommitOutcomeUnknown))desktopCredentials.Remove(reference);throw;}
                    }
                }
                if(rows.Count<32)break;
            }
        }
        public async UniTask PauseAsync(CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            if(pausing)throw new InvalidOperationException("Another queue control operation is active.");
            pausing=true;
            try
            {
                await Db(Command.Pause,cancellationToken,true);paused=true;
                await SynchronizeNativeAsync();
                await WaitExecutorRelease(null,cancellationToken);
                using var stopping=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
                await UniTask.WaitUntil(()=>!preparing && !registering,cancellationToken:stopping.Token);
                if(preparationFailure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(preparationFailure).Throw();
            }
            finally {pausing=false;}
        }
        public async UniTask ResumeAsync(CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            if(pausing)throw new InvalidOperationException("Another queue control operation is active.");
            pausing=true;
            try
            {
                await Db(Command.Pause,cancellationToken,false);paused=false;
                long after=0,upper=(await Db(Command.Info,cancellationToken)).Single.Number("upper_sequence");
                for(;;)
                {
                    var rows=(await Db(Command.Tasks,cancellationToken,after,upper,(int)BackupState.Paused,32)).Rows;
                    foreach(var row in rows)
                    {
                        after=row.Number("sequence");
                        if(row.Number("desired_action")==0)await Db(Command.Action,cancellationToken,row.Text("id"),0,Now);
                    }
                    if(rows.Count<32)break;
                }
                await AdmitReady(cancellationToken);await ScheduleAccepted();
            }
            finally {pausing=false;}
        }
        public async UniTask RetryAsync(string taskId,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();ValidateId(taskId,nameof(taskId));
            var task=await GetTaskCoreAsync(taskId,cancellationToken);
            if(task==null)throw new ArgumentException("Task does not exist.",nameof(taskId));
            if(task.state==BackupState.NeedsAttention)throw new InvalidOperationException("Reconcile the unknown server result before requesting another upload attempt.");
            if(string.IsNullOrEmpty(task.sha256))throw new InvalidOperationException("This photo was not prepared successfully. Submit its current source reference again.");
            await Db(Command.Action,cancellationToken,taskId,3,Now);
            await AdmitReady(cancellationToken);await ScheduleAccepted();
        }
        public async UniTask CancelAsync(string taskId,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();ValidateId(taskId,nameof(taskId));
            await Db(Command.Action,cancellationToken,taskId,2,Now);
            var current=(await Db(Command.Task,cancellationToken,taskId)).Single;
            if(current.Number("state")==6 && current.Flag("credential_released"))await ReconcileCore(taskId,true,cancellationToken);
            await SynchronizeNativeAsync();await WaitExecutorRelease(taskId,cancellationToken);
            var result=await GetTaskCoreAsync(taskId,cancellationToken);
            if(result.state!=BackupState.Canceled && result.state!=BackupState.Completed)
                throw new InvalidOperationException("Cancellation has not been confirmed: "+result.error);
            await CleanupFilesAsync(cancellationToken);
        }
        async UniTask WaitExecutorRelease(string taskId,CancellationToken token)
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);token=linked.Token;
            for(;;)
            {
                bool held;
                if(taskId==null)held=(await Db(Command.Attempts,token,0,Executor,1)).Rows.Count!=0;
                else
                {
                    var rows=(await Db(Command.Task,token,taskId)).Rows;
                    held=rows.Count!=0 && rows[0].Number("current_generation")>0 && (!rows[0].Flag("payload_released") || !rows[0].Flag("credential_released"));
                }
                if(!held)return;
                if(nativeEnabled)await Platform(new NativeBackupRequest {op="sync",repository=StoreId});
                else if(executorFailure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(executorFailure).Throw();
                await UniTask.Delay(100,ignoreTimeScale:true,cancellationToken:token);
            }
        }
        internal async UniTask SynchronizeNativeAsync()
        {
            bool queuePaused=(await Db(Command.Info)).Single.Flag("paused");
            if(nativeEnabled) {
                await Platform(new NativeBackupRequest {op="sync",repository=StoreId});
                if(!queuePaused){await AdmitReady(default);await ScheduleAccepted();}
                return;
            }
            foreach(var work in desktopUploads.Values.ToArray())
            {
                var row=(await Db(Command.Task,default,work.Id)).Single;
                if(queuePaused || row.Number("desired_action")!=0)
                {work.Pausing=row.Number("desired_action")!=2 && (queuePaused || row.Number("desired_action")==1);work.Cancellation.Cancel();}
            }
            if(queuePaused) {
                if(desktopControl!=null && desktopControl.ControlKind!=2){desktopControl.Pausing=true;desktopControl.Cancellation.Cancel();}
                foreach(var control in (await Db(Command.Controls,default,0,"")).Rows)
                    if(control.Number("kind")!=2 && control.Number("state")<=1 && control.Text("id")!=desktopControl?.Id) {
                        await Db(Command.ControlFail,default,control.Text("id"),"Paused before system submission",Now,true);
                        await Db(Command.ControlRelease,default,control.Text("id"));
                    }
            }
            if(!queuePaused)await AdmitReady(default);
            StartDesktopDriver();
        }
        public async UniTask ReconcileTaskAsync(string taskId,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            await ReconcileCore(taskId,false,cancellationToken);await ScheduleAccepted();
        }
        async UniTask ReconcileCore(string taskId,bool cancel,CancellationToken token)
        {
            ValidateId(taskId,nameof(taskId));token.ThrowIfCancellationRequested();
            string credential=GetAccessToken();if(string.IsNullOrWhiteSpace(credential))throw new InvalidOperationException("No access token available.");
            if(nativeEnabled)await Platform(new NativeBackupRequest {op="reconcile",repository=StoreId,id=taskId,token=credential,cancel=cancel});
            else
            {
                string reference=StoreDesktopCredential(credential);
                try {await Db(Command.ProtocolReconcile,token,taskId,Now,cancel,reference);}
                catch(Exception error) {if(!(error is BackupRepositoryException repositoryError && repositoryError.CommitOutcomeUnknown))desktopCredentials.Remove(reference);throw;}
            }
        }
    }
}
