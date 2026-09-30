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
        bool refreshing,closed,closing,driving;
        int work;
        ImageLibraryIndex(LibraryRepository repository) { this.repository=repository;UnityEngine.Application.focusChanged+=OnFocus; }
        public static async UniTask<ImageLibraryIndex> OpenAsync(string absoluteDatabasePath,CancellationToken token=default)
        {
            MediaThread.Check(); token.ThrowIfCancellationRequested();
            var repository=await UniTask.RunOnThreadPool(async()=>await LibraryRepository.OpenAsync(absoluteDatabasePath,token));
            return new ImageLibraryIndex(repository);
        }
        sealed class Observer : IDisposable
        {
            internal readonly object Gate=new object();
            internal readonly HashSet<string> Paths=new HashSet<string>(StringComparer.Ordinal);
            internal readonly ImageLibraryScope Scope;
            internal readonly List<Action<ImageLibraryChangeBatch>> Subscribers=new List<Action<ImageLibraryChangeBatch>>();
            internal bool Reconcile=true,Initialized,NativeAttached;
            internal string NativeId;
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
                    Watcher=new FileSystemWatcher(scope.Source) { IncludeSubdirectories=scope.Recursive,NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite|NotifyFilters.Size };
                    Watcher.Created+=(s,e)=>Changed(e.FullPath);
                    Watcher.Changed+=(s,e)=>Changed(e.FullPath);
                    Watcher.Deleted+=(s,e)=>Changed(e.FullPath);
                    Watcher.Renamed+=(s,e)=>{Changed(e.OldFullPath);Changed(e.FullPath);};
                    Watcher.Error+=(s,e)=>Signal(true);
                    DirectoryWatcher=new FileSystemWatcher(scope.Source) {IncludeSubdirectories=scope.Recursive,NotifyFilter=NotifyFilters.DirectoryName};
                    DirectoryWatcher.Created+=(s,e)=>Signal(true);DirectoryWatcher.Deleted+=(s,e)=>Signal(true);DirectoryWatcher.Renamed+=(s,e)=>Signal(true);DirectoryWatcher.Error+=(s,e)=>Signal(true);
                    try {Watcher.EnableRaisingEvents=true;DirectoryWatcher.EnableRaisingEvents=true;}
                    catch {Watcher.Dispose();DirectoryWatcher.Dispose();throw;}
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
        Observer Observe(ImageLibraryScope scope)
        {
            if(scope==null) throw new ArgumentNullException(nameof(scope));
            if(observers.TryGetValue(scope.Id,out var value)) return value;
            if(observers.Count>=16) throw new InvalidOperationException("An image library supports at most 16 observed scopes per lifetime.");
            var observer=new Observer(scope) {NativeId=observerOwner+scope.Id}; observers.Add(scope.Id,observer); return observer;
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
        public async UniTask<ImageLibraryPosition> GetPositionAsync(ImageLibraryScope scope,CancellationToken token=default)
        {
            Check(); var observer=Observe(scope);work++;
            try { return await GetPositionCore(scope,observer,token); }
            finally { work--; }
        }
        async UniTask<ImageLibraryPosition> GetPositionCore(ImageLibraryScope scope,Observer observer,CancellationToken token)
        {
                LibraryScopeState state;
                if(!observer.Initialized) { state=await repository.Scope(scope,await Permission(scope,token),token);observer.Initialized=true; }
                else state=await repository.State(scope.Id,token);
                var position=await repository.Position(token);
                return Position(state,position.sequence,position.retained);
        }
        ImageLibraryPosition Position(LibraryScopeState scope,long sequence,long retained) => new ImageLibraryPosition {
            LibraryId=repository.Id,IndexGeneration=repository.Generation,ScopeId=scope.Scope,ScopeRevision=scope.Revision,
            PermissionGeneration=scope.Permission,Sequence=sequence,RetainedAfter=retained,RequiresRefresh=scope.RequiresReconcile };
        static bool SameScope(ImageLibraryPosition a,ImageLibraryPosition b) => a.LibraryId==b.LibraryId && a.IndexGeneration==b.IndexGeneration && a.ScopeId==b.ScopeId && a.ScopeRevision==b.ScopeRevision && a.PermissionGeneration==b.PermissionGeneration;
        public async UniTask<ImageLibraryPage> QueryAsync(ImageLibraryScope scope,int pageSize=100,ImageLibraryCursor cursor=null,CancellationToken token=default)
        {
            Check();if(pageSize<1 || pageSize>200) throw new ArgumentOutOfRangeException(nameof(pageSize));work++;
            try
            {
                var position=await GetPositionCore(scope,Observe(scope),token);
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
                var position=await GetPositionCore(scope,Observe(scope),token);
                if(!SameScope(after,position) || after.Sequence<position.RetainedAfter)
                    return new ImageLibraryChangeBatch { Items=Array.Empty<ImageLibraryChange>(),Position=position,RequiresRefresh=true };
                long through=position.Sequence;
                var rows=await repository.Changes(scope.Id,after.Sequence,through,pageSize,token);
                if(rows.Count==pageSize) position.Sequence=rows[rows.Count-1].Sequence;
                return new ImageLibraryChangeBatch { Items=rows,Position=position };
        }
        public async UniTask<ImageLibraryRefresh> RefreshAsync(ImageLibraryScope scope,CancellationToken token=default,bool completeReconciliation=false)
        {
            Check();var observer=Observe(scope);work++;
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(token,lifetime.Token);token=linked.Token;
            bool acquired=false;
            try
            {
                await refreshGate.WaitAsync(token);acquired=true;refreshing=true;
                var state=await repository.Scope(scope,await Permission(scope,token),token); var start=await repository.Position(token);
                await PollNative(observer,token);
                bool complete;long serial;string[] paths;MediaItem[] changes;
                lock(observer.Gate)
                {
                    complete=completeReconciliation || observer.Reconcile || state.RequiresReconcile || scope.Kind==ImageLibrarySourceKind.GrantedDirectory;
                    serial=observer.Serial;paths=observer.Paths.ToArray();changes=observer.NativeItems.Values.ToArray();observer.Paths.Clear();observer.NativeItems.Clear();observer.Reconcile=false;observer.FirstSignal=observer.LastSignal=0;
                }
                int examined=0;
                if(complete)
                {
                    string run=await repository.Begin(state,start.sequence,token);
                    async UniTask<bool> Consume(IReadOnlyList<ImageReference> page)
                    {
                        for(int offset=0;offset<page.Count;offset+=32)
                        {
                            var part=page.Skip(offset).Take(32).ToArray();await repository.Upsert(state,run,part,token);examined+=part.Length;
                        }
                        return true;
                    }
                    bool finished=scope.Kind==ImageLibrarySourceKind.Directory?await GameImageDirectory.VisitAsync(scope.Source,Consume,scope.Recursive,token):
                        scope.Kind==ImageLibrarySourceKind.PhotoLibrary?await GameGallery.VisitImagesAsync(Consume,string.IsNullOrEmpty(scope.Source)?null:scope.Source,token):
                        await ImageDirectoryHandle.FromBookmark(scope.Source).VisitAsync(Consume,scope.Recursive,token);
                    if(!finished) throw new InvalidOperationException("Image library enumeration did not complete.");
                    if(await Permission(scope,token)!=state.Access) throw new InvalidOperationException("Photo permission changed during reconciliation.");
                    await repository.Finish(state,run,start.sequence,token);
                }
                else if(scope.Kind==ImageLibrarySourceKind.PhotoLibrary)
                {
                    foreach(var change in changes)
                    {
                        token.ThrowIfCancellationRequested();examined++;
                        if(change.kind>=3)await repository.Remove(state,"library:"+change.id,3,token);
                        else await repository.Upsert(state,null,new[]{new ImageReference("library",change.id,change.name,change.mime,change.size,change.width,change.height,change.version)},token);
                    }
                }
                else foreach(string path in paths)
                {
                    token.ThrowIfCancellationRequested();examined++;
                    if(File.Exists(path)) await repository.Upsert(state,null,new[]{ImageReference.FromFile(path)},token);
                    else await repository.Remove(state,"file:"+path,3,token);
                }
                token.ThrowIfCancellationRequested();var position=await GetPositionCore(scope,observer,token);bool pending;observer.Failure=null;
                lock(observer.Gate) pending=observer.Serial!=serial || observer.Reconcile || observer.Paths.Count!=0 || observer.NativeItems.Count!=0;
                // A failing subscriber ends this dispatch only. The next refresh
                // is a new operation and never replays the failed change batch.
                if(observer.Subscribers.Count!=0)
                {
                    var cursor=Position(state,start.sequence,start.retained);
                    for(;;)
                    {
                        var batch=await ReadChangesCore(scope,cursor,200,token);
                        foreach(var subscriber in observer.Subscribers.ToArray()) subscriber(batch);
                        if(batch.RequiresRefresh || batch.Position.Sequence>=position.Sequence) break;cursor=batch.Position;
                    }
                }
                return new ImageLibraryRefresh { Kind=complete?ImageLibraryRefreshKind.CompleteReconciliation:examined==0?ImageLibraryRefreshKind.Current:ImageLibraryRefreshKind.Incremental,
                    Examined=examined,HasPendingChanges=pending,Position=position };
            }
            catch { observer.Signal(true);throw; }
            finally { if(acquired){refreshing=false;refreshGate.Release();}work--; }
        }
        public IDisposable Watch(ImageLibraryScope scope,Action<ImageLibraryChangeBatch> onChanges)
        {
            Check();if(onChanges==null) throw new ArgumentNullException(nameof(onChanges));var observer=Observe(scope);
            if(observer.Subscribers.Count>=32) throw new InvalidOperationException("At most 32 subscriptions per scope are supported.");
            observer.Subscribers.Add(onChanges);StartDriver();return new Subscription(observer,onChanges);
        }
        sealed class Subscription : IDisposable
        {
            Observer owner;readonly Action<ImageLibraryChangeBatch> callback;
            internal Subscription(Observer owner,Action<ImageLibraryChangeBatch> callback) { this.owner=owner;this.callback=callback; }
            public void Dispose(){MediaThread.Check();var previous=owner;owner=null;previous?.Subscribers.Remove(callback);}
        }
        async UniTask PollNative(Observer observer,CancellationToken token)
        {
            if(observer.Scope.Kind!=ImageLibrarySourceKind.PhotoLibrary)return;
            if(!observer.NativeAttached)
            {
                observer.NativeAttached=true;
                try {await NativeMedia.Request(new MediaRequest {op="observe",path=observer.NativeId,album=observer.Scope.Source},token);}
                catch
                {
                    observer.NativeAttached=false;
                    try {await NativeMedia.Request(new MediaRequest {op="unobserve",path=observer.NativeId},default);}catch(Exception cleanup){UnityEngine.Debug.LogException(cleanup);}throw;
                }
            }
            for(;;)
            {
                var response=await NativeMedia.Request(new MediaRequest {op="drain",path=observer.NativeId},token);
                lock(observer.Gate)
                {
                    if(response.requiresReconcile)observer.Signal(true);
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
                        if(observer.Subscribers.Count==0 || observer.Failure!=null)continue;
                        try
                        {
                            if(!refreshing)await PollNative(observer,lifetime.Token);
                            bool due;lock(observer.Gate)
                            {
                                long now=System.Diagnostics.Stopwatch.GetTimestamp(),frequency=System.Diagnostics.Stopwatch.Frequency;
                                due=observer.FirstSignal!=0 && (now-observer.LastSignal>=frequency*3/10 || now-observer.FirstSignal>=frequency*2);
                            }
                            if(due && !refreshing)await RefreshAsync(observer.Scope,lifetime.Token);
                        }
                        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){throw;}
                        catch(Exception error){observer.Failure=error;UnityEngine.Debug.LogException(error);}
                    }
                    await UniTask.Delay(100,ignoreTimeScale:true,cancellationToken:lifetime.Token);
                }
            }
            catch(OperationCanceledException) when(lifetime.IsCancellationRequested) { }
            finally {driving=false;}
        }
        public Exception GetWatchFailure(ImageLibraryScope scope){Check();return Observe(scope).Failure;}
        public void RequestRefresh(ImageLibraryScope scope)
        {Check();var observer=Observe(scope);observer.Failure=null;observer.Signal(true);StartDriver();}
        public async UniTask PruneChangesAsync(long throughSequence,CancellationToken token=default)
        {Check();if(throughSequence<0)throw new ArgumentOutOfRangeException(nameof(throughSequence));work++;try{await repository.PruneChanges(throughSequence,token);}finally{work--;}}
        public async UniTask ShutdownAsync()
        {
            MediaThread.Check();if(closed)return;if(closing)throw new InvalidOperationException("Library shutdown is already active.");closing=true;
            var cleanup=new UIFrame.CleanupFailure();UnityEngine.Application.focusChanged-=OnFocus;cleanup.Run(lifetime.Cancel);
            while(work!=0 || driving)await UniTask.Yield();
            foreach(var observer in observers.Values)
            {
                cleanup.Run(observer.Dispose);
                if(observer.NativeAttached)try{await NativeMedia.Request(new MediaRequest {op="unobserve",path=observer.NativeId},default);}catch(Exception error){cleanup.Capture(error);}
                observer.Subscribers.Clear();
            }
            observers.Clear();
            try {await repository.CloseAsync();}catch(Exception error){cleanup.Capture(error);}
            cleanup.Run(lifetime.Dispose);cleanup.Run(refreshGate.Dispose);closed=true;cleanup.Throw();
        }
    }
}
