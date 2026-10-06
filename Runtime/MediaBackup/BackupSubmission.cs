using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using UnityEngine;
using Command = Game.Media.Backup.BackupRepository.Command;

namespace Game.Media.Backup
{
    /// <summary>A registered request. Waiting never owns or cancels its preparation.</summary>
    public sealed class BackupSubmission
    {
        internal readonly TaskCompletionSource<BackupSubmissionResult> Completion = new TaskCompletionSource<BackupSubmissionResult>();
        public string OperationId { get; }
        public bool IsCompleted => Completion.Task.IsCompleted;
        internal BackupSubmission(string id) { OperationId=id; }
        public async UniTask<BackupSubmissionResult> WaitAsync(CancellationToken token=default)
        { MediaThread.Check();return await Completion.Task.AsUniTask().AttachExternalCancellation(token); }
    }
    [Flags]
    public enum BackupCapacityWaitReason { None=0, StagingBudget=1, DiskSpace=2, FileWindow=4, PreparationSlot=64 }
    public sealed class BackupCapacityWait
    {
        public string OperationId { get; internal set; }
        public string TaskId { get; internal set; }
        public BackupCapacityWaitReason Reasons { get; internal set; }
        public long RequiredBytes { get; internal set; }
        public long AvailableBytes { get; internal set; }
        public long OccupiedBytes { get; internal set; }
        public long StagedFiles { get; internal set; }
        public DateTime ObservedUtc { get; internal set; }
    }
    public sealed partial class ImageBackupService
    {
        sealed class Preparation
        {
            internal BackupSubmission Handle;
            internal ImageReference[] Sources;
            internal IDisposable[] Leases;
            internal string[] Ids;
            internal string Scope;
            internal readonly List<Exception> Errors=new List<Exception>();
            internal readonly List<Exception> SharedErrors=new List<Exception>();
            internal bool RepositoryFailed;
            internal void Error(Exception error,bool shared=true) {Errors.Add(error);if(shared)SharedErrors.Add(error);}
        }
        readonly List<Preparation> preparations=new List<Preparation>();
        long preparationWake;
        void SignalPreparation() { preparationWake++; }
        bool preparing;
        int preparationItems;
        Exception preparationFailure;
        public int AvailablePreparationSlots => Math.Max(0,128-preparationItems);
        internal long PreparationRevision { get; private set; }
        public BackupCapacityWait CapacityWait { get; private set; }
        public bool IsWaitingForCapacity => CapacityWait!=null;

        static BackupAcceptanceStage Acceptance(BackupState state,bool native,bool scheduled,bool started)
        {
            if(state==BackupState.Completed)return BackupAcceptanceStage.Confirmed;
            if(scheduled)return BackupAcceptanceStage.SystemScheduled;
            if(native)return BackupAcceptanceStage.NativeAccepted;
            if(state==BackupState.Preparing || state==BackupState.Failed)return started?BackupAcceptanceStage.Preparing:BackupAcceptanceStage.Registered;
            return BackupAcceptanceStage.Prepared;
        }
        public async UniTask<BackupSubmissionResult> QuerySubmissionAsync(string operationId,CancellationToken cancellationToken=default)
        {using var operation=EnterOperation();return await QuerySubmissionCore(operationId,cancellationToken);}
        async UniTask<BackupSubmissionResult> QuerySubmissionCore(string operationId,CancellationToken token)
        {
            ValidateId(operationId,nameof(operationId));var rows=(await Db(Command.Submission,token,operationId)).Rows;
            if(rows.Count==0)return null;
            return new BackupSubmissionResult {OperationId=operationId,Items=rows.Select(row=>new BackupSubmissionItem {
                Index=(int)row.Number("ordinal"),TaskId=row.Text("id"),DetailsExpired=row.Flag("details_expired"),
                State=row.Flag("details_expired")?(BackupState?)null:(BackupState)row.Number("state"),
                Acceptance=row.Flag("details_expired")?(BackupAcceptanceStage?)null:Acceptance((BackupState)row.Number("state"),row.Flag("native_accepted"),row.Flag("system_scheduled"),row.Flag("preparation_started")),
                Phase=row.Flag("details_expired")?(BackupProtocolPhase?)null:(BackupProtocolPhase)row.Number("protocol_phase"),Error=row.Text("error")
            }).ToArray()};
        }
        /// <summary>Register fixed members and retain their sources. Returns before preparation or OS handoff.</summary>
        public UniTask<BackupSubmission> SubmitAsync(string operationId,IReadOnlyList<ImageReference> images,CancellationToken cancellationToken=default)
            => RegisterSubmission(operationId,images,null,0,cancellationToken);
        internal async UniTask<BackupSubmission> RegisterSubmission(string operationId,IReadOnlyList<ImageReference> images,string scope,long epoch,CancellationToken token)
        {
            using var operation=EnterOperation();ValidateId(operationId,nameof(operationId));
            if(images==null || images.Count<1 || images.Count>32)throw new ArgumentException("A submission contains 1–32 images.",nameof(images));
            if(registering || pausing)throw new InvalidOperationException("Await the active registration or queue control operation.");
            if(preparationFailure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(preparationFailure).Throw();
            if(images.Count>AvailablePreparationSlots)throw new InvalidOperationException("The preparation queue holds 128 members; await existing requests before registering more.");
            var sources=images.ToArray();
            foreach(var image in sources) {
                if(image==null || string.IsNullOrEmpty(image.Version))throw new ArgumentException("Every image requires its current version.",nameof(images));
                ValidateProtocolText(image.Source+":"+image.OriginId,1024,"sourceId");ValidateProtocolText(image.Version,1024,"sourceVersion");
                ValidateProtocolText(image.FileName,1024,"name");ValidateProtocolText(image.MimeType,128,"mime");
            }
            var work=new Preparation {Handle=new BackupSubmission(operationId),Sources=sources,Leases=new IDisposable[sources.Length],Ids=sources.Select((_,i)=>Hash(operationId+":"+i)).ToArray(),Scope=scope};
            registering=true;preparationItems+=sources.Length;bool registered=false;
            try {
                // Retain every transient source before the first asynchronous boundary.
                for(int i=0;i<sources.Length;i++)work.Leases[i]=sources[i].Acquire();
                if((await Db(Command.Info,token)).Single.Flag("paused"))throw new InvalidOperationException("Resume the backup queue before registering photos.");
                if(await QuerySubmissionCore(operationId,token)!=null)throw new InvalidOperationException("This operationId is already registered; query its result.");
                var args=new List<object>{operationId,owner,Now,sources.Length};
                for(int i=0;i<sources.Length;i++)args.AddRange(new object[]{work.Ids[i],sources[i].Source+":"+sources[i].OriginId,sources[i].Version,sources[i].FileName,sources[i].MimeType});
                args.Add(scope??"");args.Add(epoch);
                await Db(Command.Prepare,token,args.ToArray());registered=true;
                preparations.Add(work);SignalPreparation();StartPreparationDriver();return work.Handle;
            }
            catch(BackupRepositoryException error) when(error.CommitOutcomeUnknown) {
                // The durable registration may exist, but no producer has started.
                // Stop new preparations until explicit close/reopen recovers it.
                preparationFailure=preparationFailure??error;SignalPreparation();throw;
            }
            finally {
                registering=false;
                if(!registered) {preparationItems-=sources.Length;foreach(var lease in work.Leases)if(lease!=null)try{lease.Dispose();}catch(Exception cleanup){Debug.LogException(cleanup);}}
            }
        }
        /// <summary>Stop unaccepted members only. Accepted transfers retain their own explicit controls.</summary>
        public async UniTask CancelPreparationAsync(string operationId,CancellationToken token=default)
        {using var operation=EnterOperation();ValidateId(operationId,nameof(operationId));await Db(Command.StopPreparation,token,operationId);}
        void StartPreparationDriver()
        {if(preparing)return;preparing=true;DrivePreparations().Forget(error=>Debug.LogException(error));}
        async UniTask DrivePreparations()
        {
            try {
                while(preparations.Count!=0) {
                    var work=preparations[0];
                    try {await PrepareSubmission(work);}
                    catch(Exception error) {work.Error(error);preparationFailure=preparationFailure??error;}
                    await FinishPreparation(work);

                }
            }
            finally {preparing=false;CapacityWait=null;}
        }
        async UniTask FinishPreparation(Preparation work)
        {
            if(preparations.Count!=0 && preparations[0]==work)CapacityWait=null;
            foreach(var lease in work.Leases)if(lease!=null)try{lease.Dispose();}catch(Exception cleanup){work.Error(cleanup);}
            preparations.Remove(work);preparationItems-=work.Sources.Length;PreparationRevision++;SignalPreparation();
            BackupSubmissionResult result=null;
            if(!work.RepositoryFailed)try {result=await QuerySubmissionCore(work.Handle.OperationId,default);}catch(Exception error){work.Error(error);preparationFailure=preparationFailure??error;}
            if(work.Errors.Count==0 && (result==null || result.Items.Any(x=>x.State!=BackupState.Completed && x.Acceptance<BackupAcceptanceStage.SystemScheduled)))
                work.Error(new InvalidOperationException("Executor did not accept every submitted photo."));
            if(work.Errors.Count!=0)work.Handle.Completion.TrySetException(new BackupSubmissionException(result,work.Errors,work.SharedErrors.ToArray()));
            else work.Handle.Completion.TrySetResult(result);
        }
        async UniTask SweepStoppedPreparations()
        {
            // Only the head may own an active producer. Queued requests can be
            // ended promptly without waiting for that producer or its capacity.
            foreach(var queued in preparations.Skip(1).ToArray()) {
                var failure=await PreparationConstraint(queued.Ids[0]);
                if(failure==null || failure is BackupSourceFailure)continue;
                queued.Error(failure);
                if(failure is BackupRepositoryException){queued.RepositoryFailed=true;preparationFailure=preparationFailure??failure;}
                else foreach(string id in queued.Ids)await Db(Command.FailItem,default,id,owner,failure.ToString(),Now);
                await FinishPreparation(queued);
            }
        }
        async UniTask WaitPreparationSignal(long observed,int milliseconds,Func<bool> complete=null)
        {
            long until=System.Diagnostics.Stopwatch.GetTimestamp()+(long)milliseconds*System.Diagnostics.Stopwatch.Frequency/1000;
            await UniTask.WaitUntil(()=>lifetime.IsCancellationRequested || preparationWake!=observed || complete!=null && complete() || System.Diagnostics.Stopwatch.GetTimestamp()>=until);
        }
        async UniTask<Exception> PreparationConstraint(string id)
        {
            if(lifetime.IsCancellationRequested)return new OperationCanceledException(lifetime.Token);
            if(preparationFailure!=null)return preparationFailure;
            if(executorFailure!=null)return executorFailure;
            var r=(await Db(Command.PreparationEligibility,default,id)).Single;
            if(!r.Flag("scope_valid"))return new GalleryException("ScopeConfirmationRequired","The preparation scope is no longer eligible.");
            if(r.Flag("paused"))return new InvalidOperationException("Preparation stopped because the backup queue was paused.");
            if(r.Flag("stop_requested"))return new OperationCanceledException("Preparation was explicitly stopped.");
            if(r.Number("desired_action")!=0)return new BackupSourceFailure(new GalleryException("SourceUnavailable","The source was removed from this preparation scope."));
            return null;
        }
        async UniTask<(long size,string mime,string hash)> PrepareObserved(ImageReference image,string id,long reservation)
        {
            using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var copying=PreparePayload(image,Path.Combine(root,"payloads",id+".payload"),reservation,stop.Token).AsTask();
            Exception constraint=null;
            try {
                while(!copying.IsCompleted) {
                    await WaitPreparationSignal(preparationWake,500,()=>copying.IsCompleted);
                    if(copying.IsCompleted)break;
                    await SweepStoppedPreparations();
                    constraint=await PreparationConstraint(id);
                    if(constraint!=null){stop.Cancel();break;}
                }
            } catch(Exception error) {constraint=error;stop.Cancel();}
            // Actual producer completion precedes any capacity or file release.
            (long size,string mime,string hash) result;
            try {result=await copying;}
            catch(OperationCanceledException) when(constraint!=null) {System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(constraint).Throw();throw;}
            if(constraint!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(constraint).Throw();
            return result;
        }
        internal async UniTask SuspendPreparationScope(string scope)
        {
            long cursor=0;
            for(;;) {var row=(await Db(Command.SuspendScope,default,scope,cursor,Now)).Single;cursor=row.Number("cursor");if(row.Number("count")<32)break;await UniTask.Yield();}
            await SynchronizeNativeAsync();
        }
        async UniTask PrepareSubmission(Preparation work)
        {
            int next=0;bool scheduleFailed=false;long lastSchedule=Now;
            async UniTask Schedule()
            {
                try {await ScheduleAccepted();lastSchedule=Now;}
                catch {scheduleFailed=true;throw;}
            }
            try {
                if(preparationFailure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(preparationFailure).Throw();
                await SweepStoppedPreparations();
                for(;next<work.Sources.Length;next++) {
                    var image=work.Sources[next];string id=work.Ids[next];
                    try {
                        var constraint=await PreparationConstraint(id);if(constraint!=null)throw constraint;
                        long permitted=Math.Min(maximum,budget),required=image.ByteCount.HasValue?Math.Max(1,image.ByteCount.Value):permitted;
                        if(required>permitted)throw new BackupSourceFailure(new IOException("Image exceeds the per-file or total staging limit."));
                        bool waiting=false;long lastNativeCheck=Now;
                        for(;;) {
                            long observed=preparationWake;
                            await SweepStoppedPreparations();
                            constraint=await PreparationConstraint(id);if(constraint!=null)throw constraint;
                            var capacity=(await Db(Command.TryPrepare,default,id,owner,required,permitted,budget,Now)).Single;
                            if(capacity.Flag("admitted"))break;
                            if((capacity.Number("reasons")&56)!=0){constraint=await PreparationConstraint(id);throw constraint??new InvalidOperationException("Preparation eligibility changed.");}
                            CapacityWait=new BackupCapacityWait {OperationId=work.Handle.OperationId,TaskId=id,Reasons=(BackupCapacityWaitReason)capacity.Number("reasons"),RequiredBytes=required,AvailableBytes=capacity.Number("available_bytes"),OccupiedBytes=capacity.Number("occupied_bytes"),StagedFiles=capacity.Number("staged_count"),ObservedUtc=DateTime.UtcNow};
                            if(!waiting){await Schedule();waiting=true;}
                            if(nativeEnabled && Now-lastNativeCheck>=TimeSpan.TicksPerSecond*5){await Platform(new NativeBackupRequest {op="sync",repository=StoreId});lastNativeCheck=Now;}
                            await WaitPreparationSignal(observed,1000);
                        }
                        CapacityWait=null;
                        if(image.Source!="file")await ValidateSource(image,lifetime.Token);
                        var payload=await PrepareObserved(image,id,required);
                        if(image.Source!="file")await ValidateSource(image,lifetime.Token);
                        await Db(Command.Seal,default,id,owner,payload.size,payload.hash,payload.mime,budget,Now);
                        string credential=GetAccessToken();if(string.IsNullOrWhiteSpace(credential))throw new InvalidOperationException("No access token available.");
                        if(!(await Db(Command.AcceptItem,default,id,owner,Now)).Single.Flag("accepted")) {
                            constraint=await PreparationConstraint(id);throw constraint??new InvalidOperationException("Preparation admission closed before acceptance.");
                        }
                        var claim=await Db(Command.Claim,default,id,Executor,nativeWifiOnly,Now);
                        if(claim.Rows.Count!=1)throw new InvalidOperationException("Transfer admission changed after preparation; query the accepted member.");
                        if(nativeEnabled)await Platform(new NativeBackupRequest {op="accept",repository=StoreId,id=id,generation=claim.Single.Number("current_generation"),token=credential});
                        else {
                            string reference=StoreDesktopCredential(credential);
                            try {await Db(Command.Handoff,default,id,claim.Single.Number("current_generation"),reference);}
                            catch(Exception error){if(!(error is BackupRepositoryException re && re.CommitOutcomeUnknown))desktopCredentials.Remove(reference);throw;}
                        }
                        if(Now-lastSchedule>=TimeSpan.TicksPerMillisecond*250)await Schedule();
                    }
                    catch(BackupSourceFailure failure) {
                        work.Error(failure.Original.SourceException,false);
                        await Db(Command.FailItem,default,id,owner,failure.Original.SourceException.ToString(),Now);await CleanupFilesAsync(default,32);
                    }
                    finally {
                        var lease=work.Leases[next];work.Leases[next]=null;
                        if(lease!=null)try{lease.Dispose();}catch(Exception cleanup){work.Error(cleanup);}
                    }
                }
            }
            catch(Exception error) {
                work.Error(error);work.RepositoryFailed=error is BackupRepositoryException;
                if(work.RepositoryFailed)preparationFailure=preparationFailure??error;
                if(error is GalleryException gallery && gallery.IsScopeAccessFailure && work.Scope!=null)
                    try {await SuspendPreparationScope(work.Scope);}
                    catch(Exception secondary) {
                        work.Error(secondary);
                        if(secondary is BackupRepositoryException){work.RepositoryFailed=true;preparationFailure=preparationFailure??secondary;}
                    }
                if(!work.SharedErrors.OfType<BackupRepositoryException>().Any(e=>e.CommitOutcomeUnknown))
                    for(int i=next;i<work.Ids.Length;i++) {
                        try {var row=(await Db(Command.Task,default,work.Ids[i])).Single;if(row.Number("state")==9)await Db(Command.FailItem,default,work.Ids[i],owner,error.ToString(),Now);}
                        catch(Exception cleanup){work.Error(cleanup);work.RepositoryFailed=true;preparationFailure=preparationFailure??cleanup;break;}
                    }
            }
            finally {
                if(!work.RepositoryFailed) {
                    if(!scheduleFailed)try{await Schedule();}catch(Exception error){work.Error(error);}
                    try{await CleanupFilesAsync(default,32);}catch(Exception error){work.Error(error);}
                }
            }
        }
        int Executor => nativeEnabled?(Application.platform==RuntimePlatform.Android?1:2):0;
        static void ValidateProtocolText(string value,int maxBytes,string name)
        {if(string.IsNullOrEmpty(value) || value.Any(c=>c<32) || new System.Text.UTF8Encoding(false,true).GetByteCount(value)>maxBytes)throw new ArgumentException("Protocol text must be nonempty, contain no control characters, and fit its UTF-8 byte limit.",name);}
        async UniTask ScheduleAccepted()
        {if(nativeEnabled)await Platform(new NativeBackupRequest {op="wake",repository=StoreId});else {StartDesktopDriver();await Db(Command.SystemScheduled,default,0);}}
    }
}
