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
        readonly bool nativeEnabled, nativeWifiOnly;
        readonly BackupTransferMode transferMode;
        readonly Func<string> accessToken;
        readonly Func<NativeBackupRequest, NativeBackupStatus> nativeCall;
        readonly bool nativeAvailable;
        readonly HttpClient client;
        readonly TimeSpan requestTimeout;
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        bool disposed, closing, running, registering, pausing, paused;
        int downloads, metadataReads, operations;
        public BackupTransferMode TransferMode => transferMode;
        public bool IsRunning => running;
        public bool IsPaused => paused;
        public bool SupportsNativeBackgroundTransfer => nativeAvailable;
        public bool UsesNativeBackgroundTransfer => nativeEnabled;
        public bool NativeWifiOnly => nativeWifiOnly;
        public string Account => account;
        internal string StoreId => repository.StoreId;
        internal string CursorIdentity { get; }
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
                DiskBudgetBytes=configuration.DiskBudgetBytes,MaxFileBytes=configuration.MaxFileBytes,TransferMode=configuration.TransferMode,
                RequestTimeout=configuration.RequestTimeout };
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
            ValidateProtocolText(c.Account,1024,nameof(c.Account));
            if(string.IsNullOrWhiteSpace(c.StorageDirectory) || !Path.IsPathRooted(c.StorageDirectory)) throw new ArgumentException("Absolute backup storage directory required.");
            if(c.DiskBudgetBytes<=0 || c.MaxFileBytes<=0 || c.MaxFileBytes>512L*1024*1024 || !Enum.IsDefined(typeof(BackupTransferMode),c.TransferMode) || c.RequestTimeout<=TimeSpan.Zero || c.RequestTimeout.TotalMilliseconds>int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(c));
            if(c.EnableNativeBackgroundTransfer && !available) throw new PlatformNotSupportedException("Native background transfer requires an Android or iOS player.");
        }
        ImageBackupService(BackupConfiguration c,Func<NativeBackupRequest,NativeBackupStatus> bridge,bool available,HttpMessageHandler handler,CancellationToken token)
        {
            nativeCall=bridge??throw new ArgumentNullException(nameof(bridge)); nativeAvailable=available;
            server=new Uri(c.ServerUrl).AbsoluteUri.TrimEnd('/'); account=c.Account; owner=Guid.NewGuid().ToString("N");
            budget=c.DiskBudgetBytes; maximum=c.MaxFileBytes; accessToken=c.AccessToken; requestTimeout=c.RequestTimeout;
            nativeEnabled=c.EnableNativeBackgroundTransfer; nativeWifiOnly=c.NativeWifiOnly; transferMode=c.TransferMode;
            token.ThrowIfCancellationRequested();
            repository=new BackupRepository(c.StorageDirectory,Hash(server+"\n"+account),server,account); root=repository.Directory;
            try
            {
                repository.Execute(Command.BindPreparer,owner);
                repository.Execute(Command.ProtocolConfigure,c.AllowDevelopmentHttp,(int)c.TransferMode);
                while(repository.Execute(Command.RecoverPreparations,owner,Now,32).Single.Number("recovered")!=0) token.ThrowIfCancellationRequested();
                for(;;)
                {
                    var controls=repository.Execute(Command.Controls,0,"").Rows;
                    foreach(var control in controls)
                    {
                        token.ThrowIfCancellationRequested();
                        if(control.Number("state")<4)repository.Execute(Command.ControlRecover,control.Text("id"),"Desktop process ended; explicitly reconcile the server result",Now,false);
                        repository.Execute(Command.ControlRelease,control.Text("id"));
                    }
                    if(controls.Count<32)break;
                }
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
                var info=repository.Execute(Command.Info).Single;
                paused=info.Flag("paused");
                // StoreId routes an account; independently created catalogs also
                // need their persisted namespace to distinguish paging positions.
                CursorIdentity=StoreId+":"+info.Text("source_namespace");
                client=new HttpClient(handler??new HttpClientHandler { AllowAutoRedirect=false }) { Timeout=System.Threading.Timeout.InfiniteTimeSpan };
            }
            catch { try { repository.Dispose(); } catch(Exception cleanup) { Debug.LogException(cleanup); } throw; }
        }
        internal async UniTask<BackupRepository.Result> Db(Command command,CancellationToken token=default,params object[] arguments)
        {
            Check(); metadataReads++;
            try {
                var result=await repository.ExecuteAsync(command,token,arguments);
                if(command==Command.CleanupRun || command==Command.SuspendScope || command==Command.StopPreparation || command==Command.Pause || command==Command.Action)SignalPreparation();
                return result;
            }
            finally { metadataReads--; }
        }
        static BackupTaskInfo TaskInfo(BackupRepository.Row r) => new BackupTaskInfo {
            id=r.Text("id"),batchId=r.Text("batch_id"),sequence=r.Number("sequence"),generation=r.Number("current_generation"),
            source=r.Text("source_id"),version=r.Text("content_version"),state=(BackupState)r.Number("state"),desiredAction=(int)r.Number("desired_action"),
            name=r.Text("name"),mime=r.Text("mime"),sha256=r.Text("sha256"),size=r.Number("byte_count"),
            confirmedBytes=r.Number("confirmed_bytes"),backupId=r.Text("backup_id"),error=r.Text("error"),
            nativeOwned=r.Number("executor")!=0 && (!r.Flag("payload_released") || !r.Flag("credential_released")),
            phase=(BackupProtocolPhase)r.Number("protocol_phase"),
            acceptance=Acceptance((BackupState)r.Number("state"),r.Number("submission_state")>0,r.Flag("system_scheduled"),r.Flag("preparation_started")),
            cleanupPending=r.Number("file_state")==2 || r.Number("file_state")==4 || r.Number("file_state")==5,cleanupError=r.Text("cleanup_error") };
        public async UniTask<BackupTaskPage> QueryTasksAsync(BackupTaskQuery query=null,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            Check(); query=query??new BackupTaskQuery(); int size=query.PageSize; var state=query.State; var cursor=query.Cursor;
            if(size<1 || size>200 || state.HasValue && !Enum.IsDefined(typeof(BackupState),state.Value)) throw new ArgumentOutOfRangeException(nameof(query));
            if(cursor!=null && (cursor.Store!=CursorIdentity || cursor.State!=state)) throw new ArgumentException("Cursor belongs to another repository or filter.");
            long upper=cursor?.Upper??(await Db(Command.Info,cancellationToken)).Single.Number("upper_sequence");
            var rows=(await Db(Command.Tasks,cancellationToken,cursor?.After??0,upper,state.HasValue?(int)state.Value:-1,size)).Rows;
            var items=rows.Select(TaskInfo).ToList().AsReadOnly();
            return new BackupTaskPage(items,rows.Count==size?new BackupTaskCursor(CursorIdentity,state,items[items.Count-1].sequence,upper):null);
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
            if(cursor!=null && cursor.Store!=CursorIdentity) throw new ArgumentException("Cursor belongs to another repository.");
            var rows=(await Db(Command.Receipts,cancellationToken,cursor?.AfterTime??0,cursor?.AfterId??"",pageSize)).Rows;
            var records=rows.Select(Receipt).ToList().AsReadOnly(); var last=records.Count==0?null:records[records.Count-1];
            return new BackupReceiptPage(records,records.Count==pageSize?new BackupReceiptCursor(CursorIdentity,last.ConfirmedUtc.Ticks,last.BackupId):null);
        }
        public async UniTask<BackupFileCleanupPage> QueryCleanupFailuresAsync(int pageSize=100,BackupFileCleanupCursor cursor=null,CancellationToken cancellationToken=default)
        {
            using var operation=EnterOperation();
            if(pageSize<1 || pageSize>200)throw new ArgumentOutOfRangeException(nameof(pageSize));
            if(cursor!=null && cursor.Store!=CursorIdentity)throw new ArgumentException("Cursor belongs to another repository.");
            var rows=(await Db(Command.CleanupFailures,cancellationToken,cursor?.After??"",pageSize)).Rows;
            var items=rows.Select(r=>new BackupFileCleanupInfo {FileId=r.Text("id"),TaskId=r.Text("task_id"),AccountedBytes=r.Number("byte_count"),Error=r.Text("cleanup_error"),UpdatedUtc=new DateTime(r.Number("updated_utc"),DateTimeKind.Utc)}).ToList().AsReadOnly();
            return new BackupFileCleanupPage {Items=items,Next=items.Count==pageSize?new BackupFileCleanupCursor {Store=CursorIdentity,After=items[items.Count-1].FileId}:null};
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
        internal static string Hash(string text) { using var sha=SHA256.Create();return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();
        void Check() { MediaThread.Check();if(disposed) throw new ObjectDisposedException(nameof(ImageBackupService)); }
        void CheckIdle() { Check();if(running || preparing || registering || pausing || drivingOperations || downloads!=0 || metadataReads!=0 || operations!=0) throw new InvalidOperationException("Cancel and await active service operations first."); }
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
            while(running || preparing || registering || pausing || drivingOperations || downloads!=0 || metadataReads!=0 || operations!=0)await UniTask.Yield();
            if(preparationFailure!=null && !ReferenceEquals(preparationFailure,executorFailure))cleanup.Run(()=>System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(preparationFailure).Throw());
            if(executorFailure!=null)cleanup.Run(()=>System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(executorFailure).Throw());
            if(operationDriveFailure!=null && !ReferenceEquals(operationDriveFailure,preparationFailure) && !ReferenceEquals(operationDriveFailure,executorFailure))
                cleanup.Run(()=>System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(operationDriveFailure).Throw());
            cleanup.Run(Dispose);cleanup.Throw();
        }
        public void Dispose()
        {
            if(disposed) return;CheckIdle();disposed=true;var cleanup=new UIFrame.CleanupFailure();
            desktopCredentials.Clear();cleanup.Run(desktopSignal.Dispose);cleanup.Run(client.Dispose);cleanup.Run(repository.Dispose);cleanup.Run(lifetime.Dispose);cleanup.Throw();
        }
        static async UniTask ValidateSource(ImageReference image,CancellationToken token)
        {
            try {await GameGallery.ValidateVersionAsync(image,token);}
            catch(Exception error) when(error is GalleryException gallery && !gallery.IsScopeAccessFailure || error is IOException || error is UnauthorizedAccessException){throw new BackupSourceFailure(error);}
        }
        async UniTask<(long size, string mime, string hash)> PreparePayload(ImageReference source, string destination, long available, CancellationToken token)
        {
            void CheckSize(long size)
            {
                if (size > Math.Min(maximum,budget)) throw new BackupSourceFailure(new IOException("Image exceeds the per-file or total staging limit."));
                if (size > available) throw new BackupSourceFailure(new GalleryException("SourceChanged","Image exceeded its declared size during preparation."));
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
            string folder = destination + ".source"; Directory.CreateDirectory(folder);
            MediaResponse response;
            try
            {
                response = await NativeMedia.Request(new MediaRequest { op = "export", source = source.Source, path = source.Id,
                    output = folder, maxBytes = Math.Min(maximum, available) }, token);
            }
            catch (GalleryException error) when (error.Code == "SizeLimitExceeded")
            {
                throw new BackupSourceFailure(error);
            }
            catch (GalleryException error) when (error.Code == "SourceUnavailable" || error.Code == "ReadFailed" || error.Code == "UnsupportedFormat")
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
            var receipt=Receipt(rows[0]);
            if (File.Exists(destination)) throw new IOException("Destination already exists.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            cancellationToken = linked.Token; downloads++;
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using var request = Request(HttpMethod.Get, "/v2/backups/" + Uri.EscapeDataString(receipt.BackupId) + "/content");
                await WithResponse(request, (response, token) => UniTask.RunOnThreadPool(async () =>
                {
                    CheckResponse(response);
                    if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != receipt.ByteCount)
                        throw new IOException("Downloaded backup size mismatch.");
                    using var input = await response.Content.ReadAsStreamAsync();
                    using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var sha = SHA256.Create();
                    var buffer = new byte[128 * 1024]; long received = 0;
                    for (;;)
                    {
                        token.ThrowIfCancellationRequested();
                        int count = await input.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, receipt.ByteCount - received) + (received == receipt.ByteCount ? 1 : 0), token);
                        if (count == 0) break;
                        if (count > receipt.ByteCount - received) throw new IOException("Downloaded backup exceeds expected size.");
                        await output.WriteAsync(buffer, 0, count, token);
                        sha.TransformBlock(buffer, 0, count, null, 0); received += count;
                    }
                    sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    if (received != receipt.ByteCount || Hex(sha.Hash) != receipt.Sha256) throw new IOException("Downloaded backup checksum mismatch.");
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
        async UniTask<T> WithResponse<T>(HttpRequestMessage request, Func<HttpResponseMessage, CancellationToken, UniTask<T>> consume, CancellationToken token)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(requestTimeout);
            try
            {
                token.ThrowIfCancellationRequested();
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
            const int limit = 512 * 1024;
            if (response.Content.Headers.ContentLength > limit) throw new IOException("Backup response exceeds 512 KiB.");
            using var input = await response.Content.ReadAsStreamAsync();
            using var output = new MemoryStream(); var buffer = new byte[4096];
            while (true)
            {
                int n = await input.ReadAsync(buffer, 0, Math.Min(buffer.Length, limit + 1 - (int)output.Length), token);
                if (n == 0) break;
                if (output.Length + n > limit) throw new IOException("Backup response exceeds 512 KiB.");
                output.Write(buffer, 0, n);
            }
            return new UTF8Encoding(false,true).GetString(output.GetBuffer(), 0, (int)output.Length);
        }
        static void CheckResponse(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode) return;
            TimeSpan? retry = response.Headers.RetryAfter?.Delta;
            if (!retry.HasValue && response.Headers.RetryAfter?.Date != null) retry = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            throw new BackupHttpException((int)response.StatusCode, "Backup HTTP request failed with status "+(int)response.StatusCode+".", retry);
        }
        string GetAccessToken() => accessToken();
    }
}
