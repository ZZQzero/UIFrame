using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Media.Storage;

namespace Game.Media
{
    public enum ImageLibrarySourceKind { Directory, PhotoLibrary, GrantedDirectory }
    public enum ImageLibraryChangeKind { Added, ContentChanged, MetadataChanged, RemovedFromScope, AccessChanged }
    public enum ImageLibraryRefreshKind { Current, Incremental, CompleteReconciliation }
    public sealed class ImageLibraryScope
    {
        public ImageLibrarySourceKind Kind { get; }
        public string Source { get; }
        public bool Recursive { get; }
        public string Id { get; }
        public ImageLibraryScope(ImageLibrarySourceKind kind,string source="",bool recursive=false)
        {
            if(!Enum.IsDefined(typeof(ImageLibrarySourceKind),kind)) throw new ArgumentOutOfRangeException(nameof(kind));
            source=source??"";
            if(kind==ImageLibrarySourceKind.Directory)
            {
                if(!Path.IsPathRooted(source)) throw new ArgumentException("Absolute directory required."); source=Path.GetFullPath(source);
            }
            if(kind==ImageLibrarySourceKind.GrantedDirectory && string.IsNullOrWhiteSpace(source)) throw new ArgumentException("A granted directory bookmark is required.");
            Kind=kind;Source=source;Recursive=recursive;
            using var sha=SHA256.Create();Id=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes((int)kind+"\n"+source+"\n"+recursive))).Replace("-","").ToLowerInvariant();
        }
    }
    public sealed class ImageLibraryChange
    {
        public long Sequence { get; internal set; }
        public string SourceIdentity { get; internal set; }
        public string Version { get; internal set; }
        public ImageLibraryChangeKind Kind { get; internal set; }
        internal string Name,Mime;
    }
    public sealed class ImageLibraryPosition
    {
        public string LibraryId { get; internal set; }
        public string ScopeId { get; internal set; }
        public long IndexGeneration { get; internal set; }
        public long ScopeRevision { get; internal set; }
        public long PermissionGeneration { get; internal set; }
        public long Sequence { get; internal set; }
        public long RetainedAfter { get; internal set; }
        public bool RequiresRefresh { get; internal set; }
    }
    public sealed class ImageLibraryPage
    {
        public IReadOnlyList<ImageReference> Items { get; internal set; }
        public ImageLibraryCursor Next { get; internal set; }
        public ImageLibraryPosition Position { get; internal set; }
    }
    public sealed class ImageLibraryCursor
    {
        internal ImageLibraryPosition Position;
        internal string After;
    }
    public sealed class ImageLibraryRefresh
    {
        public ImageLibraryRefreshKind Kind { get; internal set; }
        public int Examined { get; internal set; }
        public bool HasPendingChanges { get; internal set; }
        public ImageLibraryPosition Position { get; internal set; }
    }
    public sealed class ImageLibraryChangeBatch
    {
        public IReadOnlyList<ImageLibraryChange> Items { get; internal set; }
        public ImageLibraryPosition Position { get; internal set; }
        public bool RequiresRefresh { get; internal set; }
    }

    /// <summary>Application-owned, account-independent metadata index. No image bytes are stored in SQLite.</summary>
    public sealed class ImageLibraryIndex
    {
        readonly LibraryRepository repository;
        readonly Dictionary<string,Observer> observers=new Dictionary<string,Observer>();
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        readonly string observerOwner=Guid.NewGuid().ToString("N");
        readonly SemaphoreSlim refreshGate=new SemaphoreSlim(1,1);
        bool closed,closing,driving;
        int work;
        ImageLibraryIndex(LibraryRepository repository) { this.repository=repository;UnityEngine.Application.focusChanged+=OnFocus; }
        public static async UniTask<ImageLibraryIndex> OpenAsync(string absoluteDatabasePath,CancellationToken token=default)
        {
            MediaThread.Check(); token.ThrowIfCancellationRequested();
            var repository=await UniTask.RunOnThreadPool(async()=>await LibraryRepository.OpenAsync(absoluteDatabasePath,token));
            return new ImageLibraryIndex(repository);
        }
        internal sealed class Observer : IDisposable
        {
            internal readonly object Gate=new object();
            internal readonly HashSet<string> Paths=new HashSet<string>(StringComparer.Ordinal);
            internal readonly ImageLibraryScope Scope;
            internal readonly List<WatchHandle> Subscribers=new List<WatchHandle>();
            internal int References;
            internal bool Closing;
            internal UniTask CloseTask;
            internal bool Reconcile=true,NativeAttached;
            internal string NativeId,PlatformBoundary;
            internal ImageLibraryPosition NotificationPosition;
            internal readonly Dictionary<string,MediaItem> NativeItems=new Dictionary<string,MediaItem>(StringComparer.Ordinal);
            internal long FirstSignal=System.Diagnostics.Stopwatch.GetTimestamp(),LastSignal=System.Diagnostics.Stopwatch.GetTimestamp();
            internal long Serial;
            internal FileSystemWatcher Watcher,DirectoryWatcher;
            internal Exception Failure;
            internal Observer(ImageLibraryScope scope)
            {
                Scope=scope;
                if(scope.Kind==ImageLibrarySourceKind.Directory)
                {
                    try {
                    Watcher=new FileSystemWatcher(scope.Source) { IncludeSubdirectories=scope.Recursive,NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.Size };
                    Watcher.Created+=(s,e)=>Changed(e.FullPath);
                    Watcher.Changed+=(s,e)=>Changed(e.FullPath);
                    Watcher.Deleted+=(s,e)=>Changed(e.FullPath);
                    Watcher.Renamed+=(s,e)=>{Changed(e.OldFullPath);Changed(e.FullPath);};
                    Watcher.Error+=(s,e)=>Signal(true);
                    DirectoryWatcher=new FileSystemWatcher(scope.Source) {IncludeSubdirectories=scope.Recursive,NotifyFilter=NotifyFilters.DirectoryName};
                    DirectoryWatcher.Created+=(s,e)=>Signal(true);DirectoryWatcher.Deleted+=(s,e)=>Signal(true);DirectoryWatcher.Renamed+=(s,e)=>Signal(true);DirectoryWatcher.Error+=(s,e)=>Signal(true);
                    Watcher.EnableRaisingEvents=true;DirectoryWatcher.EnableRaisingEvents=true;
                    }
                    catch {try{Dispose();}catch(Exception cleanup){UnityEngine.Debug.LogException(cleanup);}throw;}
                }
            }
            void Changed(string path)
            {
                if(!ImagePaths.Mime(Path.GetExtension(path)).StartsWith("image/",StringComparison.Ordinal))return;
                lock(Gate)
                {
                    Stamp();
                    if(Reconcile) return;
                    // Directory moves can affect arbitrarily many descendants.
                    if(Paths.Count>=1024) { Reconcile=true;Paths.Clear(); }
                    else Paths.Add(path);
                }
            }
            internal void Stamp(){long now=System.Diagnostics.Stopwatch.GetTimestamp();if(FirstSignal==0)FirstSignal=now;LastSignal=now;Serial++;}
            internal void Signal(bool full){lock(Gate){Stamp();if(full){Reconcile=true;Paths.Clear();NativeItems.Clear();}}}
            public void Dispose() { var files=Watcher;var directories=DirectoryWatcher;Watcher=DirectoryWatcher=null;var cleanup=new UIFrame.CleanupFailure();if(files!=null)cleanup.Run(files.Dispose);if(directories!=null)cleanup.Run(directories.Dispose);cleanup.Throw(); }
        }
        void Check() { MediaThread.Check();if(closed || closing) throw new ObjectDisposedException(nameof(ImageLibraryIndex)); }
        Observer AcquireObserver(ImageLibraryScope scope)
        {
            if(scope==null)throw new ArgumentNullException(nameof(scope));
            if(!observers.TryGetValue(scope.Id,out var observer))
            {
                if(observers.Count>=16)throw new InvalidOperationException("At most 16 image scopes may be observed concurrently.");
                observer=new Observer(scope) {NativeId=observerOwner+scope.Id};observers.Add(scope.Id,observer);
            }
            if(observer.Closing)throw new InvalidOperationException("Await this scope's observation close before observing it again.");
            observer.References++;return observer;
        }
        bool NeedsRefresh(string scope) => !observers.TryGetValue(scope,out var observer) || observer.Closing || observer.Reconcile;
        UniTask ReleaseObserver(Observer observer)
        {
            if(observer.Closing)return observer.CloseTask;
            return --observer.References==0?CloseObserver(observer):UniTask.CompletedTask;
        }
        UniTask CloseObserver(Observer observer)
        {
            if(observer.Closing)return observer.CloseTask;
            observer.Closing=true;observer.CloseTask=CloseObserverCore(observer).Preserve();return observer.CloseTask;
        }
        async UniTask CloseObserverCore(Observer observer)
        {
            work++;var cleanup=new UIFrame.CleanupFailure();
            try
            {
                cleanup.Run(observer.Dispose);
                if(observer.NativeAttached)
                {
                    observer.NativeAttached=false;
                    try{await NativeMedia.Request(new MediaRequest {op="unobserve",path=observer.NativeId},default);}
                    catch(Exception error){cleanup.Capture(error);}
                }
                observer.Subscribers.Clear();cleanup.Throw();observers.Remove(observer.Scope.Id);
            }
            finally {work--;}
        }
        async UniTask<long> Permission(ImageLibraryScope scope,CancellationToken token)
        {
            if(scope.Kind!=ImageLibrarySourceKind.PhotoLibrary) return 0;
            var access=await GameGallery.GetLibraryAccessAsync(token);
            if(access!=LibraryAccess.Authorized && access!=LibraryAccess.Limited)
            {
                await repository.Scope(scope,(long)access,token);
                throw new GalleryException("PermissionDenied","The configured photo library is not authorized.");
            }
            return (long)access;
        }
        async UniTask RecordAccessFailure(ImageLibraryScope scope,Exception error)
        {
            if(scope.Kind!=ImageLibrarySourceKind.PhotoLibrary || !(error is GalleryException gallery) || gallery.Code!="PermissionDenied")return;
            // The failed operation observed lost access even if permission is
            // restored before another query. Preserve that fact without retrying.
            try {await repository.RevokeAccess(scope.Id);}
            catch(Exception persistence){UnityEngine.Debug.LogException(persistence);}
        }
        // Preparation can observe access loss after a successful refresh. Record
        // that observation through the same serialized permission boundary.
        internal async UniTask RecordSourceAccessFailureAsync(ImageLibraryScope scope,GalleryException error)
        {
            if(scope.Kind!=ImageLibrarySourceKind.PhotoLibrary || error.Code!="PermissionDenied")return;
            Check();work++;bool acquired=false;
            try
            {
                await refreshGate.WaitAsync();acquired=true;
                await RecordAccessFailure(scope,error);
                if(observers.TryGetValue(scope.Id,out var observer))observer.Signal(true);
            }
            finally {if(acquired)refreshGate.Release();work--;}
        }
        public async UniTask<ImageLibraryPosition> GetPositionAsync(ImageLibraryScope scope,CancellationToken token=default)
        {
            Check();if(scope==null)throw new ArgumentNullException(nameof(scope));work++;
            try { return await GetPositionCore(scope,token); }
            finally { work--; }
        }
        async UniTask<ImageLibraryPosition> GetPositionCore(ImageLibraryScope scope,CancellationToken token)
        {
                if(scope==null)throw new ArgumentNullException(nameof(scope));
                var state=await repository.FindState(scope.Id,token)??await repository.Scope(scope,await Permission(scope,token),token);
                var position=await repository.Position(token);
                return Position(state,position.sequence,position.retained);
        }
        ImageLibraryPosition Position(LibraryScopeState scope,long sequence,long retained) => new ImageLibraryPosition {
            LibraryId=repository.Id,IndexGeneration=repository.Generation,ScopeId=scope.Scope,ScopeRevision=scope.Revision,
            PermissionGeneration=scope.Permission,Sequence=sequence,RetainedAfter=retained,RequiresRefresh=scope.RequiresReconcile || NeedsRefresh(scope.Scope) };
        static bool SameScope(ImageLibraryPosition a,ImageLibraryPosition b) => a.LibraryId==b.LibraryId && a.IndexGeneration==b.IndexGeneration && a.ScopeId==b.ScopeId && a.ScopeRevision==b.ScopeRevision && a.PermissionGeneration==b.PermissionGeneration;
        public async UniTask<ImageLibraryPage> QueryAsync(ImageLibraryScope scope,int pageSize=100,ImageLibraryCursor cursor=null,CancellationToken token=default)
        {
            Check();if(pageSize<1 || pageSize>200) throw new ArgumentOutOfRangeException(nameof(pageSize));work++;
            try
            {
                var position=await GetPositionCore(scope,token);
                if(cursor!=null && !SameScope(cursor.Position,position)) throw new InvalidOperationException("Image library cursor requires a refresh.");
                var rows=await repository.Page(scope.Id,cursor?.After??"",pageSize,token);
                return new ImageLibraryPage { Items=rows,Position=position,Next=rows.Count==pageSize?new ImageLibraryCursor { Position=position,After=rows[rows.Count-1].Source+":"+rows[rows.Count-1].OriginId }:null };
            }
            finally { work--; }
        }
        public async UniTask<ImageLibraryChangeBatch> ReadChangesAsync(ImageLibraryScope scope,ImageLibraryPosition after,int pageSize=100,CancellationToken token=default)
        {
            Check();if(after==null) throw new ArgumentNullException(nameof(after));if(pageSize<1 || pageSize>200) throw new ArgumentOutOfRangeException(nameof(pageSize));work++;
            try { return await ReadChangesCore(scope,after,pageSize,token); }
            finally { work--; }
        }
        async UniTask<ImageLibraryChangeBatch> ReadChangesCore(ImageLibraryScope scope,ImageLibraryPosition after,int pageSize,CancellationToken token)
        {
                if(scope==null)throw new ArgumentNullException(nameof(scope));
                var batch=await repository.Changes(scope.Id,after.Sequence,pageSize,token);
                if(NeedsRefresh(scope.Id) || !SameScope(after,batch.Position) || after.Sequence<batch.Position.RetainedAfter || after.Sequence>batch.Position.Sequence || batch.Position.RequiresRefresh)
                    return new ImageLibraryChangeBatch {Items=Array.Empty<ImageLibraryChange>(),Position=batch.Position,RequiresRefresh=true};
                return batch;
        }

        public async UniTask<ImageLibraryRefresh> RefreshAsync(ImageLibraryScope scope,CancellationToken token=default,bool completeReconciliation=false)
        {
            Check();token.ThrowIfCancellationRequested();var observer=AcquireObserver(scope);work++;
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);token=linked.Token;
            bool acquired=false,updating=false;Exception primary=null;
            try
            {
                await refreshGate.WaitAsync(token);acquired=true;updating=true;
                var state=await repository.Scope(scope,await Permission(scope,token),token);
                await PollNative(observer,token,true);
                var start=await repository.Position(token);
                observer.NotificationPosition??=Position(state,start.sequence,start.retained);
                bool complete,verifyContents;long serial;string[] paths;MediaItem[] changes;
                lock(observer.Gate)
                {
                    verifyContents=observer.Reconcile || state.RequiresReconcile;
                    complete=completeReconciliation || observer.Reconcile || state.RequiresReconcile || scope.Kind==ImageLibrarySourceKind.GrantedDirectory;
                    serial=observer.Serial;paths=observer.Paths.ToArray();changes=observer.NativeItems.Values.ToArray();observer.Paths.Clear();observer.NativeItems.Clear();observer.Reconcile=false;observer.FirstSignal=observer.LastSignal=0;
                }
                int examined=0;
                if(complete)
                {
                    string boundary=observer.PlatformBoundary;
                    string run=await repository.Begin(state,start.sequence,token,boundary);
                    async UniTask<bool> Consume(IReadOnlyList<ImageReference> page)
                    {
                        for(int offset=0;offset<page.Count;offset+=32)
                        {
                            var part=page.Skip(offset).Take(32).ToArray();
                            if(scope.Kind==ImageLibrarySourceKind.Directory)part=await ResolveFiles(part,verifyContents,paths,token);
                            await repository.Upsert(state,run,part,token);examined+=part.Length;
                        }
                        return true;
                    }
                    bool finished=scope.Kind==ImageLibrarySourceKind.Directory?await GameImageDirectory.VisitAsync(scope.Source,Consume,scope.Recursive,token):
                        scope.Kind==ImageLibrarySourceKind.PhotoLibrary?await GameGallery.VisitObservedImagesAsync(Consume,string.IsNullOrEmpty(scope.Source)?null:scope.Source,observer.NativeId,token):
                        await ImageDirectoryHandle.FromBookmark(scope.Source).VisitAsync(Consume,scope.Recursive,token);
                    if(!finished) throw new InvalidOperationException("Image library enumeration did not complete.");
                    long access=await Permission(scope,token);
                    if(access!=state.Access) {
                        await repository.Scope(scope,access,token);
                        throw new GalleryException("ScopeConfirmationRequired","Photo access changed during reconciliation.");
                    }
                    await PollNative(observer,token,true);
                    if((await repository.State(scope.Id,token)).Permission!=state.Permission)
                        throw new GalleryException("ScopeConfirmationRequired","Photo access changed during reconciliation.");
                    if(scope.Kind==ImageLibrarySourceKind.PhotoLibrary && boundary!=observer.PlatformBoundary)
                        throw new GalleryException("LibraryChangedDuringScan","Photo library changed during reconciliation; request a new scan.");
                    await repository.Finish(state,run,start.sequence,token);
                }
                else if(scope.Kind==ImageLibrarySourceKind.PhotoLibrary)
                {
                    foreach(var change in changes)
                    {
                        token.ThrowIfCancellationRequested();examined++;
                        if(change.kind>=3)await repository.Remove(state,"library:"+change.id,state.Access==(long)LibraryAccess.Limited?4:3,token);
                        else await repository.Upsert(state,null,new[]{new ImageReference("library",change.id,change.name,change.mime,change.size,change.width,change.height,change.version)},token);
                    }
                }
                else foreach(string path in paths)
                {
                    token.ThrowIfCancellationRequested();examined++;
                    if(File.Exists(path)) await repository.Upsert(state,null,await ResolveFiles(new[]{ImageReference.FromFile(path)},true,paths,token),token);
                    else await repository.Remove(state,"file:"+path,3,token);
                }
                updating=false;
                token.ThrowIfCancellationRequested();var position=await GetPositionCore(scope,token);bool pending;observer.Failure=null;
                lock(observer.Gate) pending=observer.Serial!=serial || observer.Reconcile || observer.Paths.Count!=0 || observer.NativeItems.Count!=0;
                // A failing subscriber ends this dispatch only. The next refresh
                // is a new operation and never replays the failed change batch.
                if(observer.Subscribers.Count!=0)
                {
                    var cursor=observer.NotificationPosition;
                    for(;;)
                    {
                        var batch=await ReadChangesCore(scope,cursor,200,token);
                        // An attempted notification batch is not replayed. Changes
                        // committed before an earlier scan failed remain pending.
                        observer.NotificationPosition=batch.Position;
                        foreach(var subscriber in observer.Subscribers.ToArray())if(!subscriber.IsClosed)subscriber.Callback(batch);
                        if(batch.RequiresRefresh || batch.Position.Sequence>=position.Sequence) break;cursor=batch.Position;
                    }
                }
                return new ImageLibraryRefresh { Kind=complete?ImageLibraryRefreshKind.CompleteReconciliation:examined==0?ImageLibraryRefreshKind.Current:ImageLibraryRefreshKind.Incremental,
                    Examined=examined,HasPendingChanges=pending,Position=position };
            }
            catch(Exception error)
            {
                primary=error;
                if(updating){observer.Signal(true);await RecordAccessFailure(scope,error);}
                throw;
            }
            finally
            {
                if(acquired)refreshGate.Release();
                try{await ReleaseObserver(observer);}
                catch(Exception cleanup){if(primary==null)throw;UnityEngine.Debug.LogException(cleanup);}
                finally {work--;}
            }
        }
        async UniTask<ImageReference[]> ResolveFiles(ImageReference[] images,bool verify,string[] changed,CancellationToken token)
        {
            var previous=(await repository.FileVersions(images,token)).ToDictionary(r=>r.source,r=>r.version,StringComparer.Ordinal);
            return await UniTask.RunOnThreadPool(()=>images.Select(image=> {
                token.ThrowIfCancellationRequested();
                if(!verify && Array.IndexOf(changed,image.Id)<0 && previous.TryGetValue("file:"+image.Id,out var version) && FileImageVersion.MatchesMetadata(image,version))
                    return FileImageVersion.WithVersion(image,version);
                return FileImageVersion.Read(image,token);
            }).ToArray());
        }
        public WatchHandle Watch(ImageLibraryScope scope,Action<ImageLibraryChangeBatch> onChanges)
        {
            Check();if(onChanges==null)throw new ArgumentNullException(nameof(onChanges));
            if(scope==null)throw new ArgumentNullException(nameof(scope));
            if(observers.TryGetValue(scope.Id,out var existing) && existing.Subscribers.Count>=32)
                throw new InvalidOperationException("At most 32 subscriptions per scope are supported.");
            var observer=AcquireObserver(scope);var handle=new WatchHandle(this,observer,onChanges);
            observer.Subscribers.Add(handle);StartDriver();return handle;
        }
        public sealed class WatchHandle
        {
            readonly ImageLibraryIndex index;
            readonly Observer observer;
            internal readonly Action<ImageLibraryChangeBatch> Callback;
            internal bool IsClosed {get;private set;}
            UniTask close;
            internal WatchHandle(ImageLibraryIndex index,Observer observer,Action<ImageLibraryChangeBatch> callback)
            {this.index=index;this.observer=observer;Callback=callback;}
            void Check(){index.Check();if(IsClosed)throw new ObjectDisposedException(nameof(WatchHandle));}
            public Exception Failure {get{Check();return observer.Failure;}}
            /// <summary>Resume notifications without replaying a failed batch or forcing a full scan.</summary>
            public void Resume(){Check();observer.Failure=null;index.StartDriver();}
            public void RequestRefresh(){Check();observer.Signal(true);Resume();}
            public UniTask CloseAsync()
            {
                MediaThread.Check();if(IsClosed)return close;IsClosed=true;observer.Subscribers.Remove(this);
                close=CloseCore().Preserve();return close;
            }
            async UniTask CloseCore()
            {
                await index.ReleaseObserver(observer);
                // The driver or an admitted refresh may still hold the last
                // temporary lease. Its release owns native unregistration.
                while(!observer.Closing && observer.Subscribers.Count==0 && observer.References!=0)await UniTask.Yield();
                if(observer.Closing)await observer.CloseTask;
            }
        }
        async UniTask PollNative(Observer observer,CancellationToken token,bool verifyBoundary=false)
        {
            if(observer.Scope.Kind!=ImageLibrarySourceKind.PhotoLibrary)return;
            if(!observer.NativeAttached)
            {
                try {await NativeMedia.Request(new MediaRequest {op="observe",path=observer.NativeId,album=observer.Scope.Source},token);observer.NativeAttached=true;}
                catch
                {
                    observer.NativeAttached=false;
                    try {await NativeMedia.Request(new MediaRequest {op="unobserve",path=observer.NativeId},default);}catch(Exception cleanup){UnityEngine.Debug.LogException(cleanup);}throw;
                }
            }
            for(;;)
            {
                var response=await NativeMedia.Request(new MediaRequest {op="drain",path=observer.NativeId,verifyBoundary=verifyBoundary},token);
                observer.PlatformBoundary=response.boundary;
                lock(observer.Gate)
                {
                    if(response.requiresReconcile || response.accessChanged)observer.Signal(true);
                    foreach(var item in response.items??Array.Empty<MediaItem>())
                    {
                        observer.Stamp();if(observer.Reconcile)continue;
                        if(observer.NativeItems.Count>=1024){observer.Signal(true);break;}
                        observer.NativeItems[item.id]=item;
                    }
                }
                if(!response.hasNext)break;
            }
        }
        void OnFocus(bool focused)
        {if(!focused || closing || closed)return;foreach(var observer in observers.Values)observer.Signal(true);StartDriver();}
        void StartDriver()
        {if(driving || closing || closed)return;Drive().Forget(error=>UnityEngine.Debug.LogException(error));}
        async UniTask Drive()
        {
            driving=true;
            try
            {
                while(observers.Values.Any(o=>o.Subscribers.Count!=0 && o.Failure==null))
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    foreach(var observer in observers.Values.ToArray())
                    {
                        if(observer.Closing || observer.Subscribers.Count==0 || observer.Failure!=null)continue;
                        observer.References++;
                        try
                        {
                            if(observer.Scope.Kind==ImageLibrarySourceKind.PhotoLibrary)
                            {
                                await refreshGate.WaitAsync(lifetime.Token);
                                try {await PollNative(observer,lifetime.Token);}
                                catch(Exception error){await RecordAccessFailure(observer.Scope,error);throw;}
                                finally {refreshGate.Release();}
                            }
                            bool due;lock(observer.Gate)
                            {
                                long now=System.Diagnostics.Stopwatch.GetTimestamp(),frequency=System.Diagnostics.Stopwatch.Frequency;
                                due=observer.FirstSignal!=0 && (now-observer.LastSignal>=frequency*3/10 || now-observer.FirstSignal>=frequency*2);
                            }
                            if(due)await RefreshAsync(observer.Scope,lifetime.Token);
                        }
                        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){throw;}
                        catch(Exception error){observer.Failure=error;UnityEngine.Debug.LogException(error);}
                        finally
                        {
                            try{await ReleaseObserver(observer);}
                            catch(Exception cleanup){observer.Failure=observer.Failure??cleanup;UnityEngine.Debug.LogException(cleanup);}
                        }
                    }
                    await UniTask.Delay(100,ignoreTimeScale:true,cancellationToken:lifetime.Token);
                }
            }
            catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
            finally {driving=false;}
        }
        public async UniTask PruneChangesAsync(long throughSequence,CancellationToken token=default)
        {Check();if(throughSequence<0)throw new ArgumentOutOfRangeException(nameof(throughSequence));work++;try{await repository.PruneChanges(throughSequence,token);}finally{work--;}}
        public async UniTask ShutdownAsync()
        {
            MediaThread.Check();if(closed)return;if(closing)throw new InvalidOperationException("Library shutdown is already active.");closing=true;
            var cleanup=new UIFrame.CleanupFailure();UnityEngine.Application.focusChanged-=OnFocus;cleanup.Run(lifetime.Cancel);
            while(work!=0 || driving)await UniTask.Yield();
            foreach(var observer in observers.Values.ToArray())
                try{await CloseObserver(observer);}catch(Exception error){cleanup.Capture(error);}
            observers.Clear();
            try {await repository.CloseAsync();}catch(Exception error){cleanup.Capture(error);}
            cleanup.Run(lifetime.Dispose);cleanup.Run(refreshGate.Dispose);closed=true;cleanup.Throw();
        }
    }
}
