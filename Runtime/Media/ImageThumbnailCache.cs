using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Game.Media
{
    public readonly struct ImageThumbnailCacheStatistics
    {
        public readonly long IdleBytes,InUseBytes,PendingDestroyBytes,EstimatedInFlightDecodeBytes;
        public readonly int Entries,PendingKeys,Waiters,RequestMetadataBytes;
        internal ImageThumbnailCacheStatistics(long idle,long used,int entries)
        {IdleBytes=idle;InUseBytes=used;PendingDestroyBytes=ImageTexture.PendingDestroyBytes;EstimatedInFlightDecodeBytes=GameImageReader.EstimatedInFlightDecodeBytes;Entries=entries;PendingKeys=ImageWorkBudget.Keys;Waiters=ImageWorkBudget.Waiters;RequestMetadataBytes=ImageWorkBudget.MetadataBytes;}
    }
    /// <summary>Application-owned memory cache. Each caller owns an independent texture lease.</summary>
    public sealed class ImageThumbnailCache
    {
        readonly struct Key:IEquatable<Key>
        {
            internal readonly string Source,Identity,Version;
            readonly int edge,pixels;readonly bool readable;
            internal Key(ImageReference image,ImagePreviewOptions options){Source=image.Source;Identity=image.OriginId;Version=image.Version;edge=options.MaxEdge;pixels=options.MaxPixels;readable=options.Readable;}
            public bool Equals(Key value)=>Source==value.Source && Identity==value.Identity && Version==value.Version && edge==value.edge && pixels==value.pixels && readable==value.readable;
            public override bool Equals(object value)=>value is Key key && Equals(key);
            public override int GetHashCode(){unchecked{return (((((Source.GetHashCode()*397)^Identity.GetHashCode())*397^Version.GetHashCode())*397^edge)*397^pixels)*397^(readable?1:0);}}
        }
        sealed class Entry
        {
            internal Key Key;
            internal ImageReference Source;
            internal ImagePreviewOptions Options;
            internal IDisposable SourceLease,Admission;
            internal CancellationTokenSource Cancellation;
            internal readonly List<Waiter> Waiters=new List<Waiter>();
            internal ImageTexture Owner;
            internal LinkedListNode<Entry> Idle;
            internal int Clients;
            internal bool Reusable=true,Delivering;
        }
        sealed class Waiter
        {
            internal Entry Entry;
            internal UniTaskCompletionSource<ImageTexture> Completion=new UniTaskCompletionSource<ImageTexture>();
            internal CancellationTokenRegistration Cancellation;
            internal IDisposable Admission;
            internal CancellationToken Token;
            internal bool Finished;
        }
        readonly Dictionary<Key,Entry> entries=new Dictionary<Key,Entry>();
        readonly LinkedList<Entry> idle=new LinkedList<Entry>();
        readonly long budget;
        readonly Func<ImageReference,ImagePreviewOptions,CancellationToken,UniTask<ImageTexture>> loader;
        long idleBytes,inUseBytes;
        int loading,pendingDisposals;
        bool closed;
        Exception shutdownFailure;
        public ImageThumbnailCache(long idleBudgetBytes=32L*1024*1024):this(idleBudgetBytes,GameImageReader.LoadAdmittedPreviewAsync){}
        internal ImageThumbnailCache(long bytes,Func<ImageReference,ImagePreviewOptions,CancellationToken,UniTask<ImageTexture>> loader)
        {MediaThread.Check();if(bytes<0)throw new ArgumentOutOfRangeException(nameof(bytes));budget=bytes;this.loader=loader??throw new ArgumentNullException(nameof(loader));}
        public ImageThumbnailCacheStatistics Statistics {get{MediaThread.Check();return new ImageThumbnailCacheStatistics(idleBytes,inUseBytes,entries.Count);}}
        public UniTask<ImageTexture> AcquireAsync(ImageReference image,ImagePreviewOptions options=null,CancellationToken cancellationToken=default)
        {
            MediaThread.Check();if(closed)throw new ObjectDisposedException(nameof(ImageThumbnailCache));if(image==null)throw new ArgumentNullException(nameof(image));
            options=options??new ImagePreviewOptions {MaxEdge=256};options.Validate();cancellationToken.ThrowIfCancellationRequested();
            image.Owner?.CheckAvailable();
            if(string.IsNullOrEmpty(image.Version))throw new ArgumentException("A content version is required for shared thumbnails.",nameof(image));
            var key=new Key(image,options);
            if(entries.TryGetValue(key,out var entry) && entry.Owner!=null)return UniTask.FromResult(Lease(entry));
            IDisposable reservation=ImageWorkBudget.Acquire(image,false,true),keyReservation=null;bool fresh=entry==null;
            try
            {
                if(fresh)
                {
                    keyReservation=ImageWorkBudget.Acquire(image,true,false);
                    entry=new Entry {Key=key,Source=image,Options=new ImagePreviewOptions {MaxEdge=options.MaxEdge,MaxPixels=options.MaxPixels,Readable=options.Readable},Cancellation=new CancellationTokenSource()};
                    entry.SourceLease=image.Acquire();entries.Add(key,entry);
                    // Request identity stays reserved until the underlying load ends,
                    // even after all waiters cancel and its reusable mapping is removed.
                    entry.Admission=keyReservation;keyReservation=null;
                }
                var waiter=new Waiter {Entry=entry,Token=cancellationToken,Admission=reservation};reservation=null;
                entry.Waiters.Add(waiter);
                if(cancellationToken.CanBeCanceled)waiter.Cancellation=cancellationToken.Register(()=>UniTask.Post(()=>Cancel(waiter)));
                var task=waiter.Completion.Task;
                if(fresh){loading++;Load(entry).Forget(error=>Debug.LogException(error));}
                return task;
            }
            catch(Exception error)
            {
                var cleanup=new UIFrame.CleanupFailure();cleanup.Capture(error);
                if(reservation!=null)cleanup.Run(reservation.Dispose);if(keyReservation!=null)cleanup.Run(keyReservation.Dispose);
                if(fresh && entry!=null){Remove(entry);if(entry.SourceLease!=null)cleanup.Run(entry.SourceLease.Dispose);if(entry.Admission!=null)cleanup.Run(entry.Admission.Dispose);if(entry.Cancellation!=null)cleanup.Run(entry.Cancellation.Dispose);}
                cleanup.Throw();throw;
            }
        }
        ImageTexture Lease(Entry entry)
        {
            var lease=entry.Owner.Retain(()=>Release(entry));
            if(entry.Clients==0)
            {
                if(entry.Idle!=null){idle.Remove(entry.Idle);entry.Idle=null;idleBytes-=entry.Owner.ByteCount;}
                inUseBytes+=entry.Owner.ByteCount;
            }
            entry.Clients++;return lease;
        }
        void Release(Entry entry)
        {
            if(--entry.Clients!=0)return;inUseBytes-=entry.Owner.ByteCount;
            if(entry.Delivering)return;
            ReturnIdle(entry);
        }
        void ReturnIdle(Entry entry)
        {
            if(!closed && entry.Reusable && entry.Owner.ByteCount<=budget)
            {
                entry.Idle=idle.AddLast(entry);idleBytes+=entry.Owner.ByteCount;Trim();
            }
            else Evict(entry);
        }
        void Remove(Entry entry)
        {
            entry.Reusable=false;
            if(entries.TryGetValue(entry.Key,out var current) && ReferenceEquals(entry,current))entries.Remove(entry.Key);
        }
        void Evict(Entry entry)
        {
            Remove(entry);if(entry.Idle!=null){idle.Remove(entry.Idle);entry.Idle=null;idleBytes-=entry.Owner.ByteCount;}
            if(entry.Clients!=0)return;
            var owner=entry.Owner;entry.Owner=null;if(owner==null)return;
            DisposeOwner(owner);
        }
        void DisposeOwner(ImageTexture owner)
        {
            if(owner==null)return;
            try {owner.Dispose();}
            finally {if(Application.isPlaying){pendingDisposals++;AwaitDisposal().Forget(Debug.LogException);}}
        }
        async UniTask AwaitDisposal(){await UniTask.NextFrame();pendingDisposals--;}
        void Trim(){while(idleBytes>budget || entries.Count>128 && idle.Count!=0)Evict(idle.First.Value);}
        void FinishWaiter(Waiter waiter,ImageTexture value,Exception error,bool cancel)
        {
            if(waiter.Finished){value?.Dispose();return;}waiter.Finished=true;waiter.Entry.Waiters.Remove(waiter);
            var cleanup=new UIFrame.CleanupFailure();if(error!=null)cleanup.Capture(error);cleanup.Run(waiter.Cancellation.Dispose);if(waiter.Admission!=null)cleanup.Run(waiter.Admission.Dispose);
            try {cleanup.Throw();if(cancel)waiter.Completion.TrySetCanceled(waiter.Token);else waiter.Completion.TrySetResult(value);}
            catch(Exception failure){try{value?.Dispose();}catch(Exception secondary){Debug.LogException(secondary);}waiter.Completion.TrySetException(failure);}
        }
        void Cancel(Waiter waiter)
        {
            if(waiter.Finished)return;var entry=waiter.Entry;FinishWaiter(waiter,null,null,true);
            if(entry.Waiters.Count==0 && entry.Owner==null){Remove(entry);entry.Cancellation.Cancel();}
        }
        async UniTask Load(Entry entry)
        {
            ImageTexture loaded=null;Exception failure=null;
            try {loaded=await loader(entry.Source,entry.Options,entry.Cancellation.Token);if(loaded==null)throw new InvalidOperationException("Thumbnail loader returned no resource.");}
            catch(Exception error){failure=error;}
            // Complete every waiter even if the source's final file cleanup fails.
            try {entry.SourceLease?.Dispose();}catch(Exception cleanup){if(failure==null)failure=cleanup;else Debug.LogException(cleanup);}
            entry.SourceLease=null;
            if(closed && failure!=null && !(failure is OperationCanceledException) && shutdownFailure==null)shutdownFailure=failure;
            try
            {
                if(failure!=null || !entry.Reusable || closed || entry.Waiters.Count==0)
                {
                    Remove(entry);
                    try{DisposeOwner(loaded);}catch(Exception cleanup){if(failure==null)failure=cleanup;else Debug.LogException(cleanup);}
                    foreach(var waiter in entry.Waiters.ToArray())FinishWaiter(waiter,null,failure is OperationCanceledException && entry.Cancellation.IsCancellationRequested?null:failure,true);
                }
                else
                {
                    entry.Owner=loaded;entry.Delivering=true;
                    foreach(var waiter in entry.Waiters.ToArray())
                    {
                        if(waiter.Token.IsCancellationRequested)FinishWaiter(waiter,null,null,true);
                        else FinishWaiter(waiter,Lease(entry),null,false);
                    }
                    entry.Delivering=false;if(entry.Clients==0)ReturnIdle(entry);
                }
            }
            catch(Exception error)
            {
                Remove(entry);entry.Delivering=false;
                foreach(var waiter in entry.Waiters.ToArray())FinishWaiter(waiter,null,error,false);
                try{if(entry.Owner!=null)Evict(entry);}catch(Exception cleanup){Debug.LogException(cleanup);}
                if(closed && shutdownFailure==null)shutdownFailure=error;
            }
            finally
            {
                var cleanup=new UIFrame.CleanupFailure();cleanup.Run(entry.Admission.Dispose);cleanup.Run(entry.Cancellation.Dispose);loading--;
                try{cleanup.Throw();}catch(Exception error){if(closed && shutdownFailure==null)shutdownFailure=error;throw;}
            }
        }
        public void Invalidate(ImageReference image)
        {
            MediaThread.Check();if(image==null)throw new ArgumentNullException(nameof(image));
            foreach(var entry in entries.Values.Where(e=>e.Key.Source==image.Source && e.Key.Identity==image.OriginId).ToArray())
            {Remove(entry);if(entry.Owner==null)entry.Cancellation.Cancel();else Evict(entry);}
        }
        public async UniTask ShutdownAsync()
        {
            MediaThread.Check();if(!closed)
            {
                closed=true;var cleanup=new UIFrame.CleanupFailure();
                foreach(var entry in entries.Values.ToArray())
                {
                    Remove(entry);if(entry.Owner==null)cleanup.Run(entry.Cancellation.Cancel);else cleanup.Run(()=>Evict(entry));
                }
                try{cleanup.Throw();}catch(Exception error){shutdownFailure=error;}
            }
            while(loading!=0 || pendingDisposals!=0)await UniTask.Yield();
            if(shutdownFailure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(shutdownFailure).Throw();
        }
    }
}
