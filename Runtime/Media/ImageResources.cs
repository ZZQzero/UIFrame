using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;

namespace Game.Media
{
    internal sealed class ImageStorage : IDisposable
    {
        readonly object gate = new object();
        readonly string directory;
        int leases = 1;
        bool disposed;
        internal ImageStorage(string directory) { this.directory = directory; }
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
        Texture2D texture;
        Sprite sprite;
        public Texture2D Texture => texture != null ? texture : throw new ObjectDisposedException(nameof(ImageTexture));
        public Sprite Sprite
        {
            get
            {
                MediaThread.Check();
                if (sprite == null) sprite = Sprite.Create(Texture, new Rect(0, 0, Texture.width, Texture.height), Vector2.one * 0.5f);
                return sprite;
            }
        }
        internal ImageTexture(Texture2D texture) { this.texture = texture; }
        public void Dispose()
        {
            MediaThread.Check();
            var oldSprite = sprite; var oldTexture = texture; sprite = null; texture = null;
            Destroy(oldSprite); Destroy(oldTexture);
        }
        internal static void Destroy(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value);
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
