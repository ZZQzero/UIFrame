using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Game.Media.Backup
{
    public enum BackupSourceKind { Directory, PhotoLibrary, GrantedDirectory }
    public sealed class BackupPreparationFailure
    {
        public string Fingerprint { get; internal set; }
        public string Source { get; internal set; }
        public string Name { get; internal set; }
        public string Error { get; internal set; }
    }
    [Serializable]
    public sealed class AutomaticBackupPolicy
    {
        public bool enabled;
        public BackupSourceKind sourceKind;
        public string source;
        public bool recursive;
        public bool includeExisting;
        public bool wifiOnly = true;
        public int scanIntervalSeconds = 300;
    }

    /// <summary>Foreground discovery. Call RunAsync from the app lifetime, not a panel lifetime.</summary>
    public sealed class AutomaticImageBackup
    {
        readonly ImageBackupService service;
        readonly Func<bool> wifiAvailable;
        AutomaticBackupPolicy policy;
        bool running, looping;
        public string LastError { get; private set; }
        public DateTime? LastScanUtc { get; private set; }
        public bool IsWaitingForCapacity { get; private set; }
        public bool SupportsBackgroundDiscovery => false;
        public AutomaticBackupPolicy Policy => JsonUtility.FromJson<AutomaticBackupPolicy>(JsonUtility.ToJson(policy));
        string Scope => policy.sourceKind + ":" + policy.source + ":" + policy.recursive;

        public IReadOnlyList<BackupPreparationFailure> GetPreparationFailures()
        {
            MediaThread.Check();
            return service.GetScanFailures(Scope);
        }

        public async UniTask<IReadOnlyList<BackupPreparationFailure>> GetPreparationFailuresAsync(CancellationToken cancellationToken = default)
        {
            MediaThread.Check(); await service.EnsureScanAsync(Scope, cancellationToken);
            return GetPreparationFailures();
        }

        /// <summary>Explicitly makes failed source versions eligible for the next scan.</summary>
        public void RetryPreparationFailures()
        {
            MediaThread.Check();
            if (running || looping) throw new InvalidOperationException("Cancel and await the automatic loop before retrying preparation.");
            foreach (var entry in service.LoadScan(Scope).entries.Where(x => !string.IsNullOrEmpty(x.error)))
            { entry.retryRequested = true; service.SaveScanEntry(Scope, entry); }
        }

        /// <param name="wifiAvailable">Required for Wi-Fi-only mode. Supply a platform-verified network policy check; Unity reachability is not a Wi-Fi guarantee.</param>
        public AutomaticImageBackup(ImageBackupService service, Func<bool> wifiAvailable = null)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.wifiAvailable = wifiAvailable ?? (NativeMedia.Available ? GameMediaNetwork.IsUnmeteredWifi : (Func<bool>)null);
            policy = File.Exists(service.PolicyPath) ? JsonUtility.FromJson<AutomaticBackupPolicy>(File.ReadAllText(service.PolicyPath)) : new AutomaticBackupPolicy();
        }
        public void Configure(AutomaticBackupPolicy value)
        {
            MediaThread.Check(); if (value == null) throw new ArgumentNullException(nameof(value));
            if (running || looping) throw new InvalidOperationException("Cancel and await the automatic loop before changing its policy.");
            if (!Enum.IsDefined(typeof(BackupSourceKind), value.sourceKind) || value.scanIntervalSeconds < 30) throw new ArgumentOutOfRangeException(nameof(value));
            if (value.enabled && value.sourceKind == BackupSourceKind.Directory && (string.IsNullOrEmpty(value.source) || !Path.IsPathRooted(value.source)))
                throw new ArgumentException("Directory backup requires an absolute path.");
            if (value.enabled && value.wifiOnly && wifiAvailable == null) throw new InvalidOperationException("Provide a verified Wi-Fi policy check before enabling Wi-Fi-only backup.");
            if (value.enabled && service.UsesNativeBackgroundTransfer && value.wifiOnly != service.NativeWifiOnly)
                throw new ArgumentException("Automatic and native background Wi-Fi policies must match.");
            var copy = JsonUtility.FromJson<AutomaticBackupPolicy>(JsonUtility.ToJson(value));
            ImageBackupService.AtomicJson(service.PolicyPath, copy); policy = copy;
        }

        public async UniTask ScanOnceAsync(CancellationToken cancellationToken = default, bool uploadDuringScan = false)
        {
            MediaThread.Check(); if (!policy.enabled) throw new InvalidOperationException("Automatic backup is disabled.");
            if (running) throw new InvalidOperationException("An automatic scan is already active.");
            if (service.UsesNativeBackgroundTransfer && policy.wifiOnly != service.NativeWifiOnly)
                throw new InvalidOperationException("Persisted automatic and native background network policies differ. Reconfigure before scanning.");
            if (policy.wifiOnly && wifiAvailable == null) throw new InvalidOperationException("No verified Wi-Fi policy provider is available.");
            if (policy.wifiOnly && (wifiAvailable == null || !wifiAvailable())) return;
            running = true; IsWaitingForCapacity = false;
            try
            {
                async UniTask<bool> Visit(Func<IReadOnlyList<ImageReference>, UniTask<bool>> consume)
                {
                    if (policy.sourceKind == BackupSourceKind.PhotoLibrary)
                    {
                        var access = await GameGallery.GetLibraryAccessAsync(cancellationToken);
                        if (access != LibraryAccess.Authorized && access != LibraryAccess.Limited) throw new GalleryException("PermissionDenied", "Automatic backup cannot access the configured photo library.");
                        return await GameGallery.VisitImagesAsync(consume, policy.source, cancellationToken);
                    }
                    else if (policy.sourceKind == BackupSourceKind.GrantedDirectory)
                        return await ImageDirectoryHandle.FromBookmark(policy.source).VisitAsync(consume, policy.recursive, cancellationToken);
                    else return await GameImageDirectory.VisitAsync(policy.source, consume, policy.recursive, cancellationToken);
                }
                string scope = Scope;
                await service.EnsureScanAsync(scope, cancellationToken);
                bool baseline = service.ScanHasBaseline(scope);
                if (!baseline && !policy.includeExisting)
                {
                    await service.EstablishBaselineAsync(scope, Visit, cancellationToken);
                    LastScanUtc = DateTime.UtcNow; LastError = null; return;
                }
                var existing = new Dictionary<(string source, string version), string>();
                foreach (var task in service.GetTasks().Where(x => x.state != BackupState.Canceled)) existing[(task.source, task.version)] = task.id;
                bool completed = await Visit(async page =>
                {
                    foreach (var image in page)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (policy.wifiOnly && !wifiAvailable()) return false;
                        string fingerprint = image.Source + ":" + image.Id + "\n" + image.Version;
                        if (service.ScanKnows(scope, fingerprint)) continue;
                        var receipt = new BackupReceipt { fingerprint = fingerprint, source = image.Source + ":" + image.OriginId, name = image.FileName };
                        IReadOnlyList<string> accepted = null;
                        if (baseline || policy.includeExisting)
                        {
                            // Recover an accepted job before writing its scan receipt after a crash.
                            if (existing.TryGetValue((receipt.source, image.Version), out var previous)) accepted = new[] { previous };
                            else
                            {
                                try
                                {
                                    accepted = await service.EnqueueBatchAsync(new[] { image }, cancellationToken);
                                    existing[(receipt.source, image.Version)] = accepted[0];
                                }
                                catch (BackupBudgetExceededException) when (uploadDuringScan && service.HasPendingNativeTransfers)
                                { IsWaitingForCapacity = true; return false; }
                                catch (BackupSourceFailure failure)
                                {
                                    receipt.error = failure.Original.SourceException.ToString();
                                }
                            }
                        }
                        service.SaveScanEntry(scope, receipt);
                        // Drain prepared files between discoveries instead of copying the entire library first.
                        if (uploadDuringScan && accepted != null)
                            await service.ProcessTasksAsync(accepted, cancellationToken, service.UsesNativeBackgroundTransfer ? null : policy.wifiOnly ? wifiAvailable : null);
                    }
                    return true;
                });
                if (!completed) return;
                if (!baseline) service.SaveScan(scope, new BackupScanState { baselineEstablished = true });
                LastScanUtc = DateTime.UtcNow; LastError = service.ScanError(scope);
            }
            catch (Exception error) { LastError = error.Message; throw; }
            finally { running = false; }
        }

        public async UniTask RunAsync(CancellationToken cancellationToken)
        {
            MediaThread.Check(); if (looping) throw new InvalidOperationException("An automatic backup loop is already running.");
            if (policy.enabled && policy.wifiOnly && wifiAvailable == null) throw new InvalidOperationException("No verified Wi-Fi policy provider is available.");
            looping = true;
            try
            {
                while (policy.enabled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!policy.wifiOnly || wifiAvailable != null && wifiAvailable())
                    {
                        // Existing receipts can hide queued payloads from discovery; drain those before reserving more space.
                        await service.ProcessAsync(cancellationToken, service.UsesNativeBackgroundTransfer ? null : policy.wifiOnly ? wifiAvailable : null);
                        await ScanOnceAsync(cancellationToken, uploadDuringScan: true);
                    }
                    await UniTask.Delay(TimeSpan.FromSeconds(policy.scanIntervalSeconds), ignoreTimeScale: true, cancellationToken: cancellationToken);
                }
            }
            finally { looping = false; }
        }
    }
}
