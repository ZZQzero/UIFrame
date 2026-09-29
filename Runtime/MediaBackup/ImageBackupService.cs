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
    public enum BackupState { Queued, Uploading, Verifying, Completed, Paused, RetryScheduled, NeedsAttention, Failed, Canceled }
    internal sealed class BackupPolicyWaitException : Exception { }
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
    }

    [Serializable]
    public sealed class BackupTaskInfo
    {
        public string id, batchId, key, source, version, name, mime, sha256, backupId, error;
        public long size, confirmedBytes, nextAttemptUtcTicks;
        public int retries;
        public BackupState state;
        public bool nativeOwned, cleanupPending;
        public string cleanupError;
        internal BackupTaskInfo Snapshot() => (BackupTaskInfo)MemberwiseClone();
    }

    [Serializable] internal sealed class BackupIdentity { public string server, account; }
    [Serializable] internal sealed class UploadRequest { public string key, sha256, name, mime, source; public long size; }
    [Serializable] internal sealed class UploadResponse { public string uploadId, sha256, backupId; public long offset, size; public bool completed; public ServerCapabilities capabilities; }
    [Serializable] internal sealed class ServerCapabilities { public int protocolVersion, chunkBytes; public long maxFileBytes; public string account; public bool backgroundUpload; }
    [Serializable] internal sealed class BackupReceipt
    {
        public string fingerprint, key, backupId, source, name, error;
        public bool retryRequested;
    }
    [Serializable] internal sealed class BackupScanState { public bool baselineEstablished; public List<BackupReceipt> entries = new List<BackupReceipt>(); }

    public sealed class BackupHttpException : Exception
    {
        public int StatusCode { get; }
        public TimeSpan? RetryAfter { get; }
        internal BackupHttpException(int status, string message, TimeSpan? retryAfter) : base(message) { StatusCode = status; RetryAfter = retryAfter; }
    }

    /// <summary>Persistent, account-bound backup queue with optional native background execution. Own one service per storage directory.</summary>
    public sealed class ImageBackupService : IDisposable
    {
        readonly string root, server, account;
        readonly long budget, maximum;
        readonly bool retryEnabled, nativeEnabled, nativeWifiOnly;
        readonly int retryLimit;
        readonly Func<string> accessToken;
        readonly Func<NativeBackupRequest, NativeBackupStatus> nativeCall;
        readonly bool nativeAvailable;
        readonly HttpClient client;
        readonly FileStream ownerLock;
        readonly Dictionary<string, BackupTaskInfo> tasks = new Dictionary<string, BackupTaskInfo>();
        long stagedBytes;
        long nextNativeRefresh;
        ServerCapabilities capabilities;
        readonly Dictionary<string, ScanIndex> scans = new Dictionary<string, ScanIndex>();
        readonly Dictionary<string, UniTaskCompletionSource<ScanIndex>> scanLoads = new Dictionary<string, UniTaskCompletionSource<ScanIndex>>();
        int metadataReads;
        sealed class ScanIndex
        {
            internal bool baseline;
            internal readonly Dictionary<string, BackupReceipt> entries = new Dictionary<string, BackupReceipt>();
            internal BackupScanState Snapshot() => new BackupScanState { baselineEstablished = baseline, entries = entries.Values.Select(CloneReceipt).ToList() };
        }
        static BackupReceipt CloneReceipt(BackupReceipt x) => new BackupReceipt { fingerprint=x.fingerprint, key=x.key, backupId=x.backupId, source=x.source, name=x.name, error=x.error, retryRequested=x.retryRequested };
        Exception callbackFailure, persistenceFailure, capabilityFailure;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        CancellationTokenSource processing;
        bool disposed, running, accepting, pausing;
        int downloads;
        static readonly int[] RetrySeconds = { 5, 30, 120, 600, 1800 };
        public bool IsRunning => running;
        public bool SupportsNativeBackgroundTransfer => nativeAvailable;
        public bool UsesNativeBackgroundTransfer => nativeEnabled;
        public bool NativeWifiOnly => nativeWifiOnly;
        public string Account => account;

        public ImageBackupService(BackupConfiguration configuration) : this(configuration, NativeBackup.Call, NativeBackup.Available) { }

        /// <summary>Loads the persistent store on a worker. The returned service is owned by the calling main thread.</summary>
        public static async UniTask<ImageBackupService> CreateAsync(BackupConfiguration configuration, CancellationToken cancellationToken = default)
        {
            MediaThread.Check(); cancellationToken.ThrowIfCancellationRequested();
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            var copy = new BackupConfiguration { ServerUrl=configuration.ServerUrl, Account=configuration.Account,
                StorageDirectory=configuration.StorageDirectory, AccessToken=configuration.AccessToken,
                AllowDevelopmentHttp=configuration.AllowDevelopmentHttp, EnableNativeBackgroundTransfer=configuration.EnableNativeBackgroundTransfer,
                NativeWifiOnly=configuration.NativeWifiOnly, DiskBudgetBytes=configuration.DiskBudgetBytes, MaxFileBytes=configuration.MaxFileBytes,
                EnableTransientRetries=configuration.EnableTransientRetries, MaxRetries=configuration.MaxRetries };
            bool available = NativeBackup.Available;
            // Await worker ownership even after cancellation, so a successfully opened store cannot leak its lock.
            var service = await UniTask.RunOnThreadPool(() => new ImageBackupService(copy, NativeBackup.Call, available, false, cancellationToken));
            try { cancellationToken.ThrowIfCancellationRequested(); return service; }
            catch { try { service.Dispose(); } catch (Exception cleanup) { Debug.LogException(cleanup); } throw; }
        }

        internal ImageBackupService(BackupConfiguration configuration, Func<NativeBackupRequest, NativeBackupStatus> nativeCall, bool nativeAvailable)
            : this(configuration, nativeCall, nativeAvailable, true, default) { }

        ImageBackupService(BackupConfiguration configuration, Func<NativeBackupRequest, NativeBackupStatus> nativeCall, bool nativeAvailable, bool checkThread, CancellationToken initializationToken)
        {
            this.nativeCall = nativeCall ?? throw new ArgumentNullException(nameof(nativeCall)); this.nativeAvailable = nativeAvailable;
            if (checkThread) MediaThread.Check();
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (!Uri.TryCreate(configuration.ServerUrl, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("A server base URL without credentials, query or fragment is required.");
            if (uri.Scheme != "https" && !(configuration.AllowDevelopmentHttp && uri.Scheme == "http" && IsLocal(uri.Host)))
                throw new ArgumentException("HTTPS is required; development HTTP is restricted to explicit local addresses.");
            if (string.IsNullOrWhiteSpace(configuration.Account) || configuration.AccessToken == null) throw new ArgumentException("Account and token provider are required.");
            if (string.IsNullOrWhiteSpace(configuration.StorageDirectory) || !Path.IsPathRooted(configuration.StorageDirectory)) throw new ArgumentException("Absolute backup storage directory required.");
            if (configuration.DiskBudgetBytes <= 0 || configuration.MaxFileBytes <= 0 || configuration.MaxRetries < 0 || configuration.MaxRetries > 5) throw new ArgumentOutOfRangeException(nameof(configuration));
            if (configuration.EnableNativeBackgroundTransfer && !nativeAvailable)
                throw new PlatformNotSupportedException("Native background backup requires an Android or iOS player.");
            if (configuration.EnableNativeBackgroundTransfer && configuration.EnableTransientRetries)
                throw new ArgumentException("Native background transfer uses explicit Retry after failures; managed transient retries cannot be combined with it.");
            nativeEnabled = configuration.EnableNativeBackgroundTransfer; nativeWifiOnly = configuration.NativeWifiOnly;
            root = Path.GetFullPath(configuration.StorageDirectory); server = uri.AbsoluteUri.TrimEnd('/'); account = configuration.Account;
            budget = configuration.DiskBudgetBytes; maximum = configuration.MaxFileBytes; accessToken = configuration.AccessToken;
            retryEnabled = configuration.EnableTransientRetries; retryLimit = configuration.MaxRetries;
            initializationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(root);
            ownerLock = new FileStream(Path.Combine(root, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                string identityPath = Path.Combine(root, "identity.json");
                if (File.Exists(identityPath))
                {
                    var identity = JsonUtility.FromJson<BackupIdentity>(File.ReadAllText(identityPath));
                    if (identity.server != server || identity.account != account) throw new InvalidOperationException("This backup store belongs to another server or account.");
                }
                else AtomicJson(identityPath, new BackupIdentity { server = server, account = account });
                Directory.CreateDirectory(Path.Combine(root, "batches"));
                string staging = Path.Combine(root, "staging");
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                foreach (var record in ReadTasks()) { initializationToken.ThrowIfCancellationRequested(); tasks.Add(record.id, record); }
                stagedBytes = Directory.EnumerateFiles(Path.Combine(root, "batches"), "*.payload", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
                foreach (var record in LoadTasks())
                {
                    initializationToken.ThrowIfCancellationRequested();
                    if (!record.nativeOwned && (record.state == BackupState.Uploading || record.state == BackupState.Verifying))
                    { record.state = BackupState.Queued; Save(record); }
                    if (!record.nativeOwned && !record.cleanupPending && (record.state == BackupState.Completed || record.state == BackupState.Canceled) && File.Exists(Payload(record)))
                    { record.cleanupPending = true; Save(record); }
                }
                client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
            }
            catch { try { ownerLock.Dispose(); } catch (Exception cleanup) { Debug.LogException(cleanup); } throw; }
        }

        public IReadOnlyList<BackupTaskInfo> GetTasks()
        {
            Check(); ReconcileNative(); return LoadTasks().AsReadOnly();
        }

        public async UniTask<IReadOnlyList<string>> EnqueueAsync(IReadOnlyList<ImageReference> images, CancellationToken cancellationToken = default)
        {
            try { return await EnqueueBatchAsync(images, cancellationToken); }
            catch (BackupSourceFailure failure) { failure.Original.Throw(); throw; }
        }

        internal async UniTask<IReadOnlyList<string>> EnqueueBatchAsync(IReadOnlyList<ImageReference> images, CancellationToken cancellationToken)
        {
            Check(); if (images == null) throw new ArgumentNullException(nameof(images));
            if (images.Count == 0) throw new ArgumentException("At least one image is required.");
            if (accepting || pausing) throw new InvalidOperationException("Another batch is being accepted or the queue is pausing.");
            var sources = images.ToArray();
            if (sources.Any(x => x == null)) throw new ArgumentException("Null image reference.");
            cancellationToken.ThrowIfCancellationRequested(); accepting = true;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            string batch = Guid.NewGuid().ToString("N"), folder = Path.Combine(root, "staging", batch);
            try
            {
                Directory.CreateDirectory(folder); var records = new List<BackupTaskInfo>();
                long bytes = 0;
                foreach (var source in sources)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (source.ByteCount > maximum) throw new BackupSourceFailure(new IOException("Image exceeds the per-file backup limit."));
                    if (source.ByteCount > budget - stagedBytes - bytes && tasks.Values.Any(x => x.nativeOwned)) ReconcileNative();
                    if (source.ByteCount > budget - stagedBytes - bytes) throw new BackupBudgetExceededException();
                    string id = Guid.NewGuid().ToString("N"), destination = Path.Combine(folder, id + ".payload");
                    var prepared = await PreparePayload(source, destination, budget - stagedBytes - bytes, linked.Token);
                    string sourceIdentity = source.Source + ":" + source.OriginId;
                    var record = new BackupTaskInfo { id = id, batchId = batch, source = sourceIdentity, version = source.Version,
                        name = source.FileName, mime = prepared.mime, sha256 = prepared.hash, size = prepared.size,
                        key = Hash(sourceIdentity + "\n" + prepared.hash), state = BackupState.Queued };
                    AtomicJson(Path.Combine(folder, id + ".json"), record); records.Add(record); bytes += prepared.size;
                }
                linked.Token.ThrowIfCancellationRequested();
                Directory.Move(folder, Path.Combine(root, "batches", batch));
                foreach (var record in records) tasks.Add(record.id, record.Snapshot());
                stagedBytes += bytes;
                return records.Select(x => x.id).ToList().AsReadOnly();
            }
            catch { ImagePaths.CleanAfterFailure(folder); throw; }
            finally { accepting = false; }
        }

        async UniTask<(long size, string mime, string hash)> PreparePayload(ImageReference source, string destination, long available, CancellationToken token)
        {
            using var lease = source.Acquire();
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
                    try { input = File.OpenRead(source.Id); }
                    catch (IOException error) { throw new BackupSourceFailure(error); }
                    catch (UnauthorizedAccessException error) { throw new BackupSourceFailure(error); }
                    using (input)
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var sha = SHA256.Create())
                    {
                        var buffer = new byte[128 * 1024]; long size = 0;
                        for (;;)
                        {
                            token.ThrowIfCancellationRequested(); int count;
                            try { count = input.Read(buffer, 0, buffer.Length); }
                            catch (IOException error) { throw new BackupSourceFailure(error); }
                            if (count == 0) break;
                            CheckSize(size + count); output.Write(buffer, 0, count);
                            sha.TransformBlock(buffer, 0, count, null, 0); size += count;
                        }
                        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0); output.Flush(true);
                        return (size, source.MimeType, Hex(sha.Hash));
                    }
                }, cancellationToken: token);
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
            }, cancellationToken: token);
            return (length, ImagePaths.Mime(Path.GetExtension(path)), hash);
        }

        static string HashFile(Stream input, CancellationToken token)
        {
            using var sha = SHA256.Create(); var buffer = new byte[128 * 1024]; int length;
            while ((length = input.Read(buffer, 0, buffer.Length)) != 0)
            { token.ThrowIfCancellationRequested(); sha.TransformBlock(buffer, 0, length, null, 0); }
            token.ThrowIfCancellationRequested(); sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return Hex(sha.Hash);
        }

        /// <summary>Processes eligible jobs once. In native mode returns after durable OS submission, not upload completion. Cancellation stops submission only.</summary>
        public UniTask ProcessAsync(CancellationToken cancellationToken = default, Func<bool> canTransfer = null)
            => ProcessTasksAsync(null, cancellationToken, canTransfer);

        internal async UniTask ProcessTasksAsync(IReadOnlyList<string> taskIds, CancellationToken cancellationToken, Func<bool> canTransfer = null)
        {
            Check(); if (running || pausing) throw new InvalidOperationException("A backup pass is already running or the queue is pausing.");
            if (nativeEnabled && canTransfer != null) throw new ArgumentException("A managed callback cannot enforce a background network policy. Configure NativeWifiOnly instead.");
            cancellationToken.ThrowIfCancellationRequested(); running = true; callbackFailure = null; persistenceFailure = null; capabilityFailure = null;
            processing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            var token = processing.Token;
            try
            {
                ReconcileNative(nativeEnabled && DateTime.UtcNow.Ticks >= nextNativeRefresh ? null : taskIds);
                if (!CanTransfer(canTransfer)) return;
                var candidates = taskIds == null ? LoadTasks() : taskIds.Select(Find).ToList();
                // Waking an existing OS job or an empty/completed queue needs no server handshake.
                if (candidates.Any(x => (x.state == BackupState.Queued || x.state == BackupState.RetryScheduled) &&
                    x.nextAttemptUtcTicks <= DateTime.UtcNow.Ticks && (!x.nativeOwned || !NativeStatus(x).exists)))
                    await EnsureCapabilities(token);
                foreach (var record in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (record.nativeOwned && NativeStatus(record).exists)
                    {
                        if (record.state == BackupState.Queued || record.state == BackupState.Uploading)
                            nativeCall(new NativeBackupRequest { op = "wake", id = record.id });
                        continue;
                    }
                    if (record.state != BackupState.Queued && record.state != BackupState.RetryScheduled) continue;
                    if (record.nextAttemptUtcTicks > DateTime.UtcNow.Ticks) continue;
                    try
                    {
                        if (record.size > capabilities.maxFileBytes) throw new IOException("Image exceeds server file limit.");
                        if (nativeEnabled) await SubmitNative(record, token);
                        else if (record.nativeOwned) throw new InvalidOperationException("Resume this store with native background transfer enabled.");
                        else await Upload(record, token, canTransfer);
                    }
                    catch (BackupPolicyWaitException) { record.state = BackupState.Queued; Save(record); break; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        if (!record.nativeOwned) { record.state = BackupState.Queued; Save(record); } throw;
                    }
                    catch (Exception error)
                    {
                        if (ReferenceEquals(error, persistenceFailure)) throw;
                        if (ReferenceEquals(error, callbackFailure) || ReferenceEquals(error, capabilityFailure))
                        {
                            record.error = error.Message; record.state = BackupState.Failed;
                            try { Save(record); } catch (Exception persistence) { Debug.LogException(persistence); }
                            throw;
                        }
                        // A native handoff may have succeeded before a bridge error. Never steal its payload.
                        if (record.nativeOwned) throw;
                        // A committed backup is not changed to Failed because local cleanup failed.
                        if (record.state == BackupState.Completed) throw;
                        record.error = error.Message;
                        if (retryEnabled && record.retries < retryLimit && Transient(error))
                        {
                            int delay = RetrySeconds[record.retries++];
                            double seconds = delay + delay * (Guid.NewGuid().ToByteArray()[0] / 255.0) * 0.2;
                            if (error is BackupHttpException http && http.RetryAfter.HasValue) seconds = Math.Max(seconds, http.RetryAfter.Value.TotalSeconds);
                            record.nextAttemptUtcTicks = DateTime.UtcNow.AddSeconds(seconds).Ticks; record.state = BackupState.RetryScheduled;
                        }
                        else record.state = error is BackupHttpException auth && (auth.StatusCode == 401 || auth.StatusCode == 403 || auth.StatusCode == 413 || auth.StatusCode == 507)
                            ? BackupState.NeedsAttention : BackupState.Failed;
                        Save(record);
                    }
                }
            }
            finally { processing.Dispose(); processing = null; running = false; callbackFailure = null; capabilityFailure = null; }
        }

        async UniTask SubmitNative(BackupTaskInfo record, CancellationToken token)
        {
            var session = await JsonRequest<UploadResponse>(HttpMethod.Post, "/v1/uploads", new UploadRequest
            { key = record.key, sha256 = record.sha256, size = record.size, name = record.name, mime = record.mime, source = record.source }, token);
            ValidateResponse(record, session);
            if (session.capabilities != null) SetCapabilities(session.capabilities);
            if (session.completed)
            {
                if (string.IsNullOrEmpty(session.backupId)) throw new InvalidOperationException("Missing committed backup ID.");
                record.state = BackupState.Completed; record.backupId = session.backupId; record.confirmedBytes = record.size;
                record.nativeOwned = false; record.cleanupPending = true; Save(record); FinishCleanup(record); return;
            }
            string credential = GetAccessToken();
            if (string.IsNullOrWhiteSpace(credential)) throw new InvalidOperationException("No access token available.");
            token.ThrowIfCancellationRequested();
            record.nativeOwned = true; record.state = BackupState.Queued; record.error = null; Save(record);
            nativeCall(new NativeBackupRequest { op = "submit", id = record.id, payload = Payload(record),
                url = server + "/v1/uploads/" + record.key + "/background", account = Hash(account), token = credential,
                key = record.key, sha256 = record.sha256, size = record.size, wifiOnly = nativeWifiOnly });
        }

        NativeBackupStatus NativeStatus(BackupTaskInfo record) => nativeCall(new NativeBackupRequest { op = "status", id = record.id });
        void ReconcileNative(IReadOnlyList<string> taskIds = null)
        {
            var records = (taskIds == null ? tasks.Values.AsEnumerable() : taskIds.Select(Find))
                .Where(x => x.nativeOwned || x.cleanupPending).Select(x => x.Snapshot()).ToList();
            foreach (var record in records)
            {
                if (record.cleanupPending)
                {
                    if (string.IsNullOrEmpty(record.cleanupError)) FinishCleanup(record);
                    continue;
                }
                var status = NativeStatus(record);
                if (!status.exists) continue; // Durable handoff intent; next ProcessAsync submits it.
                bool changed = record.state != status.state || (record.error ?? "") != (status.error ?? "") ||
                    (record.backupId ?? "") != (status.backupId ?? "") || record.confirmedBytes != status.confirmedBytes;
                record.state = status.state; record.error = status.error; record.backupId = status.backupId;
                record.confirmedBytes = status.confirmedBytes;
                if (status.released && (status.state == BackupState.Completed || status.state == BackupState.Canceled))
                {
                    record.cleanupPending = true; Save(record); FinishCleanup(record);
                }
                else if (changed) Save(record);
            }
            if (taskIds == null) nextNativeRefresh = DateTime.UtcNow.AddSeconds(1).Ticks;
        }
        void FinishCleanup(BackupTaskInfo record)
        {
            try
            {
                if (record.nativeOwned)
                {
                    nativeCall(new NativeBackupRequest { op = "forget", id = record.id });
                    record.nativeOwned = false; Save(record);
                }
                DeletePayload(record);
                record.cleanupPending = false; record.cleanupError = null; Save(record);
            }
            catch (Exception error)
            {
                record.cleanupError = error.ToString();
                try { Save(record); } catch (Exception persistence) { Debug.LogException(persistence); }
                throw;
            }
        }
        /// <summary>Explicitly retries failed local/native cleanup without retrying an upload.</summary>
        public void RetryCleanup(string taskId)
        {
            CheckIdle(); var record = Find(taskId);
            if (!record.cleanupPending) throw new InvalidOperationException("Task has no pending cleanup.");
            record.cleanupError = null; Save(record); FinishCleanup(record);
        }
        void ReleaseNativeForRetry(BackupTaskInfo record)
        {
            if (!record.nativeOwned) return;
            var status = NativeStatus(record);
            if (status.exists && !status.released) throw new InvalidOperationException("Native cancellation is still settling. Await PauseAsync or wait for the terminal status.");
            nativeCall(new NativeBackupRequest { op = "forget", id = record.id });
            record.nativeOwned = false;
        }

        async UniTask Upload(BackupTaskInfo record, CancellationToken token, Func<bool> canTransfer)
        {
            void CheckPolicy() { if (!CanTransfer(canTransfer)) throw new BackupPolicyWaitException(); }
            CheckPolicy();
            record.state = BackupState.Uploading; record.error = null; Save(record);
            var session = await JsonRequest<UploadResponse>(HttpMethod.Post, "/v1/uploads", new UploadRequest
            { key = record.key, sha256 = record.sha256, size = record.size, name = record.name, mime = record.mime, source = record.source }, token);
            ValidateResponse(record, session);
            if (session.capabilities != null) SetCapabilities(session.capabilities);
            if (!session.completed)
            {
                using var input = File.OpenRead(Payload(record));
                if (input.Length != record.size) throw new IOException("Staged backup file size changed.");
                string actual = await UniTask.RunOnThreadPool(() => HashFile(input, token), cancellationToken: token);
                if (actual != record.sha256) throw new IOException("Staged backup checksum changed.");
                input.Position = session.offset; var buffer = new byte[capabilities.chunkBytes];
                while (input.Position < input.Length)
                {
                    CheckPolicy();
                    token.ThrowIfCancellationRequested(); int length = input.Read(buffer, 0, buffer.Length); long previous = session.offset;
                    using var body = new ByteArrayContent(buffer, 0, length);
                    session = await Send<UploadResponse>(HttpMethod.Put, "/v1/uploads/" + record.key + "?offset=" + previous, body, token);
                    ValidateResponse(record, session);
                    if (session.offset != previous + length) throw new InvalidOperationException("Server acknowledged an unexpected upload position.");
                    record.confirmedBytes = session.offset; Save(record);
                }
                record.state = BackupState.Verifying; Save(record);
                CheckPolicy();
                session = await JsonRequest<UploadResponse>(HttpMethod.Post, "/v1/uploads/" + record.key + "/commit", null, token);
                ValidateResponse(record, session);
            }
            if (!session.completed || string.IsNullOrEmpty(session.backupId)) throw new InvalidOperationException("Server did not confirm a committed backup.");
            record.backupId = session.backupId; record.confirmedBytes = record.size; record.state = BackupState.Completed;
            record.cleanupPending = true; Save(record); FinishCleanup(record);
        }

        static void ValidateResponse(BackupTaskInfo record, UploadResponse response)
        {
            if (response.uploadId != record.key || response.sha256 != record.sha256 || response.size != record.size || response.offset < 0 || response.offset > record.size)
                throw new InvalidOperationException("Server upload identity, checksum or size mismatch.");
        }

        public async UniTask PauseAsync(CancellationToken cancellationToken = default)
        {
            Check();
            if (pausing || accepting) throw new InvalidOperationException("Await the active pause or cancel and await file preparation before pausing.");
            cancellationToken.ThrowIfCancellationRequested(); pausing = true;
            try
            {
                processing?.Cancel();
                while (running) await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
                foreach (var record in LoadTasks())
                {
                    if (record.nativeOwned)
                    {
                        var status = nativeCall(new NativeBackupRequest { op = "pause", id = record.id });
                        if (!status.exists) { record.nativeOwned = false; record.state = BackupState.Paused; Save(record); }
                    }
                    else if (record.state == BackupState.Queued || record.state == BackupState.RetryScheduled)
                    { record.state = BackupState.Paused; Save(record); }
                }
                var pending = LoadTasks().Where(x => x.nativeOwned).ToList();
                while (pending.Count != 0)
                {
                    pending.RemoveAll(x => { var status = NativeStatus(x); return !status.exists || status.released; });
                    if (pending.Count != 0) await UniTask.Delay(100, ignoreTimeScale: true, cancellationToken: cancellationToken);
                }
                ReconcileNative();
            }
            finally { pausing = false; }
        }
        public void Resume()
        {
            CheckIdle(); ReconcileNative(); foreach (var record in LoadTasks()) if (record.state == BackupState.Paused)
            { ReleaseNativeForRetry(record); record.state = BackupState.Queued; Save(record); }
        }
        public void Retry(string taskId)
        {
            CheckIdle(); ReconcileNative(); var record = Find(taskId);
            if (record.state != BackupState.Failed && record.state != BackupState.NeedsAttention && record.state != BackupState.RetryScheduled)
                throw new InvalidOperationException("Task is not in a retryable state.");
            ReleaseNativeForRetry(record); record.retries = 0; record.nextAttemptUtcTicks = 0; record.error = null; record.state = BackupState.Queued; Save(record);
        }
        public void Cancel(string taskId)
        {
            CheckIdle(); ReconcileNative(); var record = Find(taskId);
            if (record.state == BackupState.Completed) throw new InvalidOperationException("A committed backup cannot be canceled.");
            if (record.nativeOwned)
            {
                var status = nativeCall(new NativeBackupRequest { op = "cancel", id = record.id });
                if (status.exists) { ReconcileNative(); return; }
                record.nativeOwned = false;
            }
            record.state = BackupState.Canceled; record.cleanupPending = true; Save(record); FinishCleanup(record);
        }

        public async UniTask DownloadAndVerifyAsync(string taskId, string destination, CancellationToken cancellationToken = default)
        {
            Check(); if (!Path.IsPathRooted(destination)) throw new ArgumentException("Absolute destination required.");
            ReconcileNative(); var record = Find(taskId); if (record.state != BackupState.Completed) throw new InvalidOperationException("Backup not complete.");
            if (File.Exists(destination)) throw new IOException("Destination already exists.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            cancellationToken = linked.Token; downloads++;
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using var request = Request(HttpMethod.Get, "/v1/backups/" + Uri.EscapeDataString(record.backupId) + "/content");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                await CheckResponse(response, cancellationToken);
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { await input.CopyToAsync(output, 128 * 1024, cancellationToken); output.Flush(true); }
                await UniTask.RunOnThreadPool(() =>
                {
                    using var file = File.OpenRead(temporary);
                    if (file.Length != record.size || HashFile(file, cancellationToken) != record.sha256) throw new IOException("Downloaded backup checksum mismatch.");
                }, cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, destination);
            }
            catch { try { File.Delete(temporary); } catch (Exception cleanup) { Debug.LogException(cleanup); } throw; }
            finally { downloads--; }
        }

        void SetCapabilities(ServerCapabilities value)
        {
            try
            {
                if (value == null || value.protocolVersion != 1 || value.account != account || value.chunkBytes < 1 || value.chunkBytes > 4 * 1024 * 1024 || value.maxFileBytes < 0)
                    throw new InvalidOperationException("Server protocol or authenticated account does not match configuration.");
                if (nativeEnabled && !value.backgroundUpload) throw new InvalidOperationException("Server does not advertise backgroundUpload support.");
                capabilities = value;
            }
            catch (Exception error) { capabilities = null; capabilityFailure = error; throw; }
        }
        async UniTask EnsureCapabilities(CancellationToken token)
        { if (capabilities == null) SetCapabilities(await JsonRequest<ServerCapabilities>(HttpMethod.Get, "/v1/capabilities", null, token)); }
        public async UniTask RefreshServerCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            CheckIdle(); cancellationToken.ThrowIfCancellationRequested(); metadataReads++;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            try { SetCapabilities(await JsonRequest<ServerCapabilities>(HttpMethod.Get, "/v1/capabilities", null, linked.Token)); }
            finally { metadataReads--; }
        }
        internal bool HasPendingNativeTransfers => tasks.Values.Any(x => x.nativeOwned && (x.state == BackupState.Queued || x.state == BackupState.Uploading));
        internal IReadOnlyList<BackupPreparationFailure> GetScanFailures(string key)
        {
            return GetScanIndex(key).entries.Values.Where(x => !string.IsNullOrEmpty(x.error)).Select(x => new BackupPreparationFailure
            { Fingerprint = x.fingerprint, Source = x.source, Name = x.name, Error = x.error }).ToList().AsReadOnly();
        }
        ScanIndex GetScanIndex(string key)
        {
            Check();
            if (!scans.TryGetValue(key, out var index))
            {
                if (scanLoads.ContainsKey(key)) throw new InvalidOperationException("An asynchronous scan load is active; await it before using synchronous reads.");
                index = ReadScan(key, default); scans.Add(key, index);
            }
            return index;
        }
        internal BackupScanState LoadScan(string key) => GetScanIndex(key).Snapshot();
        internal async UniTask EnsureScanAsync(string key, CancellationToken token)
        {
            Check(); token.ThrowIfCancellationRequested();
            if (scans.ContainsKey(key)) return;
            if (!scanLoads.TryGetValue(key, out var pending))
            {
                pending = new UniTaskCompletionSource<ScanIndex>(); scanLoads.Add(key, pending);
                metadataReads++; ReadScanAsync(key, pending).Forget();
            }
            await pending.Task.AttachExternalCancellation(token);
        }
        async UniTaskVoid ReadScanAsync(string key, UniTaskCompletionSource<ScanIndex> completion)
        {
            ScanIndex loaded = null; Exception failure = null;
            try
            {
                loaded = await UniTask.RunOnThreadPool(() => ReadScan(key, lifetime.Token));
                lifetime.Token.ThrowIfCancellationRequested(); scans.Add(key, loaded);
            }
            catch (Exception error) { failure = error; }
            finally { scanLoads.Remove(key); metadataReads--; }
            if (failure != null) completion.TrySetException(failure); else completion.TrySetResult(loaded);
        }
        internal bool ScanHasBaseline(string key) => scans[key].baseline;
        internal bool ScanKnows(string key, string fingerprint) => scans[key].entries.TryGetValue(fingerprint, out var entry) && !entry.retryRequested;
        internal string ScanError(string key) => scans[key].entries.Values.FirstOrDefault(x => !string.IsNullOrEmpty(x.error))?.error;
        ScanIndex ReadScan(string key, CancellationToken token)
        {
            string path = Path.Combine(root, "scan-" + Hash(key) + ".json");
            var state = File.Exists(path) ? JsonUtility.FromJson<BackupScanState>(File.ReadAllText(path)) : new BackupScanState();
            var index = new ScanIndex { baseline = state.baselineEstablished };
            foreach (var entry in state.entries) { NormalizeReceipt(entry); index.entries[entry.fingerprint] = entry; }
            if (state.baselineEstablished && File.Exists(path + ".baseline"))
            {
                using var input = new StreamReader(path + ".baseline"); string line;
                while ((line = input.ReadLine()) != null) { token.ThrowIfCancellationRequested(); var entry = JsonUtility.FromJson<BackupReceipt>(line); NormalizeReceipt(entry); index.entries[entry.fingerprint] = entry; }
            }
            string directory = path + ".entries";
            if (Directory.Exists(directory)) foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                token.ThrowIfCancellationRequested();
                var entry = JsonUtility.FromJson<BackupReceipt>(File.ReadAllText(file)); NormalizeReceipt(entry);
                string canonical = Path.Combine(directory, Hash(entry.fingerprint) + ".json");
                if (!StringComparer.Ordinal.Equals(file, canonical) && File.Exists(canonical)) continue;
                index.entries[entry.fingerprint] = entry;
            }
            return index;
        }
        internal async UniTask EstablishBaselineAsync(string key,
            Func<Func<IReadOnlyList<ImageReference>, UniTask<bool>>, UniTask<bool>> visit, CancellationToken token)
        {
            Check(); metadataReads++;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            string path = Path.Combine(root, "scan-" + Hash(key) + ".json"), temporary = path + ".baseline.new";
            bool failed = false;
            try
            {
                FileStream stream = null; StreamWriter writer = null; var cleanup = new UIFrame.CleanupFailure();
                try
                {
                    stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
                    writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, true);
                    bool completed = await visit(async page => await UniTask.RunOnThreadPool(() =>
                    {
                        foreach (var image in page)
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            writer.WriteLine(JsonUtility.ToJson(new BackupReceipt { fingerprint = image.Source + ":" + image.Id + "\n" + image.Version }));
                        }
                        return true;
                    }));
                    if (!completed) throw new InvalidOperationException("Baseline enumeration did not complete.");
                    linked.Token.ThrowIfCancellationRequested(); writer.Flush(); stream.Flush(true);
                }
                catch (Exception error) { cleanup.Capture(error); }
                finally
                {
                    if (writer != null) cleanup.Run(writer.Dispose);
                    if (stream != null) cleanup.Run(stream.Dispose);
                }
                cleanup.Throw();
                string target = path + ".baseline";
                if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
                AtomicJson(path, new BackupScanState { baselineEstablished = true }); scans.Remove(key);
            }
            catch { failed = true; throw; }
            finally
            {
                metadataReads--;
                if (failed) { try { File.Delete(temporary); } catch (Exception cleanup) { Debug.LogException(cleanup); } }
            }
        }
        internal void SaveScan(string key, BackupScanState state)
        {
            Check(); AtomicJson(Path.Combine(root, "scan-" + Hash(key) + ".json"), state);
            if (!scans.TryGetValue(key, out var index)) { index = new ScanIndex(); scans.Add(key, index); }
            index.baseline = state.baselineEstablished;
            foreach (var entry in state.entries) if (!index.entries.ContainsKey(entry.fingerprint)) index.entries.Add(entry.fingerprint, CloneReceipt(entry));
        }
        internal void SaveScanEntry(string key, BackupReceipt entry)
        {
            var index = GetScanIndex(key);
            string directory = Path.Combine(root, "scan-" + Hash(key) + ".json.entries"); Directory.CreateDirectory(directory);
            AtomicJson(Path.Combine(directory, Hash(entry.fingerprint) + ".json"), entry);
            index.entries[entry.fingerprint] = CloneReceipt(entry);
        }
        internal string PolicyPath => Path.Combine(root, "automatic.json");
        internal static void AtomicJson<T>(string path, T value)
        {
            string temporary = path + ".new";
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(JsonUtility.ToJson(value, true)); stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        static bool IsLocal(string host)
        {
            if (host == "localhost") return true;
            if (!IPAddress.TryParse(host, out var ip)) return false;
            if (IPAddress.IsLoopback(ip)) return true;
            var b = ip.GetAddressBytes(); return b.Length == 4 && (b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] >= 16 && b[1] <= 31);
        }
        async UniTask<T> JsonRequest<T>(HttpMethod method, string path, object data, CancellationToken token)
        {
            using var body = data == null ? null : new StringContent(JsonUtility.ToJson(data), Encoding.UTF8, "application/json");
            return await Send<T>(method, path, body, token);
        }
        async UniTask<T> Send<T>(HttpMethod method, string path, HttpContent content, CancellationToken token)
        {
            using var request = Request(method, path); request.Content = content;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            string text = await ReadResponse(response, token); await CheckResponse(response, token, text);
            return JsonUtility.FromJson<T>(text);
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
        static bool Transient(Exception error) => error is HttpRequestException || error is System.Threading.Tasks.TaskCanceledException || error is BackupHttpException http &&
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
        List<BackupTaskInfo> LoadTasks() => tasks.Values.Select(x => x.Snapshot()).ToList();
        IEnumerable<BackupTaskInfo> ReadTasks() => Directory.EnumerateFiles(Path.Combine(root, "batches"), "*.json", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.Ordinal).Select(x => NormalizeTask(JsonUtility.FromJson<BackupTaskInfo>(File.ReadAllText(x))));
        static BackupTaskInfo NormalizeTask(BackupTaskInfo task) { task.source = ImageIdentity.Source(task.source); return task; }
        static void NormalizeReceipt(BackupReceipt entry) { entry.fingerprint = ImageIdentity.Source(entry.fingerprint); entry.source = ImageIdentity.Source(entry.source); }
        BackupTaskInfo Find(string id) => id != null && tasks.TryGetValue(id, out var record) ? record.Snapshot() : throw new ArgumentException("Task not found.", nameof(id));
        string Payload(BackupTaskInfo task) => Path.Combine(root, "batches", task.batchId, task.id + ".payload");
        void DeletePayload(BackupTaskInfo task)
        {
            string path = Payload(task); long length = File.Exists(path) ? new FileInfo(path).Length : 0;
            File.Delete(path); stagedBytes -= length;
        }
        void Save(BackupTaskInfo task)
        {
            try { AtomicJson(Path.Combine(root, "batches", task.batchId, task.id + ".json"), task); }
            catch (Exception error) { persistenceFailure = error; throw; }
            tasks[task.id] = task.Snapshot();
        }
        internal static string Hash(string text) { using var sha = SHA256.Create(); return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        void Check() { MediaThread.Check(); if (disposed) throw new ObjectDisposedException(nameof(ImageBackupService)); }
        void CheckIdle() { Check(); if (running || accepting || pausing || downloads != 0 || metadataReads != 0) throw new InvalidOperationException("Pause and await the active operation first."); }
        public async UniTask ShutdownAsync()
        {
            Check(); lifetime.Cancel(); while (running || accepting || pausing || downloads != 0 || metadataReads != 0) await UniTask.Yield(); Dispose();
        }
        public void Dispose()
        {
            if (disposed) return; CheckIdle(); disposed = true;
            var cleanup = new UIFrame.CleanupFailure();
            cleanup.Run(client.Dispose); cleanup.Run(ownerLock.Dispose); cleanup.Run(lifetime.Dispose); cleanup.Throw();
        }
    }
}
