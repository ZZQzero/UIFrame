using System;
using System.Buffers;
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
using Command = Game.Media.Backup.BackupRepository.Command;

namespace Game.Media.Backup
{
    /// <summary>Application-owned facade over the shared native backup repository.</summary>
    public sealed partial class ImageBackupService : IDisposable
    {
        readonly BackupRepository repository;
        readonly string root, server, account, owner;
        readonly long budget, maximum;
        readonly bool nativeEnabled, nativeWifiOnly, retryEnabled;
        readonly int retryLimit;
        readonly Func<string> accessToken;
        readonly Func<NativeBackupRequest, NativeBackupStatus> nativeCall;
        readonly bool nativeAvailable;
        readonly HttpClient client;
        readonly TimeSpan requestTimeout;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        CancellationTokenSource processing;
        ServerCapabilities capabilities;
        Exception callbackFailure, capabilityFailure;
        bool disposed, closing, running, accepting, pausing, paused, refreshingCapabilities;
        int downloads, metadataReads, operations;
        TransferAttempt activeTransfer;
        sealed class TransferAttempt : IDisposable
        {
            internal readonly string TaskId;
            internal readonly CancellationTokenSource Cancellation;
            internal bool OutcomeUnknown;
            internal TransferAttempt(string id,CancellationToken token)
            {TaskId=id;Cancellation=CancellationTokenSource.CreateLinkedTokenSource(token);}
            internal void PendingResponse(){OutcomeUnknown=false;}
            public void Dispose(){Cancellation.Dispose();}
        }
        public bool IsRunning => running;
        public bool IsPaused => paused;
        public bool SupportsNativeBackgroundTransfer => nativeAvailable;
        public bool UsesNativeBackgroundTransfer => nativeEnabled;
        public bool NativeWifiOnly => nativeWifiOnly;
        public string Account => account;
        internal string StoreId => repository.StoreId;
        internal string RepositoryRoot => repository.Root;
        internal static long Now => DateTime.UtcNow.Ticks;
        async UniTask<NativeBackupStatus> Platform(NativeBackupRequest request)
        {
            metadataReads++;
            try { return await UniTask.RunOnThreadPool(() => nativeCall(request)); }
            finally { metadataReads--; }
        }

        public static UniTask<ImageBackupService> CreateAsync(BackupConfiguration configuration,CancellationToken cancellationToken=default)
            => CreateAsync(configuration,NativeBackup.Call,NativeBackup.Available,null,cancellationToken);

        internal static async UniTask<ImageBackupService> CreateAsync(BackupConfiguration configuration,
            Func<NativeBackupRequest,NativeBackupStatus> nativeCall,bool available,HttpMessageHandler handler=null,CancellationToken token=default)
        {
            MediaThread.Check(); token.ThrowIfCancellationRequested();
            if(configuration==null) throw new ArgumentNullException(nameof(configuration));
            var copy=new BackupConfiguration { ServerUrl=configuration.ServerUrl,Account=configuration.Account,StorageDirectory=configuration.StorageDirectory,
                AccessToken=configuration.AccessToken,AllowDevelopmentHttp=configuration.AllowDevelopmentHttp,
                EnableNativeBackgroundTransfer=configuration.EnableNativeBackgroundTransfer,NativeWifiOnly=configuration.NativeWifiOnly,
                DiskBudgetBytes=configuration.DiskBudgetBytes,MaxFileBytes=configuration.MaxFileBytes,EnableTransientRetries=configuration.EnableTransientRetries,
                MaxRetries=configuration.MaxRetries,RequestTimeout=configuration.RequestTimeout };
            Validate(copy,available);
            if(copy.EnableNativeBackgroundTransfer)
            {
                string platformRoot=nativeCall(new NativeBackupRequest { op="root" }).root;
                if(Path.GetFullPath(copy.StorageDirectory)!=Path.GetFullPath(platformRoot))
                    throw new ArgumentException("Background backup must use NativeBackupStorageDirectory so the OS can locate it before Unity starts.");
            }
            var service=await UniTask.RunOnThreadPool(()=>new ImageBackupService(copy,nativeCall,available,handler,token));
            try
            {
                token.ThrowIfCancellationRequested();
                if(service.nativeEnabled) await service.Platform(new NativeBackupRequest { op="wake",repository=service.StoreId });
                if((await service.Db(Command.Operations,token,"",0,1)).Rows.Count!=0 || (await service.Db(Command.Operations,token,"",1,1)).Rows.Count!=0)service.StartOperationDriver();return service;
            }
            catch { try { service.Dispose(); } catch(Exception cleanup) { Debug.LogException(cleanup); } throw; }
        }
        public static string NativeBackupStorageDirectory
        {
            get { MediaThread.Check(); return NativeBackup.Call(new NativeBackupRequest { op="root" }).root; }
        }
        static void Validate(BackupConfiguration c,bool available)
        {
            if(!Uri.TryCreate(c.ServerUrl,UriKind.Absolute,out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("A server base URL without credentials, query or fragment is required.");
            if(uri.Scheme!="https" && !(c.AllowDevelopmentHttp && uri.Scheme=="http" && IsLocal(uri.Host))) throw new ArgumentException("HTTPS is required; development HTTP must use an explicit local address.");
            if(string.IsNullOrWhiteSpace(c.Account) || c.AccessToken==null) throw new ArgumentException("Account and token provider are required.");
            if(string.IsNullOrWhiteSpace(c.StorageDirectory) || !Path.IsPathRooted(c.StorageDirectory)) throw new ArgumentException("Absolute backup storage directory required.");
            if(c.DiskBudgetBytes<=0 || c.MaxFileBytes<=0 || c.MaxRetries<0 || c.MaxRetries>5 || c.RequestTimeout<=TimeSpan.Zero || c.RequestTimeout.TotalMilliseconds>int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(c));
            if(c.EnableNativeBackgroundTransfer && !available) throw new PlatformNotSupportedException("Native background transfer requires an Android or iOS player.");
            if(c.EnableNativeBackgroundTransfer && c.EnableTransientRetries) throw new ArgumentException("Managed retry policy cannot be combined with native background transfer.");
        }
        ImageBackupService(BackupConfiguration c,Func<NativeBackupRequest,NativeBackupStatus> bridge,bool available,HttpMessageHandler handler,CancellationToken token)
        {
            nativeCall=bridge??throw new ArgumentNullException(nameof(bridge)); nativeAvailable=available;
            server=new Uri(c.ServerUrl).AbsoluteUri.TrimEnd('/'); account=c.Account; owner=Guid.NewGuid().ToString("N");
            budget=c.DiskBudgetBytes; maximum=c.MaxFileBytes; accessToken=c.AccessToken; requestTimeout=c.RequestTimeout;
            nativeEnabled=c.EnableNativeBackgroundTransfer; nativeWifiOnly=c.NativeWifiOnly; retryEnabled=c.EnableTransientRetries; retryLimit=c.MaxRetries;
            token.ThrowIfCancellationRequested();
            repository=new BackupRepository(c.StorageDirectory,Hash(server+"\n"+account),server,account); root=repository.Directory;
            try
            {
                repository.Execute(Command.BindPreparer,owner);
                while(repository.Execute(Command.RecoverPreparations,owner,Now,32).Single.Number("recovered")!=0) token.ThrowIfCancellationRequested();
                for(int executor=0;executor<=2;executor++)
                {
                long cursor=0;
                do
                {
                    var attempts=repository.Execute(Command.Attempts,cursor,executor,100).Rows;
                    foreach(var attempt in attempts)
                    {
                        token.ThrowIfCancellationRequested(); cursor=attempt.Number("sequence");
                        if(executor!=0 && attempt.Number("submission_state")!=0)continue;
                        repository.Execute(Command.RecoverAttempt,attempt.Text("id"),attempt.Number("current_generation"),Now);
                    }
                    if(attempts.Count<100) break;
                } while(true);
                }
                paused=repository.Execute(Command.Info).Single.Flag("paused");
                client=new HttpClient(handler??new HttpClientHandler { AllowAutoRedirect=false }) { Timeout=System.Threading.Timeout.InfiniteTimeSpan };
            }
            catch { try { repository.Dispose(); } catch(Exception cleanup) { Debug.LogException(cleanup); } throw; }
        }
        internal async UniTask<BackupRepository.Result> Db(Command command,CancellationToken token=default,params object[] arguments)
        {
            Check(); metadataReads++;
            try { return await repository.ExecuteAsync(command,token,arguments); }
            finally { metadataReads--; }
        }
        static BackupTaskInfo TaskInfo(BackupRepository.Row r) => new BackupTaskInfo {
            id=r.Text("id"),batchId=r.Text("batch_id"),sequence=r.Number("sequence"),generation=r.Number("current_generation"),
            source=r.Text("source_id"),version=r.Text("content_version"),state=(BackupState)r.Number("state"),desiredAction=(int)r.Number("desired_action"),
            name=r.Text("name"),mime=r.Text("mime"),key=r.Text("idempotency_key"),sha256=r.Text("sha256"),size=r.Number("byte_count"),
            confirmedBytes=r.Number("confirmed_bytes"),nextAttemptUtcTicks=r.Number("next_attempt_utc"),retries=(int)r.Number("retries"),backupId=r.Text("backup_id"),error=r.Text("error"),relativePath=r.Text("relative_path"),
            nativeOwned=r.Number("executor")!=0 && (!r.Flag("payload_released") || !r.Flag("credential_released")),
            cleanupPending=r.Number("file_state")==2 || r.Number("file_state")==4 || r.Number("file_state")==5,cleanupError=r.Text("cleanup_error") };
        public async UniTask<BackupTaskPage> QueryTasksAsync(BackupTaskQuery query=null,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            Check(); query=query??new BackupTaskQuery(); int size=query.PageSize; var state=query.State; var cursor=query.Cursor;
            if(size<1 || size>200 || state.HasValue && !Enum.IsDefined(typeof(BackupState),state.Value)) throw new ArgumentOutOfRangeException(nameof(query));
            if(cursor!=null && (cursor.Store!=StoreId || cursor.State!=state)) throw new ArgumentException("Cursor belongs to another repository or filter.");
            long upper=cursor?.Upper??(await Db(Command.Info,cancellationToken)).Single.Number("upper_sequence");
            var rows=(await Db(Command.Tasks,cancellationToken,cursor?.After??0,upper,state.HasValue?(int)state.Value:-1,size)).Rows;
            var items=rows.Select(TaskInfo).ToList().AsReadOnly();
            return new BackupTaskPage(items,rows.Count==size?new BackupTaskCursor(StoreId,state,items[items.Count-1].sequence,upper):null);
        }
        public async UniTask<BackupTaskInfo> GetTaskAsync(string taskId,CancellationToken cancellationToken=default)
        {using var operation=EnterOperation();return await GetTaskCoreAsync(taskId,cancellationToken);}
        async UniTask<BackupTaskInfo> GetTaskCoreAsync(string taskId,CancellationToken cancellationToken=default)
        {
            if(string.IsNullOrEmpty(taskId)) throw new ArgumentException("Task ID required.");
            var rows=(await Db(Command.Task,cancellationToken,taskId)).Rows; return rows.Count==0?null:TaskInfo(rows[0]);
        }
        public async UniTask<BackupSummary> GetSummaryAsync(CancellationToken cancellationToken=default)
        {using var operation=EnterOperation();return await GetSummaryCoreAsync(cancellationToken);}
        async UniTask<BackupSummary> GetSummaryCoreAsync(CancellationToken cancellationToken=default)
        {
            var rows=(await Db(Command.Summary,cancellationToken)).Rows; var counts=new long[10];
            foreach(var row in rows) counts[(int)row.Number("state")]=row.Number("count");
            paused=rows[0].Flag("paused"); return new BackupSummary(counts,paused);
        }
        static BackupRecord Receipt(BackupRepository.Row r) => new BackupRecord { BackupId=r.Text("backup_id"),Source=r.Text("source_id"),Version=r.Text("content_version"),
            Sha256=r.Text("sha256"),ByteCount=r.Number("byte_count"),Name=r.Text("name"),MimeType=r.Text("mime"),ConfirmedUtc=new DateTime(r.Number("confirmed_utc"),DateTimeKind.Utc) };
        public async UniTask<BackupReceiptPage> QueryBackupsAsync(int pageSize=100,BackupReceiptCursor cursor=null,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            if(pageSize<1 || pageSize>200) throw new ArgumentOutOfRangeException(nameof(pageSize));
            if(cursor!=null && cursor.Store!=StoreId) throw new ArgumentException("Cursor belongs to another repository.");
            var rows=(await Db(Command.Receipts,cancellationToken,cursor?.AfterTime??0,cursor?.AfterId??"",pageSize)).Rows;
            var records=rows.Select(Receipt).ToList().AsReadOnly(); var last=records.Count==0?null:records[records.Count-1];
            return new BackupReceiptPage(records,records.Count==pageSize?new BackupReceiptCursor(StoreId,last.ConfirmedUtc.Ticks,last.BackupId):null);
        }
        public async UniTask<IReadOnlyList<string>> EnqueueAsync(IReadOnlyList<ImageReference> images,CancellationToken cancellationToken=default)
        {
            try { return await EnqueueBatchAsync(images,cancellationToken); }
            catch(BackupSourceFailure failure) { failure.Original.Throw(); throw; }
        }
        internal async UniTask<IReadOnlyList<string>> EnqueueBatchAsync(IReadOnlyList<ImageReference> images,CancellationToken token,string operationId=null)
        {
            using var operation=EnterOperation();
            Check(); if(images==null || images.Count<1 || images.Count>32) throw new ArgumentException("A durable preparation batch contains 1-32 images.");
            if(accepting) throw new InvalidOperationException("Another preparation is active.");
            var sources=images.ToArray(); if(sources.Any(x=>x==null || string.IsNullOrEmpty(x.Version))) throw new ArgumentException("Every image needs a current source identity and version.");
            token.ThrowIfCancellationRequested(); accepting=true;
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token); token=linked.Token;
            string batch=operationId??Guid.NewGuid().ToString("N"); bool prepared=false,accepted=false,unknown=false;
            var leases=new List<IDisposable>(sources.Length); var ids=new List<string>(sources.Length); var cleanup=new UIFrame.CleanupFailure();
            try
            {
                var args=new List<object>{batch,owner,Now,sources.Length};
                foreach(var image in sources)
                {
                    leases.Add(image.Acquire()); string id=Guid.NewGuid().ToString("N"); ids.Add(id);
                    args.AddRange(new object[]{id,image.Source+":"+image.OriginId,image.Version,image.FileName,image.MimeType,0L});
                }
                await Db(Command.Prepare,token,args.ToArray()); prepared=true;
                for(int i=0;i<sources.Length;i++)
                {
                    token.ThrowIfCancellationRequested(); var image=sources[i];
                    if(image.ByteCount>maximum) throw new BackupSourceFailure(new IOException("Image exceeds the per-file backup limit."));
                    long occupied=(await Db(Command.FileSummary,token)).Rows.Where(x=>x.Number("state")!=3).Sum(x=>x.Number("bytes"));
                    long free=(await Db(Command.Storage,token)).Single.Number("available_bytes")-16L*1024*1024;
                    long available=Math.Min(budget-occupied,free); if(available<=0 || image.ByteCount>available) throw new BackupBudgetExceededException();
                    await Db(Command.ReservePayload,token,ids[i],owner,Math.Min(maximum,available),budget,Now);
                    if(image.Source!="file")await ValidateSource(image,token);
                    var value=await PreparePayload(image,Path.Combine(root,"payloads",ids[i]+".payload"),available,token);
                    if(image.Source!="file")await ValidateSource(image,token);
                    await Db(Command.Seal,token,ids[i],owner,value.size,value.hash,Hash(image.Source+":"+image.OriginId+"\n"+value.hash),value.mime,budget,Now);
                }
                await Db(Command.Accept,token,batch,owner,Now); accepted=true;
            }
            catch(Exception error)
            {
                unknown=error is BackupRepositoryException repositoryError && repositoryError.CommitOutcomeUnknown;
                cleanup.Capture(error);
            }
            finally
            {
                if(prepared && !accepted && !unknown)
                {
                    try { await Db(Command.Abandon,default,batch,owner,Now,"Preparation did not complete");await CleanupFilesAsync(default,32); }
                    catch(Exception error) { cleanup.Capture(error); }
                }
                foreach(var lease in leases) if(lease!=null) cleanup.Run(lease.Dispose);
                accepting=false;
            }
            cleanup.Throw(); return ids.AsReadOnly();
        }
        public UniTask ProcessAsync(CancellationToken cancellationToken=default,Func<bool> canTransfer=null)
            => ProcessTasksAsync(null,cancellationToken,canTransfer);
        internal async UniTask ProcessTasksAsync(IReadOnlyList<string> ids,CancellationToken token,Func<bool> canTransfer=null)
        {
            using var operation=EnterOperation();
            Check(); if(running || pausing || refreshingCapabilities) throw new InvalidOperationException("A backup pass, queue control or capability refresh is already active.");
            if(nativeEnabled && canTransfer!=null) throw new ArgumentException("A managed callback cannot enforce background network policy.");
            if(ids!=null && ids.Count>32) throw new ArgumentException("An explicit processing batch contains at most 32 tasks.");
            token.ThrowIfCancellationRequested();
            running=true; callbackFailure=null; capabilityFailure=null;
            CancellationTokenSource passCancellation=null;
            try
            {
                passCancellation=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);
                processing=passCancellation;token=passCancellation.Token;
                if((await GetSummaryCoreAsync(token)).QueuePaused) return;
                if(!CanTransfer(canTransfer)) return;
                long upper=(await Db(Command.Info,token)).Single.Number("upper_sequence"),after=0,passTime=Now; int index=0;
                for(;;)
                {
                    IReadOnlyList<BackupTaskInfo> page;
                    if(ids==null) page=(await Db(Command.Ready,token,after,upper,passTime,100)).Rows.Select(TaskInfo).ToList();
                    else
                    {
                        if(index>=ids.Count) break;
                        var one=await GetTaskCoreAsync(ids[index++],token); if(one==null) throw new ArgumentException("Task does not exist."); page=new[]{one};
                    }
                    foreach(var candidate in page)
                    {
                        after=candidate.sequence; if(candidate.state!=BackupState.Queued && candidate.state!=BackupState.RetryScheduled || candidate.nextAttemptUtcTicks>passTime) continue;
                        if(!CanTransfer(canTransfer)) return;
                        await EnsureCapabilities(token);
                        var claim=await Db(Command.Claim,token,candidate.id,nativeEnabled?(Application.platform==RuntimePlatform.Android?1:2):0,nativeWifiOnly,Now,owner);
                        if(claim.Rows.Count==0)continue;
                        var record=TaskInfo(claim.Single);
                        using var transfer=new TransferAttempt(record.id,token);
                        activeTransfer=transfer;var transferToken=transfer.Cancellation.Token;
                        bool handedOff=false,determined=false; ExceptionDispatchInfo primary=null; Exception retryFailure=null;
                        try
                        {
                            if(record.size>capabilities.maxFileBytes) throw new IOException("Image exceeds server file limit.");
                            if(nativeEnabled)
                            {
                                var session=await CreateSession(record,transferToken,transfer);
                                if(session.completed) { await Confirm(record,session); determined=true; }
                                else
                                {
                                    string credential=GetAccessToken(); if(string.IsNullOrWhiteSpace(credential)) throw new InvalidOperationException("No access token available.");
                                    transferToken.ThrowIfCancellationRequested(); handedOff=true;
                                    try { await Platform(new NativeBackupRequest { op="submit",repository=StoreId,id=record.id,generation=record.generation,token=credential }); }
                                    catch {
                                        try { handedOff=(await Db(Command.Attempt,default,record.id,record.generation)).Single.Number("submission_state")>0; }
                                        catch(Exception lookup){Debug.LogException(lookup);}
                                        throw;
                                    }
                                }
                            }
                            else
                            {
                                var started=await Db(Command.Start,transferToken,record.id,record.generation);
                                if(started.Rows.Count==0) await Finish(record,4,"Execution stopped before network admission");
                                else
                                {
                                    var response=await Upload(record,transferToken,canTransfer,transfer);
                                    if(response==null) await Finish(record,transfer.OutcomeUnknown?3:4,"Transfer deferred before commit");
                                    else await Confirm(record,response);
                                }
                                determined=true;
                            }
                        }
                        catch(Exception error)
                        {
                            if(error is BackupRepositoryException || handedOff) { primary=ExceptionDispatchInfo.Capture(error); }
                            else
                            {
                            int outcome=transfer.OutcomeUnknown?3:transferToken.IsCancellationRequested?4:2;
                            try { await Finish(record,outcome,error.ToString()); determined=true; }
                            catch(Exception persistence) { Debug.LogException(persistence); primary=ExceptionDispatchInfo.Capture(error); }
                            if(token.IsCancellationRequested || ReferenceEquals(error,callbackFailure) || ReferenceEquals(error,capabilityFailure)) primary=ExceptionDispatchInfo.Capture(error);
                            if(retryEnabled && !transferToken.IsCancellationRequested && Transient(error)) retryFailure=error;
                            }
                        }
                        finally
                        {
                            try
                            {
                                if(!handedOff)
                                {
                                    try
                                    {
                                        if(determined) await Db(Command.Release,default,record.id,record.generation,true,!nativeEnabled);
                                        else await Db(Command.RecoverAttempt,default,record.id,record.generation,Now);
                                        if(nativeEnabled)await Platform(new NativeBackupRequest {op="sync",repository=StoreId});
                                    }
                                    catch(Exception cleanup) { if(primary==null) primary=ExceptionDispatchInfo.Capture(cleanup); else Debug.LogException(cleanup); }
                                }
                            }
                            finally {activeTransfer=null;}
                        }
                        primary?.Throw();
                        if(retryFailure!=null)
                        {
                            long delay=retryFailure is BackupHttpException http && http.RetryAfter.HasValue?Math.Max(0,http.RetryAfter.Value.Ticks):0;
                            await Db(Command.ScheduleRetry,default,record.id,retryLimit,Now,delay);
                        }
                    }
                    if(ids==null && page.Count<100) break;
                }
                await CleanupFilesAsync(token);
            }
            finally { processing=null;running=false;callbackFailure=null;capabilityFailure=null;passCancellation?.Dispose(); }
        }
        async UniTask<UploadResponse> CreateSession(BackupTaskInfo record,CancellationToken token,TransferAttempt transfer)
        {
            var response=await JsonRequest<UploadResponse>(HttpMethod.Post,"/v1/uploads",new UploadRequest {
                key=record.key,sha256=record.sha256,size=record.size,name=record.name,mime=record.mime,source=record.source },token,transfer);
            ValidateResponse(record,response);if(response.capabilities!=null)SetCapabilities(response.capabilities);
            if(!response.completed)transfer.PendingResponse();return response;
        }
        async UniTask<UploadResponse> Upload(BackupTaskInfo record,CancellationToken token,Func<bool> canTransfer,TransferAttempt transfer)
        {
            if(!CanTransfer(canTransfer)) return null;
            var session=await CreateSession(record,token,transfer); if(session.completed) return session;
            using var input=File.OpenRead(Payload(record)); if(input.Length!=record.size) throw new IOException("Staged backup size changed.");
            string actual=await UniTask.RunOnThreadPool(()=>HashFile(input,token)); if(actual!=record.sha256) throw new IOException("Staged backup checksum changed.");
            input.Position=session.offset; var buffer=new byte[capabilities.chunkBytes];
            while(input.Position<input.Length)
            {
                token.ThrowIfCancellationRequested(); if(!CanTransfer(canTransfer)) return null;
                int length=await input.ReadAsync(buffer,0,buffer.Length,token); long previous=session.offset;
                if(length==0) throw new EndOfStreamException("Staged backup was truncated.");
                using var body=new ByteArrayContent(buffer,0,length);
                session=await Send<UploadResponse>(HttpMethod.Put,"/v1/uploads/"+record.key+"?offset="+previous,body,token,transfer);
                ValidateResponse(record,session); if(session.offset!=previous+length) throw new InvalidOperationException("Unexpected server upload position.");
                if(!session.completed)transfer.PendingResponse();
                await Db(Command.Progress,token,record.id,record.generation,session.offset,false,Now);
            }
            if(!CanTransfer(canTransfer)) return null;
            await Db(Command.Progress,token,record.id,record.generation,record.size,true,Now);
            session=await JsonRequest<UploadResponse>(HttpMethod.Post,"/v1/uploads/"+record.key+"/commit",null,token,transfer); ValidateResponse(record,session); return session;
        }
        UniTask<BackupRepository.Result> Finish(BackupTaskInfo record,int outcome,string error)
            => Db(Command.Finish,default,record.id,record.generation,outcome,"",error,Now,record.size,record.sha256);
        async UniTask Confirm(BackupTaskInfo record,UploadResponse response)
        {
            ValidateResponse(record,response);
            if(!response.completed || string.IsNullOrEmpty(response.backupId) || response.offset!=record.size) throw new InvalidOperationException("Server did not confirm the verified backup.");
            await Db(Command.Finish,default,record.id,record.generation,1,response.backupId,"",Now,record.size,record.sha256);
        }
        static void ValidateResponse(BackupTaskInfo record,UploadResponse response)
        {
            if(response==null || response.uploadId!=record.key || response.sha256!=record.sha256 || response.size!=record.size || response.offset<0 || response.offset>record.size)
                throw new InvalidOperationException("Server upload identity, checksum or size mismatch.");
        }
        public async UniTask PauseAsync(CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);cancellationToken=linked.Token;
            Check(); if(pausing) throw new InvalidOperationException("Another queue control operation is active."); pausing=true;
            try
            {
                await Db(Command.Pause,cancellationToken,true); paused=true; processing?.Cancel();
                if(nativeEnabled) await Platform(new NativeBackupRequest { op="pause",repository=StoreId });
                while(running) await UniTask.Yield(PlayerLoopTiming.Update,cancellationToken);
                await WaitNativeRelease(null,cancellationToken);
            }
            finally { pausing=false; }
        }
        async UniTask WaitNativeRelease(string taskId,CancellationToken token)
        {
            if(!nativeEnabled)return;
            for(;;)
            {
                bool owned;
                if(taskId!=null)owned=(await GetTaskCoreAsync(taskId,token))?.nativeOwned==true;
                else owned=(await Db(Command.Attempts,token,0,Application.platform==RuntimePlatform.Android?1:2,1)).Rows.Count!=0;
                if(!owned)return;
                // Wake also surfaces repository/adapter failures; cancellation stops only this wait.
                await Platform(new NativeBackupRequest {op="sync",repository=StoreId});
                await UniTask.Delay(100,ignoreTimeScale:true,cancellationToken:token);
            }
        }
        internal async UniTask SynchronizeNativeAsync()
        {
            var transfer=activeTransfer;
            if(transfer!=null)
            {
                var current=await GetTaskCoreAsync(transfer.TaskId);
                if(ReferenceEquals(activeTransfer,transfer) && current!=null && current.desiredAction!=0)transfer.Cancellation.Cancel();
            }
            if(nativeEnabled)await Platform(new NativeBackupRequest {op="sync",repository=StoreId});
        }
        public async UniTask ResumeAsync(CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            Check();if(pausing)throw new InvalidOperationException("Another queue control operation is active.");pausing=true;
            try {
                await Db(Command.Pause,cancellationToken,false);paused=false;
                if(nativeEnabled)await Platform(new NativeBackupRequest {op="wake",repository=StoreId});
            }
            finally {pausing=false;}
        }
        public async UniTask RetryAsync(string taskId,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation(); Check(); await Db(Command.Action,cancellationToken,taskId,3,Now); }
        public async UniTask CancelAsync(string taskId,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);cancellationToken=linked.Token;
            Check(); await Db(Command.Action,cancellationToken,taskId,2,Now);
            if(nativeEnabled) await Platform(new NativeBackupRequest { op="stop",repository=StoreId,id=taskId });
            if(activeTransfer?.TaskId==taskId)activeTransfer.Cancellation.Cancel();
            while(activeTransfer?.TaskId==taskId)await UniTask.Yield(PlayerLoopTiming.Update,cancellationToken);
            await WaitNativeRelease(taskId,cancellationToken);
            await CleanupFilesAsync(cancellationToken);
        }
        public async UniTask<BackupFileCleanupPage> QueryCleanupFailuresAsync(int pageSize=100,BackupFileCleanupCursor cursor=null,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            if(pageSize<1 || pageSize>200)throw new ArgumentOutOfRangeException(nameof(pageSize));
            if(cursor!=null && cursor.Store!=StoreId)throw new ArgumentException("Cursor belongs to another repository.");
            var rows=(await Db(Command.CleanupFailures,cancellationToken,cursor?.After??"",pageSize)).Rows;
            var items=rows.Select(r=>new BackupFileCleanupInfo {FileId=r.Text("id"),TaskId=r.Text("task_id"),AccountedBytes=r.Number("byte_count"),Error=r.Text("cleanup_error"),UpdatedUtc=new DateTime(r.Number("updated_utc"),DateTimeKind.Utc)}).ToList().AsReadOnly();
            return new BackupFileCleanupPage {Items=items,Next=items.Count==pageSize?new BackupFileCleanupCursor {Store=StoreId,After=items[items.Count-1].FileId}:null};
        }
        public async UniTask RetryCleanupAsync(string fileId,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            Check();ValidateId(fileId,nameof(fileId));var row=(await Db(Command.CleanupRetry,cancellationToken,fileId,Now)).Single;
            await Db(Command.CleanupRun,cancellationToken,row.Text("id"),row.Number("updated_utc"),Now);
        }
        internal async UniTask<long> CleanupFilesAsync(CancellationToken token=default,int maximumItems=200)
        {
            string after=""; long freed=0; int count=0;
            while(count<maximumItems)
            {
                var rows=(await Db(Command.CleanupPage,token,after,Math.Min(100,maximumItems-count))).Rows;
                if(rows.Count==0) break;
                foreach(var row in rows)
                {
                    token.ThrowIfCancellationRequested(); after=row.Text("id");count++;
                    try {
                        var result=(await Db(Command.CleanupRun,token,after,row.Number("updated_utc"),Now)).Single;freed+=result.Number("freed_bytes");
                    }
                    catch(BackupRepositoryException error) when(error.IsIsolatedCleanupFailure) {Debug.LogException(error);}
                }
            }
            return freed;
        }
        void SetCapabilities(ServerCapabilities value)
        {
            try
            {
                if(value==null || value.protocolVersion!=1 || value.account!=account || value.chunkBytes<1 || value.chunkBytes>4*1024*1024 || value.maxFileBytes<0)
                    throw new InvalidOperationException("Server protocol or authenticated account does not match configuration.");
                if(nativeEnabled && !value.backgroundUpload) throw new InvalidOperationException("Server does not support background uploads."); capabilities=value;
            }
            catch(Exception error) { capabilities=null;capabilityFailure=error;throw; }
        }
        async UniTask EnsureCapabilities(CancellationToken token)
        { if(capabilities==null) SetCapabilities(await JsonRequest<ServerCapabilities>(HttpMethod.Get,"/v1/capabilities",null,token)); }
        public async UniTask RefreshServerCapabilitiesAsync(CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            Check();if(running || refreshingCapabilities)throw new InvalidOperationException("Await the active backup pass or capability refresh.");
            refreshingCapabilities=true;using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,lifetime.Token);
            try { SetCapabilities(await JsonRequest<ServerCapabilities>(HttpMethod.Get,"/v1/capabilities",null,linked.Token)); }
            finally { refreshingCapabilities=false; }
        }
        string Payload(BackupTaskInfo task) => Path.Combine(root,task.relativePath);
        internal static string Hash(string text) { using var sha=SHA256.Create();return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();
        void Check() { MediaThread.Check();if(disposed) throw new ObjectDisposedException(nameof(ImageBackupService)); }
        void CheckIdle() { Check();if(running || accepting || pausing || refreshingCapabilities || drivingOperations || downloads!=0 || metadataReads!=0 || operations!=0) throw new InvalidOperationException("Cancel and await active service operations first."); }
        internal OperationLease EnterOperation()
        {
            Check();if(closing)throw new ObjectDisposedException(nameof(ImageBackupService));operations++;return new OperationLease(this);
        }
        internal readonly struct OperationLease:IDisposable
        {
            readonly ImageBackupService owner;
            internal OperationLease(ImageBackupService owner){this.owner=owner;}
            public void Dispose(){owner.operations--;}
        }
        public async UniTask ShutdownAsync()
        {
            Check();if(closing)throw new InvalidOperationException("Backup shutdown is already active.");closing=true;
            var cleanup=new UIFrame.CleanupFailure();cleanup.Run(lifetime.Cancel);
            while(running || accepting || pausing || refreshingCapabilities || drivingOperations || downloads!=0 || metadataReads!=0 || operations!=0)await UniTask.Yield();
            cleanup.Run(Dispose);cleanup.Throw();
        }
        public void Dispose()
        {
            if(disposed) return;CheckIdle();disposed=true;var cleanup=new UIFrame.CleanupFailure();
            cleanup.Run(client.Dispose);cleanup.Run(repository.Dispose);cleanup.Run(lifetime.Dispose);cleanup.Throw();
        }
        static async UniTask ValidateSource(ImageReference image,CancellationToken token)
        {
            try {await GameGallery.ValidateVersionAsync(image,token);}
            catch(Exception error) when(error is GalleryException || error is IOException || error is UnauthorizedAccessException){throw new BackupSourceFailure(error);}
        }
        async UniTask<(long size, string mime, string hash)> PreparePayload(ImageReference source, string destination, long available, CancellationToken token)
        {
            void CheckSize(long size)
            {
                if (size > maximum) throw new BackupSourceFailure(new IOException("Image exceeds the per-file backup limit."));
                if (size > available) throw new BackupBudgetExceededException();
            }
            if (source.Source == "file")
            {
                return await UniTask.RunOnThreadPool(() =>
                {
                    FileStream input;
                    try { FileImageVersion.ValidateMetadata(source);input = File.OpenRead(source.Id); }
                    catch (GalleryException error) { throw new BackupSourceFailure(error); }
                    catch (IOException error) { throw new BackupSourceFailure(error); }
                    catch (UnauthorizedAccessException error) { throw new BackupSourceFailure(error); }
                    using (input)
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var sha = SHA256.Create())
                    {
                        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024); long size = 0;
                        try {
                        for (;;) {
                            token.ThrowIfCancellationRequested(); int count;
                            try { count = input.Read(buffer, 0, 128*1024); }
                            catch (IOException error) { throw new BackupSourceFailure(error); }
                            if (count == 0) break;
                            CheckSize(size + count); output.Write(buffer, 0, count);
                            sha.TransformBlock(buffer, 0, count, null, 0); size += count;
                        }
                        token.ThrowIfCancellationRequested();sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        string hash=Hex(sha.Hash);
                        try {FileImageVersion.ValidateCopy(source,hash);}
                        catch(Exception error) when(error is GalleryException || error is IOException || error is UnauthorizedAccessException){throw new BackupSourceFailure(error);}
                        // Dispose flushes the stream buffer; native Seal owns durable publication.
                        return (size, source.MimeType, hash);
                        }
                        finally {ArrayPool<byte>.Shared.Return(buffer);}
                    }
                });
            }
            if (available <= 0) throw new BackupBudgetExceededException();
            string folder = destination + ".source"; Directory.CreateDirectory(folder);
            MediaResponse response;
            try
            {
                response = await NativeMedia.Request(new MediaRequest { op = "export", source = source.Source, path = source.Id,
                    output = folder, maxBytes = Math.Min(maximum, available) }, token);
            }
            catch (GalleryException error) when (error.Code == "SizeLimitExceeded")
            {
                if (maximum <= available) throw new BackupSourceFailure(error);
                throw new BackupBudgetExceededException();
            }
            catch (GalleryException error) when (error.Code == "SourceUnavailable" || error.Code == "PermissionDenied" || error.Code == "ReadFailed" || error.Code == "UnsupportedFormat")
            { throw new BackupSourceFailure(error); }
            string path = response.items[0].path;
            long length = new FileInfo(path).Length; CheckSize(length);
            File.Move(path, destination); Directory.Delete(folder, true);
            string hash = await UniTask.RunOnThreadPool(() =>
            {
                using var input = File.OpenRead(destination); return HashFile(input, token);
            });
            return (length, ImagePaths.Mime(Path.GetExtension(path)), hash);
        }

        static string HashFile(Stream input, CancellationToken token)
        {
            using var sha = SHA256.Create(); var buffer = new byte[128 * 1024]; int length;
            while ((length = input.Read(buffer, 0, buffer.Length)) != 0)
            { token.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, length, null, 0); }
            token.ThrowIfCancellationRequested(); sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return Hex(sha.Hash);
        }

        public async UniTask DownloadBackupAndVerifyAsync(string backupId, string destination, CancellationToken cancellationToken = default)
        {
            using var operation=EnterOperation();
            Check(); if (!Path.IsPathRooted(destination)) throw new ArgumentException("Absolute destination required.");
            var rows=(await Db(Command.Receipt,cancellationToken,backupId)).Rows;
            if(rows.Count!=1) throw new ArgumentException("Confirmed backup receipt not found.",nameof(backupId));
            var receipt=Receipt(rows[0]); var record=new BackupTaskInfo { backupId=receipt.BackupId,size=receipt.ByteCount,sha256=receipt.Sha256 };
            if (File.Exists(destination)) throw new IOException("Destination already exists.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            cancellationToken = linked.Token; downloads++;
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using var request = Request(HttpMethod.Get, "/v1/backups/" + Uri.EscapeDataString(record.backupId) + "/content");
                await WithResponse(request, (response, token) => UniTask.RunOnThreadPool(async () =>
                {
                    await CheckResponse(response, token);
                    if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != record.size)
                        throw new IOException("Downloaded backup size mismatch.");
                    using var input = await response.Content.ReadAsStreamAsync();
                    using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var sha = SHA256.Create();
                    var buffer = new byte[128 * 1024]; long received = 0;
                    for (;;)
                    {
                        token.ThrowIfCancellationRequested();
                        int count = await input.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, record.size - received) + (received == record.size ? 1 : 0), token);
                        if (count == 0) break;
                        if (count > record.size - received) throw new IOException("Downloaded backup exceeds expected size.");
                        await output.WriteAsync(buffer, 0, count, token);
                        sha.TransformBlock(buffer, 0, count, null, 0); received += count;
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    if (received != record.size || Hex(sha.Hash) != record.sha256) throw new IOException("Downloaded backup checksum mismatch.");
                    token.ThrowIfCancellationRequested(); output.Flush(true); return true;
                }), cancellationToken);
                cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, destination);
            }
            catch { try { File.Delete(temporary); } catch (Exception cleanup) { Debug.LogException(cleanup); } throw; }
            finally { downloads--; }
        }

        static bool IsLocal(string host)
        {
            if (host == "localhost") return true;
            if (!IPAddress.TryParse(host, out var ip)) return false;
            if (IPAddress.IsLoopback(ip)) return true;
            var b = ip.GetAddressBytes(); return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31);
        }
        async UniTask<T> JsonRequest<T>(HttpMethod method, string path, object data, CancellationToken token,TransferAttempt transfer=null)
        {
            using var body = data == null ? null : new StringContent(JsonUtility.ToJson(data), Encoding.UTF8, "application/json");
            return await Send<T>(method, path, body, token,transfer);
        }
        async UniTask<T> Send<T>(HttpMethod method, string path, HttpContent content, CancellationToken token,TransferAttempt transfer=null)
        {
            using var request = Request(method, path); request.Content = content;
            return await WithResponse(request, async (response, requestToken) =>
            {
                string text = await ReadResponse(response, requestToken); await CheckResponse(response, requestToken, text);
                return JsonUtility.FromJson<T>(text);
            }, token,transfer);
        }
        async UniTask<T> WithResponse<T>(HttpRequestMessage request, Func<HttpResponseMessage, CancellationToken, UniTask<T>> consume, CancellationToken token,TransferAttempt transfer=null)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(requestTimeout);
            try
            {
                token.ThrowIfCancellationRequested();
                if(transfer!=null)transfer.OutcomeUnknown=true;
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                // Disposing also interrupts response streams that do not implement cancellation themselves.
                using var interruption = deadline.Token.Register(response.Dispose);
                T result = await consume(response, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested(); return result;
            }
            catch (Exception error) when (deadline.IsCancellationRequested &&
                (error is OperationCanceledException || error is IOException || error is ObjectDisposedException || error is HttpRequestException))
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("Backup HTTP request exceeded its deadline.", error);
            }
        }
        HttpRequestMessage Request(HttpMethod method, string path)
        {
            string token = GetAccessToken(); if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("No access token available.");
            var request = new HttpRequestMessage(method, server + path); request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token); return request;
        }
        internal static async UniTask<string> ReadResponse(HttpResponseMessage response, CancellationToken token)
        {
            const int limit = 64 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new IOException("Backup response exceeds 64 KiB.");
            using var input = await response.Content.ReadAsStreamAsync();
            using var output = new MemoryStream(); var buffer = new byte[4096];
            while (true)
            {
                int n = await input.ReadAsync(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)output.Length), token);
                if (n == 0) break;
                if (output.Length + n > limit) throw new IOException("Backup response exceeds 64 KiB.");
                output.Write(buffer, 0, n);
            }
            return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        static async UniTask CheckResponse(HttpResponseMessage response, CancellationToken token, string body = null)
        {
            if (response.IsSuccessStatusCode) return;
            TimeSpan? retry = response.Headers.RetryAfter?.Delta;
            if (!retry.HasValue && response.Headers.RetryAfter?.Date != null) retry = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            throw new BackupHttpException((int)response.StatusCode, body ?? await ReadResponse(response, token), retry);
        }
        static bool Transient(Exception error) => error is HttpRequestException || error is TimeoutException || error is BackupHttpException http &&
            (http.StatusCode == 408 || http.StatusCode == 429 || http.StatusCode == 500 || http.StatusCode == 502 || http.StatusCode == 503 || http.StatusCode == 504);
        bool CanTransfer(Func<bool> policy)
        {
            try { return policy == null || policy(); }
            catch (Exception error) { callbackFailure = error; throw; }
        }
        string GetAccessToken()
        {
            try { return accessToken(); }
            catch (Exception error) { callbackFailure = error; throw; }
        }
    }
}
