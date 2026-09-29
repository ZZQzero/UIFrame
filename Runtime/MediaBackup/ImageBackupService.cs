using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Game.Media.Backup
{
    public enum BackupState { Queued, Uploading, Verifying, Completed, Paused, RetryScheduled, NeedsAttention, Failed, Canceled }
    internal sealed class BackupPolicyWaitException : Exception { }

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
        public bool nativeOwned;
        internal BackupTaskInfo Snapshot() => (BackupTaskInfo)MemberwiseClone();
    }

    [Serializable] internal sealed class BackupIdentity { public string server, account; }
    [Serializable] internal sealed class UploadRequest { public string key, sha256, name, mime, source; public long size; }
    [Serializable] internal sealed class UploadResponse { public string uploadId, sha256, backupId; public long offset, size; public bool completed; }
    [Serializable] internal sealed class ServerCapabilities { public int protocolVersion, chunkBytes; public long maxFileBytes; public string account; public bool backgroundUpload; }
    [Serializable] internal sealed class BackupReceipt { public string fingerprint, key, backupId; }
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

        internal ImageBackupService(BackupConfiguration configuration, Func<NativeBackupRequest, NativeBackupStatus> nativeCall, bool nativeAvailable)
        {
            this.nativeCall = nativeCall ?? throw new ArgumentNullException(nameof(nativeCall)); this.nativeAvailable = nativeAvailable;
            MediaThread.Check();
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
                foreach (var record in LoadTasks())
                {
                    if (!record.nativeOwned && (record.state == BackupState.Uploading || record.state == BackupState.Verifying))
                    { record.state = BackupState.Queued; Save(record); }
                    if (!record.nativeOwned && (record.state == BackupState.Completed || record.state == BackupState.Canceled)) DeletePayload(record);
                }
                client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
            }
            catch { ownerLock.Dispose(); throw; }
        }

        public IReadOnlyList<BackupTaskInfo> GetTasks()
        {
            Check(); ReconcileNative(); return LoadTasks().Select(x => x.Snapshot()).ToList().AsReadOnly();
        }

        public async UniTask<IReadOnlyList<string>> EnqueueAsync(IReadOnlyList<ImageReference> images, CancellationToken cancellationToken = default)
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
                long bytes = Directory.EnumerateFiles(Path.Combine(root, "batches"), "*.payload", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
                foreach (var source in sources)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    using var file = await GameImageReader.ExportFileAsync(source, cancellationToken: linked.Token);
                    if (file.ByteCount > maximum || file.ByteCount > budget - bytes) throw new IOException("Backup staging budget or per-file limit exceeded.");
                    string id = Guid.NewGuid().ToString("N"), destination = Path.Combine(folder, id + ".payload");
                    string path = file.LocalPath;
                    string hash = await UniTask.RunOnThreadPool(() =>
                    {
                        GameGallery.CopyFile(path, destination, linked.Token);
                        using var stream = File.OpenRead(destination); using var sha = SHA256.Create(); return Hex(sha.ComputeHash(stream));
                    }, cancellationToken: linked.Token);
                    string sourceIdentity = source.Source + ":" + source.OriginId;
                    var record = new BackupTaskInfo { id = id, batchId = batch, source = sourceIdentity, version = source.Version,
                        name = source.FileName, mime = file.MimeType, sha256 = hash, size = file.ByteCount,
                        key = Hash(sourceIdentity + "\n" + hash), state = BackupState.Queued };
                    AtomicJson(Path.Combine(folder, id + ".json"), record); records.Add(record); bytes += file.ByteCount;
                }
                linked.Token.ThrowIfCancellationRequested();
                Directory.Move(folder, Path.Combine(root, "batches", batch));
                return records.Select(x => x.id).ToList().AsReadOnly();
            }
            catch { ImagePaths.CleanAfterFailure(folder); throw; }
            finally { accepting = false; }
        }

        /// <summary>Processes eligible jobs once. In native mode returns after durable OS submission, not upload completion. Cancellation stops submission only.</summary>
        public async UniTask ProcessAsync(CancellationToken cancellationToken = default, Func<bool> canTransfer = null)
        {
            Check(); if (running || pausing) throw new InvalidOperationException("A backup pass is already running or the queue is pausing.");
            if (nativeEnabled && canTransfer != null) throw new ArgumentException("A managed callback cannot enforce a background network policy. Configure NativeWifiOnly instead.");
            cancellationToken.ThrowIfCancellationRequested(); running = true;
            processing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            var token = processing.Token;
            try
            {
                ReconcileNative();
                if (canTransfer != null && !canTransfer()) return;
                var capabilities = await JsonRequest<ServerCapabilities>(HttpMethod.Get, "/v1/capabilities", null, token);
                if (capabilities.protocolVersion != 1 || capabilities.account != account || capabilities.chunkBytes < 1 || capabilities.chunkBytes > 4 * 1024 * 1024)
                    throw new InvalidOperationException("Server protocol or authenticated account does not match configuration.");
                if (nativeEnabled && !capabilities.backgroundUpload) throw new InvalidOperationException("Server does not advertise backgroundUpload support.");
                foreach (var record in LoadTasks())
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
                        else await Upload(record, capabilities.chunkBytes, token, canTransfer);
                    }
                    catch (BackupPolicyWaitException) { record.state = BackupState.Queued; Save(record); break; }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        if (!record.nativeOwned) { record.state = BackupState.Queued; Save(record); } throw;
                    }
                    catch (Exception error)
                    {
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
            finally { processing.Dispose(); processing = null; running = false; }
        }

        async UniTask SubmitNative(BackupTaskInfo record, CancellationToken token)
        {
            var session = await JsonRequest<UploadResponse>(HttpMethod.Post, "/v1/uploads", new UploadRequest
            { key = record.key, sha256 = record.sha256, size = record.size, name = record.name, mime = record.mime, source = record.source }, token);
            ValidateResponse(record, session);
            if (session.completed)
            {
                if (string.IsNullOrEmpty(session.backupId)) throw new InvalidOperationException("Missing committed backup ID.");
                record.state = BackupState.Completed; record.backupId = session.backupId; record.confirmedBytes = record.size;
                record.nativeOwned = false; Save(record); DeletePayload(record); return;
            }
            string credential = accessToken();
            if (string.IsNullOrWhiteSpace(credential)) throw new InvalidOperationException("No access token available.");
            token.ThrowIfCancellationRequested();
            record.nativeOwned = true; record.state = BackupState.Queued; record.error = null; Save(record);
            nativeCall(new NativeBackupRequest { op = "submit", id = record.id, payload = Payload(record),
                url = server + "/v1/uploads/" + record.key + "/background", account = Hash(account), token = credential,
                key = record.key, sha256 = record.sha256, size = record.size, wifiOnly = nativeWifiOnly });
        }

        NativeBackupStatus NativeStatus(BackupTaskInfo record) => nativeCall(new NativeBackupRequest { op = "status", id = record.id });
        void ReconcileNative()
        {
            foreach (var record in LoadTasks().Where(x => x.nativeOwned))
            {
                var status = NativeStatus(record);
                if (!status.exists) continue; // Durable handoff intent; next ProcessAsync submits it.
                record.state = status.state; record.error = status.error; record.backupId = status.backupId;
                record.confirmedBytes = status.confirmedBytes; Save(record);
                if (status.released && (status.state == BackupState.Completed || status.state == BackupState.Canceled))
                {
                    record.nativeOwned = false; Save(record); DeletePayload(record);
                    nativeCall(new NativeBackupRequest { op = "forget", id = record.id });
                }
            }
        }
        void ReleaseNativeForRetry(BackupTaskInfo record)
        {
            if (!record.nativeOwned) return;
            var status = NativeStatus(record);
            if (status.exists && !status.released) throw new InvalidOperationException("Native cancellation is still settling. Await PauseAsync or wait for the terminal status.");
            nativeCall(new NativeBackupRequest { op = "forget", id = record.id });
            record.nativeOwned = false;
        }

        async UniTask Upload(BackupTaskInfo record, int chunkSize, CancellationToken token, Func<bool> canTransfer)
        {
            void CheckPolicy() { if (canTransfer != null && !canTransfer()) throw new BackupPolicyWaitException(); }
            CheckPolicy();
            record.state = BackupState.Uploading; record.error = null; Save(record);
            var session = await JsonRequest<UploadResponse>(HttpMethod.Post, "/v1/uploads", new UploadRequest
            { key = record.key, sha256 = record.sha256, size = record.size, name = record.name, mime = record.mime, source = record.source }, token);
            ValidateResponse(record, session);
            if (!session.completed)
            {
                using var input = File.OpenRead(Payload(record));
                if (input.Length != record.size) throw new IOException("Staged backup file size changed.");
                using (var hash = SHA256.Create())
                {
                    string actual = await UniTask.RunOnThreadPool(() => Hex(hash.ComputeHash(input)), cancellationToken: token);
                    if (actual != record.sha256) throw new IOException("Staged backup checksum changed.");
                }
                input.Position = session.offset; var buffer = new byte[chunkSize];
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
            record.backupId = session.backupId; record.confirmedBytes = record.size; record.state = BackupState.Completed; Save(record);
            DeletePayload(record);
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
                while (LoadTasks().Any(x => x.nativeOwned && NativeStatus(x).exists && !NativeStatus(x).released))
                    await UniTask.Yield(PlayerLoopTiming.Update, cancellationToken);
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
            record.state = BackupState.Canceled; Save(record); DeletePayload(record);
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
                await CheckResponse(response);
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { await input.CopyToAsync(output, 128 * 1024, cancellationToken); output.Flush(true); }
                using (var file = File.OpenRead(temporary)) using (var sha = SHA256.Create())
                {
                    if (file.Length != record.size || Hex(sha.ComputeHash(file)) != record.sha256) throw new IOException("Downloaded backup checksum mismatch.");
                }
                cancellationToken.ThrowIfCancellationRequested(); File.Move(temporary, destination);
            }
            catch { try { File.Delete(temporary); } catch (Exception cleanup) { Debug.LogException(cleanup); } throw; }
            finally { downloads--; }
        }

        internal BackupScanState LoadScan(string key)
        {
            Check(); string path = Path.Combine(root, "scan-" + Hash(key) + ".json");
            return File.Exists(path) ? JsonUtility.FromJson<BackupScanState>(File.ReadAllText(path)) : new BackupScanState();
        }
        internal void SaveScan(string key, BackupScanState state) { Check(); AtomicJson(Path.Combine(root, "scan-" + Hash(key) + ".json"), state); }
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
            using var response = await client.SendAsync(request, token); await CheckResponse(response);
            return JsonUtility.FromJson<T>(await response.Content.ReadAsStringAsync());
        }
        HttpRequestMessage Request(HttpMethod method, string path)
        {
            string token = accessToken(); if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("No access token available.");
            var request = new HttpRequestMessage(method, server + path); request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token); return request;
        }
        static async UniTask CheckResponse(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode) return;
            TimeSpan? retry = response.Headers.RetryAfter?.Delta;
            if (!retry.HasValue && response.Headers.RetryAfter?.Date != null) retry = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
            throw new BackupHttpException((int)response.StatusCode, await response.Content.ReadAsStringAsync(), retry);
        }
        static bool Transient(Exception error) => error is HttpRequestException || error is System.Threading.Tasks.TaskCanceledException || error is BackupHttpException http &&
            (http.StatusCode == 408 || http.StatusCode == 429 || http.StatusCode == 500 || http.StatusCode == 502 || http.StatusCode == 503 || http.StatusCode == 504);
        List<BackupTaskInfo> LoadTasks() => Directory.EnumerateFiles(Path.Combine(root, "batches"), "*.json", SearchOption.AllDirectories)
            .OrderBy(x => x, StringComparer.Ordinal).Select(x => JsonUtility.FromJson<BackupTaskInfo>(File.ReadAllText(x))).ToList();
        BackupTaskInfo Find(string id) => LoadTasks().Find(x => x.id == id) ?? throw new ArgumentException("Task not found.", nameof(id));
        string Payload(BackupTaskInfo task) => Path.Combine(root, "batches", task.batchId, task.id + ".payload");
        void DeletePayload(BackupTaskInfo task) { File.Delete(Payload(task)); }
        void Save(BackupTaskInfo task) => AtomicJson(Path.Combine(root, "batches", task.batchId, task.id + ".json"), task);
        internal static string Hash(string text) { using var sha = SHA256.Create(); return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text))); }
        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        void Check() { MediaThread.Check(); if (disposed) throw new ObjectDisposedException(nameof(ImageBackupService)); }
        void CheckIdle() { Check(); if (running || accepting || pausing || downloads != 0) throw new InvalidOperationException("Pause and await the active operation first."); }
        public async UniTask ShutdownAsync()
        {
            Check(); lifetime.Cancel(); while (running || accepting || pausing || downloads != 0) await UniTask.Yield(); Dispose();
        }
        public void Dispose()
        {
            if (disposed) return; CheckIdle(); disposed = true;
            client.Dispose(); ownerLock.Dispose(); lifetime.Dispose();
        }
    }
}
