using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Command = Game.Media.Backup.BackupRepository.Command;

namespace Game.Media.Backup
{
    public sealed partial class ImageBackupService
    {
        public bool IsWaitingForCapacity { get; private set; }
        static BackupAcceptanceStage Acceptance(BackupState state,bool accepted,bool scheduled)
            => state==BackupState.Completed?BackupAcceptanceStage.Confirmed:
               state==BackupState.Preparing || state==BackupState.Failed && !accepted?BackupAcceptanceStage.Preparing:
               scheduled?BackupAcceptanceStage.SystemScheduled:accepted?BackupAcceptanceStage.NativeAccepted:BackupAcceptanceStage.Prepared;

        public async UniTask<BackupSubmissionResult> QuerySubmissionAsync(string operationId,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            return await QuerySubmissionCore(operationId,cancellationToken);
        }
        async UniTask<BackupSubmissionResult> QuerySubmissionCore(string operationId,CancellationToken token)
        {
            ValidateId(operationId,nameof(operationId));
            var rows=(await Db(Command.Submission,token,operationId)).Rows;
            if(rows.Count==0)return null;
            return new BackupSubmissionResult {OperationId=operationId,Items=rows.Select(row=>new BackupSubmissionItem {
                Index=(int)row.Number("ordinal"),TaskId=row.Text("id"),DetailsExpired=row.Flag("details_expired"),
                State=row.Flag("details_expired")?(BackupState?)null:(BackupState)row.Number("state"),
                Phase=row.Flag("details_expired")?(BackupProtocolPhase?)null:(BackupProtocolPhase)row.Number("protocol_phase"),Error=row.Text("error"),
                Acceptance=row.Flag("details_expired")?(BackupAcceptanceStage?)null:Acceptance((BackupState)row.Number("state"),row.Number("native_accepted")>0,row.Flag("system_scheduled"))
            }).ToList().AsReadOnly()};
        }

        /// <summary>Prepare independently, durably hand off, and obtain executor admission. Does not await upload or confirmation.</summary>
        public async UniTask<BackupSubmissionResult> SubmitAsync(string operationId,IReadOnlyList<ImageReference> images,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            ValidateId(operationId,nameof(operationId));
            if(images==null || images.Count<1 || images.Count>32)throw new ArgumentException("A submission contains 1–32 images.",nameof(images));
            var sources=images.ToArray();
            if(sources.Any(x=>x==null || string.IsNullOrEmpty(x.Version)))throw new ArgumentException("Every image requires its source identity and current version.",nameof(images));
            foreach(var image in sources)
            {
                ValidateProtocolText(image.Source+":"+image.OriginId,1024,"sourceId");
                ValidateProtocolText(image.Version,1024,"sourceVersion");
                ValidateProtocolText(image.FileName,1024,"name");
                ValidateProtocolText(image.MimeType,128,"mimeType");
            }
            if(accepting || pausing)throw new InvalidOperationException("Await the current preparation or queue control operation.");
            accepting=true;
            try {return await SubmitCore(operationId,sources,cancellationToken);}
            finally {accepting=false;IsWaitingForCapacity=false;}
        }
        async UniTask<BackupSubmissionResult> SubmitCore(string operationId,ImageReference[] sources,CancellationToken cancellationToken)
        {
            if((await Db(Command.Info,cancellationToken)).Single.Flag("paused"))throw new InvalidOperationException("Resume the backup queue before submitting photos.");
            if(await QuerySubmissionCore(operationId,cancellationToken)!=null)throw new InvalidOperationException("This operationId was already submitted. Query its results; retries are explicit operations.");
            string credential=GetAccessToken();if(string.IsNullOrWhiteSpace(credential))throw new InvalidOperationException("No access token available.");
            if(executorFailure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(executorFailure).Throw();
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
            var token=linked.Token;token.ThrowIfCancellationRequested();
            var ids=sources.Select((_,index)=>Hash(operationId+":"+index)).ToArray();
            var errors=new List<Exception>();bool prepared=false,sharedFailure=false,schedulingFailed=false,repositoryFailed=false;int next=0;
            long lastSchedule=Now;
            async UniTask Schedule()
            {
                try {await ScheduleAccepted();}
                catch {schedulingFailed=true;throw;}
            }
            try
            {
                var arguments=new List<object>{operationId,owner,Now,sources.Length};
                for(int i=0;i<sources.Length;i++)
                {
                    var image=sources[i];
                    arguments.AddRange(new object[]{ids[i],image.Source+":"+image.OriginId,image.Version,image.FileName,image.MimeType,0L});
                }
                await Db(Command.Prepare,token,arguments.ToArray());prepared=true;
                for(;next<sources.Length;next++)
                {
                    if(paused)throw new InvalidOperationException("Preparation stopped because the backup queue was paused. Query accepted items before submitting new work.");
                    token.ThrowIfCancellationRequested();var image=sources[next];IDisposable lease=null;
                    try
                    {
                        lease=image.Acquire();
                        if(image.ByteCount>Math.Min(maximum,budget))throw new BackupSourceFailure(new IOException("Image exceeds the per-file or total staging limit."));
                        // Unknown lengths reserve their full permitted size before the first read.
                        // Waiting here is backpressure, never a retry of a failed export.
                        long required=image.ByteCount??Math.Min(maximum,budget);
                        long available;
                        for(;;)
                        {
                            if(paused)throw new InvalidOperationException("Preparation stopped because the backup queue was paused.");
                            token.ThrowIfCancellationRequested();
                            var counts=(await Db(Command.FileSummary,token)).Rows;
                            long occupied=counts.Where(x=>x.Number("state")!=3).Sum(x=>x.Number("bytes"));
                            long staged=counts.Single(x=>x.Number("state")==1).Number("count");
                            long free=(await Db(Command.Storage,token)).Single.Number("available_bytes")-16L*1024*1024;
                            available=Math.Min(budget-occupied,free);
                            if(staged<64 && available>0 && required<=available)break;
                            IsWaitingForCapacity=true;
                            await Schedule();
                            await UniTask.Delay(200,ignoreTimeScale:true,cancellationToken:token);
                        }
                        IsWaitingForCapacity=false;
                        await Db(Command.ReservePayload,token,ids[next],owner,Math.Min(maximum,available),budget,Now);
                        if(image.Source!="file")await ValidateSource(image,token);
                        var payload=await PreparePayload(image,Path.Combine(root,"payloads",ids[next]+".payload"),available,token);
                        if(image.Source!="file")await ValidateSource(image,token);
                        await Db(Command.Seal,token,ids[next],owner,payload.size,payload.hash,payload.mime,budget,Now);
                        if(paused)throw new InvalidOperationException("Preparation stopped because the backup queue was paused.");
                        await Db(Command.AcceptItem,token,ids[next],owner,Now);
                        var claim=await Db(Command.Claim,default,ids[next],Executor,nativeWifiOnly,Now);
                        if(claim.Rows.Count!=1)throw new InvalidOperationException("The photo was prepared but execution admission changed. Query the submission before continuing.");
                        if(nativeEnabled)await Platform(new NativeBackupRequest {op="accept",repository=StoreId,id=ids[next],generation=claim.Single.Number("current_generation"),token=credential});
                        else
                        {
                            string reference=StoreDesktopCredential(credential);
                            try {await Db(Command.Handoff,default,ids[next],claim.Single.Number("current_generation"),reference);}
                            catch(Exception error) {if(!(error is BackupRepositoryException repositoryError && repositoryError.CommitOutcomeUnknown))desktopCredentials.Remove(reference);throw;}
                        }
                        if(Now-lastSchedule>=TimeSpan.TicksPerMillisecond*250) {await Schedule();lastSchedule=Now;}
                    }
                    catch(BackupSourceFailure failure)
                    {
                        errors.Add(failure.Original.SourceException);
                        await Db(Command.FailItem,default,ids[next],owner,failure.Original.SourceException.ToString(),Now);
                        await CleanupFilesAsync(default,32);
                    }
                    finally
                    {
                        if(lease!=null)try{lease.Dispose();}catch(Exception cleanup){errors.Add(cleanup);}
                    }
                }
            }
            catch(Exception primary)
            {
                errors.Add(primary);sharedFailure=true;
                repositoryFailed=primary is BackupRepositoryException;
                if(prepared && !(primary is BackupRepositoryException repositoryError && repositoryError.CommitOutcomeUnknown))
                {
                    // Accepted members are never abandoned. Only not-yet-accepted
                    // preparations are ended when a shared dependency stops this call.
                    for(int i=next;i<ids.Length;i++)
                    {
                        try
                        {
                            var task=(await Db(Command.Task,default,ids[i])).Rows;
                            if(task.Count==1 && task[0].Number("state")==9)
                                await Db(Command.FailItem,default,ids[i],owner,primary.ToString(),Now);
                        }
                        catch(Exception cleanup){Debug.LogException(cleanup);break;}
                    }
                }
            }
            finally
            {
                if(prepared && !repositoryFailed)
                {
                    if(!schedulingFailed)try {await Schedule();}catch(Exception scheduling){errors.Add(scheduling);sharedFailure=true;}
                    try {await CleanupFilesAsync(default,32);}catch(Exception cleanup){errors.Add(cleanup);sharedFailure=true;}
                }
            }
            BackupSubmissionResult result;
            if(repositoryFailed)throw new BackupSubmissionException(null,errors,true);
            try {result=await QuerySubmissionCore(operationId,default);}
            catch(Exception lookup)
            {
                if(errors.Count==0)throw;
                Debug.LogException(lookup);throw new BackupSubmissionException(null,errors);
            }
            if(errors.Count!=0)throw new BackupSubmissionException(result,errors,sharedFailure);
            if(result==null || result.Items.Any(x=>x.State!=BackupState.Completed && x.Acceptance<BackupAcceptanceStage.SystemScheduled))
                throw new BackupSubmissionException(result,new[]{new InvalidOperationException("Executor did not accept every submitted photo.")});
            return result;
        }

        int Executor => nativeEnabled?(Application.platform==RuntimePlatform.Android?1:2):0;
        static void ValidateProtocolText(string value,int maxBytes,string name)
        {
            if(string.IsNullOrEmpty(value) || value.Any(c=>c<32) || new System.Text.UTF8Encoding(false,true).GetByteCount(value)>maxBytes)
                throw new ArgumentException("Protocol text must be nonempty, contain no control characters, and fit its UTF-8 byte limit.",name);
        }
        async UniTask ScheduleAccepted()
        {
            if(nativeEnabled)await Platform(new NativeBackupRequest {op="wake",repository=StoreId});
            else {StartDesktopDriver();await Db(Command.SystemScheduled,default,0);}
        }
    }
}
