using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using Cysharp.Threading.Tasks;

namespace Game.Media
{
    internal sealed class ImageStorage : IDisposable
    {
        readonly object gate = new object();
        readonly string directory;
        int leases = 1;
        bool disposed;
        internal ImageStorage(string directory) { this.directory = directory; }
        internal void CheckAvailable()
        { lock(gate) { if(disposed)throw new ObjectDisposedException(nameof(ImageSelection)); } }
        internal IDisposable Acquire()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(ImageSelection));
                leases++; return new Lease(this);
            }
        }
        public void Dispose()
        {
            lock (gate) { if (disposed) return; disposed = true; }
            Release();
        }
        void Release()
        {
            bool clean;
            lock (gate) { clean = --leases == 0; }
            if (clean && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        sealed class Lease : IDisposable
        {
            ImageStorage owner;
            public Lease(ImageStorage owner) { this.owner = owner; }
            public void Dispose() { Interlocked.Exchange(ref owner, null)?.Release(); }
        }
    }

    public sealed class ImageSelection : IDisposable
    {
        ImageStorage storage;
        public IReadOnlyList<ImageReference> Items { get; }
        internal ImageSelection(ImageReference[] items, ImageStorage storage)
        { Items = Array.AsReadOnly(items); this.storage = storage; }
        public void Dispose() { Interlocked.Exchange(ref storage, null)?.Dispose(); }
    }

    public sealed class ImageFile : IDisposable
    {
        ImageStorage storage;
        readonly string path;
        public string LocalPath { get { if (storage == null) throw new ObjectDisposedException(nameof(ImageFile)); return path; } }
        public string MimeType { get; }
        public long ByteCount { get; }
        internal ImageFile(string path, ImageStorage storage)
        { this.path = path; this.storage = storage; MimeType = ImagePaths.Mime(Path.GetExtension(path)); ByteCount = new FileInfo(path).Length; }
        public void Dispose() { Interlocked.Exchange(ref storage, null)?.Dispose(); }
    }

    public sealed class ImageTexture : IDisposable
    {
        internal sealed class Resource
        {
            internal Texture2D Texture;
            internal Sprite Sprite;
            internal int Leases;
            internal readonly long Bytes;
            internal Resource(Texture2D texture)
            {
                Texture=texture??throw new ArgumentNullException(nameof(texture));
                Bytes=UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(texture);
                if(Bytes<=0)Bytes=(long)texture.width*texture.height*4*(texture.isReadable?2:1);
            }
            internal void Release()
            {
                if(--Leases!=0)return;
                var sprite=Sprite;var texture=Texture;Sprite=null;Texture=null;
                var cleanup=new UIFrame.CleanupFailure();cleanup.Run(()=>Destroy(sprite));cleanup.Run(()=>Destroy(texture));cleanup.Throw();
            }
        }
        Resource resource;
        Action released;
        public Texture2D Texture => resource?.Texture!=null?resource.Texture:throw new ObjectDisposedException(nameof(ImageTexture));
        public Sprite Sprite
        {
            get
            {
                MediaThread.Check();var texture=Texture;
                if(resource.Sprite==null)resource.Sprite=Sprite.Create(texture,new Rect(0,0,texture.width,texture.height),Vector2.one*0.5f);
                return resource.Sprite;
            }
        }
        internal long ByteCount=>resource?.Bytes??0;
        internal ImageTexture(Texture2D texture):this(new Resource(texture),null){}
        ImageTexture(Resource resource,Action released){this.resource=resource;this.released=released;resource.Leases++;}
        internal ImageTexture Retain(Action onRelease=null)
        {MediaThread.Check();if(resource==null)throw new ObjectDisposedException(nameof(ImageTexture));return new ImageTexture(resource,onRelease);}
        public void Dispose()
        {
            MediaThread.Check();var old=resource;var callback=released;resource=null;released=null;if(old==null)return;
            var cleanup=new UIFrame.CleanupFailure();cleanup.Run(old.Release);if(callback!=null)cleanup.Run(callback);cleanup.Throw();
        }
        internal static long PendingDestroyBytes { get; private set; }
        static async Cysharp.Threading.Tasks.UniTask ObserveDestroy(long bytes)
        {await Cysharp.Threading.Tasks.UniTask.NextFrame();PendingDestroyBytes-=bytes;}
        internal static void Destroy(UnityEngine.Object value)
        {
            if(value==null)return;
            if(Application.isPlaying) {
                long bytes=value is Texture2D?UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(value):0;
                UnityEngine.Object.Destroy(value);
                if(bytes>0){PendingDestroyBytes+=bytes;ObserveDestroy(bytes).Forget(error=>Debug.LogException(error));}
            } else UnityEngine.Object.DestroyImmediate(value);
        }
    }

    internal static class MediaThread
    {
        internal static void Check()
        {
            if (!Cysharp.Threading.Tasks.PlayerLoopHelper.IsMainThread)
                throw new InvalidOperationException("Media Unity APIs require the Unity main thread.");
        }
    }

    internal static class ImagePaths
    {
        static string session;
        static FileStream sessionLock;
        internal static string NewDirectory()
        {
            MediaThread.Check();
            if (session == null)
            {
                string root = Path.Combine(Application.temporaryCachePath, "UIFrameImages");
                Directory.CreateDirectory(root);
                foreach (string previous in Directory.EnumerateDirectories(root, "session-*"))
                {
                    FileStream oldLock;
                    try { oldLock = new FileStream(Path.Combine(previous, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                    catch (IOException) { continue; } // Another live runtime owns this session.
                    oldLock.Dispose(); Directory.Delete(previous, true);
                }
                string next = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(next);
                sessionLock = new FileStream(Path.Combine(next, "owner.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                session = next;
                Application.quitting += () => { sessionLock?.Dispose(); sessionLock = null; session = null; };
            }
            var path = Path.Combine(session, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }
        internal static string Mime(string extension)
        {
            switch (extension.ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".heic": case ".heif": return "image/heic";
                case ".webp": return "image/webp";
                case ".gif": return "image/gif";
                default: return "application/octet-stream";
            }
        }
        internal static string Extension(string name)
        {
            var ext = Path.GetExtension(name ?? "").ToLowerInvariant();
            return Mime(ext) == "application/octet-stream" ? ".bin" : ext;
        }
        internal static void CleanAfterFailure(string directory)
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch (Exception cleanup) { Debug.LogException(cleanup); }
        }
    }
}
