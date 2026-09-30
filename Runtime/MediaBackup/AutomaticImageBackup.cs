using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Command = Game.Media.Backup.BackupRepository.Command;

namespace Game.Media.Backup
{
    public enum BackupSourceKind { Directory, PhotoLibrary, GrantedDirectory }
    public sealed class BackupPreparationFailure
    {
        public string Source { get; internal set; }
        public string Version { get; internal set; }
        public string Name { get; internal set; }
        public string Error { get; internal set; }
    }
    public sealed class AutomaticBackupPolicy
    {
        public bool enabled;
        public BackupSourceKind sourceKind;
        public string source;
        public bool recursive;
        public bool includeExisting;
        public bool wifiOnly=true;
        public int scanIntervalSeconds=300;
        internal AutomaticBackupPolicy Copy() => (AutomaticBackupPolicy)MemberwiseClone();
    }
    public sealed class BackupPreparationFailurePage
    {
        public IReadOnlyList<BackupPreparationFailure> Items { get; internal set; }
        public BackupDiscoveryCursor Next { get; internal set; }
    }
    public sealed class BackupDiscoveryCursor
    {
        internal string Store,Scope,Source,Version;
    }

    /// <summary>Foreground discovery consumes the independent library index and durable catalog discoveries.</summary>
    public sealed class AutomaticImageBackup
    {
        readonly ImageBackupService service;
        readonly ImageLibraryIndex library;
        readonly Func<bool> wifiAvailable;
        AutomaticBackupPolicy policy;
        bool running,looping;
        public string LastError { get; private set; }
        public DateTime? LastScanUtc { get; private set; }
        public bool IsWaitingForCapacity { get; private set; }
        public bool SupportsBackgroundDiscovery => false;
        public AutomaticBackupPolicy Policy => policy.Copy();
        ImageLibraryScope Scope => new ImageLibraryScope((ImageLibrarySourceKind)policy.sourceKind,policy.source,policy.recursive);
        string CatalogScope => ImageBackupService.Hash(Scope.Id+":"+policy.includeExisting);
        AutomaticImageBackup(ImageBackupService service,ImageLibraryIndex library,Func<bool> wifiAvailable)
        {
            this.service=service??throw new ArgumentNullException(nameof(service));this.library=library??throw new ArgumentNullException(nameof(library));
            this.wifiAvailable=wifiAvailable??(NativeMedia.Available?GameMediaNetwork.IsUnmeteredWifi:(Func<bool>)null);
        }
        public static async UniTask<AutomaticImageBackup> CreateAsync(ImageBackupService service,ImageLibraryIndex library,Func<bool> wifiAvailable=null,CancellationToken token=default)
        {
            MediaThread.Check();var value=new AutomaticImageBackup(service,library,wifiAvailable);
            var row=(await service.Db(Command.Policy,token)).Single;
            value.policy=row["automatic_policy"]==null?new AutomaticBackupPolicy():Decode((byte[])row["automatic_policy"]);
            value.Validate(value.policy);return value;
        }
        void Validate(AutomaticBackupPolicy value)
        {
            if(value==null) throw new ArgumentNullException(nameof(value));
            if(!Enum.IsDefined(typeof(BackupSourceKind),value.sourceKind) || value.scanIntervalSeconds<30) throw new ArgumentOutOfRangeException(nameof(value));
            if(!value.enabled) return;
            _=new ImageLibraryScope((ImageLibrarySourceKind)value.sourceKind,value.source,value.recursive);
            if(value.wifiOnly && wifiAvailable==null) throw new InvalidOperationException("Wi-Fi-only discovery requires a verified network policy provider.");
            if(service.UsesNativeBackgroundTransfer && value.wifiOnly!=service.NativeWifiOnly) throw new ArgumentException("Automatic and native background Wi-Fi policies must match.");
        }
        public async UniTask ConfigureAsync(AutomaticBackupPolicy value,CancellationToken token=default)
        {
            MediaThread.Check();if(running || looping) throw new InvalidOperationException("Cancel and await the automatic loop before changing its policy.");
            Validate(value);var copy=value.Copy();await service.Db(Command.SetPolicy,token,Encode(copy));policy=copy;
        }
        static byte[] Encode(AutomaticBackupPolicy value)
        {
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,new UTF8Encoding(false,true),true);
            writer.Write(value.enabled);writer.Write((int)value.sourceKind);writer.Write(value.source??"");writer.Write(value.recursive);
            writer.Write(value.includeExisting);writer.Write(value.wifiOnly);writer.Write(value.scanIntervalSeconds);return stream.ToArray();
        }
        static AutomaticBackupPolicy Decode(byte[] bytes)
        {
            using var stream=new MemoryStream(bytes,false);using var reader=new BinaryReader(stream,new UTF8Encoding(false,true));
            var value=new AutomaticBackupPolicy { enabled=reader.ReadBoolean(),sourceKind=(BackupSourceKind)reader.ReadInt32(),source=reader.ReadString(),
                recursive=reader.ReadBoolean(),includeExisting=reader.ReadBoolean(),wifiOnly=reader.ReadBoolean(),scanIntervalSeconds=reader.ReadInt32() };
            if(stream.Position!=stream.Length) throw new InvalidDataException("Trailing automatic backup policy data.");return value;
        }
        public async UniTask<BackupPreparationFailurePage> GetPreparationFailuresAsync(int pageSize=100,BackupDiscoveryCursor cursor=null,CancellationToken token=default)
        {
            MediaThread.Check();if(pageSize<1 || pageSize>200) throw new ArgumentOutOfRangeException(nameof(pageSize));string scope=CatalogScope;
            if(cursor!=null && (cursor.Store!=service.StoreId || cursor.Scope!=scope)) throw new ArgumentException("Cursor belongs to another scope.");
            var rows=(await service.Db(Command.Discoveries,token,scope,cursor?.Source??"",cursor?.Version??"",4,pageSize)).Rows;
            var items=rows.Select(r=>new BackupPreparationFailure { Source=r.Text("source_id"),Version=r.Text("content_version"),Name=r.Text("name"),Error=r.Text("error") }).ToList().AsReadOnly();
            var last=items.Count==0?null:items[items.Count-1];return new BackupPreparationFailurePage { Items=items,Next=items.Count==pageSize?new BackupDiscoveryCursor { Store=service.StoreId,Scope=scope,Source=last.Source,Version=last.Version }:null };
        }
        public async UniTask RetryPreparationFailuresAsync(CancellationToken token=default)
        {
            MediaThread.Check();if(running || looping) throw new InvalidOperationException("Await the automatic loop before retrying preparation.");
            BackupDiscoveryCursor cursor=null;
            do
            {
                var page=await GetPreparationFailuresAsync(100,cursor,token);
                foreach(var entry in page.Items) await service.Db(Command.Disposition,token,CatalogScope,entry.Source,entry.Version,0,"");
                cursor=page.Next;
            } while(cursor!=null);
        }
        static ImageLibraryPosition CopyPosition(ImageLibraryPosition value,long sequence) => new ImageLibraryPosition {
            LibraryId=value.LibraryId,IndexGeneration=value.IndexGeneration,ScopeId=value.ScopeId,ScopeRevision=value.ScopeRevision,
            PermissionGeneration=value.PermissionGeneration,Sequence=sequence,RetainedAfter=value.RetainedAfter,RequiresRefresh=value.RequiresRefresh };
        async UniTask<ImageLibraryPosition> ConsumeChanges(ImageLibraryScope source,string scope,ImageLibraryPosition cursor,CancellationToken token)
        {
            for(;;)
            {
                var batch=await library.ReadChangesAsync(source,cursor,32,token);
                if(batch.RequiresRefresh) throw new InvalidOperationException("Library change history requires complete reconciliation.");
                var p=batch.Position;var args=new List<object>{scope,p.LibraryId,p.IndexGeneration,p.ScopeRevision,p.PermissionGeneration,cursor.Sequence,p.Sequence,p.RetainedAfter,batch.Items.Count};
                foreach(var change in batch.Items)
                {
                    int colon=change.SourceIdentity.IndexOf(':');string provider=change.SourceIdentity.Substring(colon+1);
                    args.AddRange(new object[]{change.Sequence,(int)change.Kind,change.SourceIdentity,change.Version??"",change.Name??"",change.Mime??"",provider,0L});
                }
                await service.Db(Command.Discover,token,args.ToArray());cursor=p;
                if(batch.Items.Count<32) return cursor;
            }
        }
        public async UniTask ScanOnceAsync(CancellationToken cancellationToken=default,bool uploadDuringScan=false,bool completeReconciliation=true)
        {
            MediaThread.Check();Validate(policy);if(!policy.enabled) throw new InvalidOperationException("Automatic backup is disabled.");
            if(running) throw new InvalidOperationException("An automatic scan is already active.");
            if(policy.wifiOnly && !wifiAvailable()) return;
            running=true;IsWaitingForCapacity=false;
            try
            {
                var source=Scope;string scope=CatalogScope;await library.RefreshAsync(source,cancellationToken,completeReconciliation);
                var position=await library.GetPositionAsync(source,cancellationToken);
                var previous=(await service.Db(Command.ScopeState,cancellationToken,scope)).Rows;
                if(previous.Count!=0 && previous[0].Text("library_id")==position.LibraryId && previous[0].Number("permission_generation")!=position.PermissionGeneration)
                    await SuspendScopeAsync(cancellationToken);
                var state=(await service.Db(Command.Scope,cancellationToken,scope,source.Id,position.LibraryId,position.IndexGeneration,position.ScopeRevision,position.PermissionGeneration,policy.includeExisting)).Single;
                bool reconcile=!state.Flag("enabled") || state.Number("consumed_seq")<position.RetainedAfter;
                if(reconcile)
                {
                    string run=Guid.NewGuid().ToString("N");long start=position.Sequence;
                    await service.Db(Command.BeginScan,cancellationToken,scope,run,position.LibraryId,position.IndexGeneration,position.ScopeRevision,position.PermissionGeneration,start);
                    ImageLibraryCursor cursor=null;
                    do
                    {
                        var page=await library.QueryAsync(source,32,cursor,cancellationToken);
                        if(page.Items.Count!=0)
                        {
                            var args=new List<object>{scope,run,page.Items.Count};
                            foreach(var image in page.Items)
                            {
                                if(string.IsNullOrEmpty(image.Version)) throw new GalleryException("ContentVersionUnavailable","The provider cannot identify the current image version.");
                                args.AddRange(new object[]{image.Source+":"+image.OriginId,image.Version,image.FileName,image.MimeType,image.Id,image.ByteCount??0});
                            }
                            await service.Db(Command.ScanPage,cancellationToken,args.ToArray());
                        }
                        cursor=page.Next;
                    } while(cursor!=null);
                    var through=await ConsumeChanges(source,scope,position,cancellationToken);
                    var latest=await library.GetPositionAsync(source,cancellationToken);
                    if(latest.LibraryId!=position.LibraryId || latest.IndexGeneration!=position.IndexGeneration || latest.ScopeRevision!=position.ScopeRevision || latest.PermissionGeneration!=position.PermissionGeneration)
                        throw new InvalidOperationException("Library scope changed during baseline reconciliation.");
                    await service.Db(Command.ActivateScan,cancellationToken,scope,run,position.LibraryId,position.IndexGeneration,position.ScopeRevision,position.PermissionGeneration,start,through.Sequence,latest.RetainedAfter);
                }
                else await ConsumeChanges(source,scope,CopyPosition(position,state.Number("consumed_seq")),cancellationToken);
                await service.SynchronizeNativeAsync();
                string afterSource="",afterVersion="";
                for(;;)
                {
                    var pending=(await service.Db(Command.Discoveries,cancellationToken,scope,afterSource,afterVersion,0,32)).Rows;
                    foreach(var candidate in pending)
                    {
                        cancellationToken.ThrowIfCancellationRequested();if(policy.wifiOnly && !wifiAvailable()) return;
                        afterSource=candidate.Text("source_id");afterVersion=candidate.Text("content_version");int colon=afterSource.IndexOf(':');
                        var image=new ImageReference(afterSource.Substring(0,colon),candidate.Text("provider_id"),candidate.Text("name"),candidate.Text("mime"),version:afterVersion);
                        IReadOnlyList<string> ids;
                        try { ids=await service.EnqueueBatchAsync(new[]{image},cancellationToken); }
                        catch(BackupBudgetExceededException) { IsWaitingForCapacity=true;return; }
                        catch(BackupSourceFailure failure)
                        {
                            await service.Db(Command.Disposition,cancellationToken,scope,afterSource,afterVersion,4,failure.Original.SourceException.ToString());continue;
                        }
                        if(uploadDuringScan) await service.ProcessTasksAsync(ids,cancellationToken,service.UsesNativeBackgroundTransfer?null:policy.wifiOnly?wifiAvailable:null);
                    }
                    if(pending.Count<32) break;
                }
                LastScanUtc=DateTime.UtcNow;var failures=await GetPreparationFailuresAsync(1,null,cancellationToken);LastError=failures.Items.FirstOrDefault()?.Error;
            }
            catch(Exception error)
            {
                LastError=error.Message;
                if(error is GalleryException gallery && gallery.Code=="PermissionDenied")
                {
                    try {await SuspendScopeAsync(default);}catch(Exception secondary){UnityEngine.Debug.LogException(secondary);}
                }
                throw;
            }
            finally { running=false; }
        }
        async UniTask SuspendScopeAsync(CancellationToken token)
        {
            long cursor=0;for(;;)
            {
                var result=(await service.Db(Command.SuspendScope,token,CatalogScope,cursor,ImageBackupService.Now)).Single;cursor=result.Number("cursor");
                if(result.Number("count")<32)break;await UniTask.Yield(PlayerLoopTiming.Update,token);
            }
            await service.SynchronizeNativeAsync();
        }
        /// <summary>Explicitly discard this scope's baseline and source dispositions, retaining confirmed receipts and accepted tasks.</summary>
        public async UniTask ResetScopeAsync(CancellationToken token=default)
        {
            MediaThread.Check();if(running || looping)throw new InvalidOperationException("Stop the automatic loop before resetting its scope.");
            string scope=CatalogScope;await service.Db(Command.ResetScope,token,scope);
            while((await service.Db(Command.ResetScopePage,token,scope)).Single.Flag("pending"))await UniTask.Yield(PlayerLoopTiming.Update,token);
        }
        public async UniTask RunAsync(CancellationToken cancellationToken)
        {
            MediaThread.Check();if(looping) throw new InvalidOperationException("Automatic backup loop already running.");Validate(policy);looping=true;
            bool changed=true;long nextReconciliation=0;using var watch=policy.enabled?library.Watch(Scope,batch=>{if(batch.RequiresRefresh || batch.Items.Count!=0)changed=true;}):null;
            try
            {
                while(policy.enabled)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    changed=false;
                    if(!policy.wifiOnly || wifiAvailable())
                    {
                        await service.ProcessAsync(cancellationToken,service.UsesNativeBackgroundTransfer?null:policy.wifiOnly?wifiAvailable:null);
                        bool complete=System.Diagnostics.Stopwatch.GetTimestamp()>=nextReconciliation;
                        await ScanOnceAsync(cancellationToken,true,complete);
                        if(complete)nextReconciliation=System.Diagnostics.Stopwatch.GetTimestamp()+(long)policy.scanIntervalSeconds*System.Diagnostics.Stopwatch.Frequency;
                    }
                    long until=System.Diagnostics.Stopwatch.GetTimestamp()+(long)policy.scanIntervalSeconds*System.Diagnostics.Stopwatch.Frequency;
                    do
                    {
                        var failure=library.GetWatchFailure(Scope);
                        if(failure!=null)
                        {
                            if(failure is GalleryException gallery && gallery.Code=="PermissionDenied")
                                try{await SuspendScopeAsync(default);}catch(Exception cleanup){UnityEngine.Debug.LogException(cleanup);}
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                        }
                        await UniTask.Delay(100,ignoreTimeScale:true,cancellationToken:cancellationToken);
                    } while(!changed && System.Diagnostics.Stopwatch.GetTimestamp()<until && (nextReconciliation==0 || System.Diagnostics.Stopwatch.GetTimestamp()<nextReconciliation));
                }
            }
            finally { looping=false; }
        }
    }
}
