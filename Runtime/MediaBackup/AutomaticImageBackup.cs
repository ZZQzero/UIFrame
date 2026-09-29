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
        public bool SupportsBackgroundDiscovery => false;
        public AutomaticBackupPolicy Policy => JsonUtility.FromJson<AutomaticBackupPolicy>(JsonUtility.ToJson(policy));

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
            running = true;
            try
            {
                ImageSnapshot snapshot;
                if (policy.sourceKind == BackupSourceKind.PhotoLibrary)
                {
                    var access = await GameGallery.GetLibraryAccessAsync(cancellationToken);
                    if (access != LibraryAccess.Authorized && access != LibraryAccess.Limited) throw new GalleryException("PermissionDenied", "Automatic backup cannot access the configured photo library.");
                    snapshot = await GameGallery.QueryImagesAsync(policy.source, cancellationToken);
                }
                else if (policy.sourceKind == BackupSourceKind.GrantedDirectory)
                    snapshot = await ImageDirectoryHandle.FromBookmark(policy.source).QueryAsync(policy.recursive, cancellationToken);
                else snapshot = await GameImageDirectory.QueryAsync(policy.source, policy.recursive, cancellationToken);
                string scope = policy.sourceKind + ":" + policy.source + ":" + policy.recursive;
                var state = service.LoadScan(scope); var known = new HashSet<string>(state.entries.Select(x => x.fingerprint));
                if (!state.baselineEstablished && !policy.includeExisting)
                {
                    // Commit one complete baseline. A canceled partial walk must not silently absorb later photos.
                    var baseline = new BackupScanState { baselineEstablished = true };
                    for (int offset = 0; offset < snapshot.Count; offset += 60)
                        foreach (var image in snapshot.GetPage(offset))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            baseline.entries.Add(new BackupReceipt { fingerprint = image.Source + ":" + image.Id + "\n" + image.Version });
                        }
                    cancellationToken.ThrowIfCancellationRequested(); service.SaveScan(scope, baseline);
                    LastScanUtc = DateTime.UtcNow; LastError = null; return;
                }
                var existing = service.GetTasks();
                for (int offset = 0; offset < snapshot.Count; offset += 60)
                {
                    foreach (var image in snapshot.GetPage(offset))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (policy.wifiOnly && !wifiAvailable()) return;
                        string fingerprint = image.Source + ":" + image.Id + "\n" + image.Version;
                        if (known.Contains(fingerprint)) continue;
                        if (state.baselineEstablished || policy.includeExisting)
                        {
                            // Recover an accepted job before writing its scan receipt after a crash.
                            var previous = existing.FirstOrDefault(x => x.source == image.Source + ":" + image.OriginId && x.version == image.Version && x.state != BackupState.Canceled);
                            if (previous == null) await service.EnqueueAsync(new[] { image }, cancellationToken);
                        }
                        state.entries.Add(new BackupReceipt { fingerprint = fingerprint }); known.Add(fingerprint);
                        service.SaveScan(scope, state);
                        // Drain prepared files between discoveries instead of copying the entire library first.
                        if (uploadDuringScan) await service.ProcessAsync(cancellationToken, service.UsesNativeBackgroundTransfer ? null : policy.wifiOnly ? wifiAvailable : null);
                    }
                }
                state.baselineEstablished = true; service.SaveScan(scope, state);
                LastScanUtc = DateTime.UtcNow; LastError = null;
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
                        await ScanOnceAsync(cancellationToken, uploadDuringScan: true);
                        await service.ProcessAsync(cancellationToken, service.UsesNativeBackgroundTransfer ? null : policy.wifiOnly ? wifiAvailable : null);
                    }
                    await UniTask.Delay(TimeSpan.FromSeconds(policy.scanIntervalSeconds), ignoreTimeScale: true, cancellationToken: cancellationToken);
                }
            }
            finally { looping = false; }
        }
    }
}
