using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Game.Media
{
    [Serializable] internal sealed class MediaRequest
    {
        public string id, op, source, path, output, album, format;
        public float backgroundR = 1, backgroundG = 1, backgroundB = 1;
        public int count = 1, edge = 2048, quality = 90, maxPixels = 4 * 1024 * 1024;
        public long maxBytes;
        public bool recursive,verifyBoundary;
    }
    [Serializable] internal sealed class MediaItem
    {
        public string id, name, mime, path, version, source;
        public int width, height, kind, count = -1;
        public long size = -1;
    }
    [Serializable] internal sealed class MediaResponse
    {
        public string status, code, error, access, boundary;
        public bool more,hasNext,requiresReconcile,accessChanged;
        public MediaItem[] items;
    }

    internal interface IMediaTransport
    {
        void Start(string json);
        string Poll(string id);
        void Cancel(string id);
        bool Pending(string id);
    }

    internal static class NativeMedia
    {
        static readonly Dictionary<string,IMediaTransport> active = new Dictionary<string,IMediaTransport>();
        internal static IMediaTransport Transport { get; set; } = new PlatformTransport();
        sealed class PlatformTransport : IMediaTransport
        {
            public void Start(string json) => NativeMedia.Start(json);
            public string Poll(string id) => NativeMedia.Poll(id);
            public void Cancel(string id) => NativeMedia.Cancel(id);
            public bool Pending(string id) => NativeMedia.Pending(id);
        }
        static bool quitRegistered;
        internal static bool Available
        {
            get
            {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern void UFMStart(string request);
        [DllImport("__Internal")] static extern IntPtr UFMPoll(string id);
        [DllImport("__Internal")] static extern void UFMFree(IntPtr value);
        [DllImport("__Internal")] static extern void UFMCancel(string id);
        [DllImport("__Internal")]
        [return: MarshalAs(UnmanagedType.I1)] static extern bool UFMPending(string id);
#endif
        internal static async UniTask<MediaResponse> Request(MediaRequest request, CancellationToken token, Func<MediaItem[], UniTask<bool>> consume = null)
        {
            MediaThread.Check();
            request.id = Guid.NewGuid().ToString("N");
            var transport = Transport;
            try
            {
                token.ThrowIfCancellationRequested();
                if (!quitRegistered) { Application.quitting += CancelAll; quitRegistered = true; }
                if(active.Count>=32)throw new GalleryException("MediaQueueFull","At most 32 native media requests may be active.");
                transport.Start(JsonUtility.ToJson(request)); active.Add(request.id,transport);
            }
            catch { if (!string.IsNullOrEmpty(request.output)) ImagePaths.CleanAfterFailure(request.output); throw; }
            bool finished = false;Exception primary=null;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    string json = transport.Poll(request.id);
                    if (!string.IsNullOrEmpty(json))
                    {
                        var result = JsonUtility.FromJson<MediaResponse>(json);
                        finished = !result.more;
                        if (result.status == "canceled") throw new OperationCanceledException();
                        if (result.status != "ok") throw new GalleryException(result.code ?? "NativeFailure", result.error);
                        if (consume != null)
                        {
                            token.ThrowIfCancellationRequested();
                            bool proceed = await consume(result.items ?? Array.Empty<MediaItem>());
                            result.items = null;
                            if (!proceed) return result;
                            if (!result.more) return result;
                        }
                        else
                        {
                            if (result.more) throw new InvalidOperationException("Paged native responses require a page consumer.");
                            return result;
                        }
                    }
                    await UniTask.Yield(PlayerLoopTiming.Update, token);
                }
            }
            catch(Exception error){primary=error;throw;}
            finally
            {
                try
                {
                    if (!finished)
                    {
                        transport.Cancel(request.id);
                        // Cancellation is a request. Resource owners may close only
                        // after native registration, enumeration or file work ends.
                        while (transport.Pending(request.id)) await UniTask.Yield(PlayerLoopTiming.Update);
                    }
                }
                catch(Exception cleanup){if(primary==null)throw;Debug.LogException(cleanup);}
                finally {active.Remove(request.id);}
            }
        }
        static void CancelAll()
        {
            foreach (var item in active)
            {
                try { item.Value.Cancel(item.Key); }
                catch (Exception error) { Debug.LogException(error); }
            }
            active.Clear();
        }
        static void Start(string json)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using var bridge = new AndroidJavaClass("com.zzq.uiframe.media.GalleryBridge");
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            bridge.CallStatic("start", activity, json);
#elif UNITY_IOS && !UNITY_EDITOR
            UFMStart(json);
#else
            throw new PlatformNotSupportedException("Native photo libraries are supported on Android and iOS players.");
#endif
        }
        static string Poll(string id)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using var bridge = new AndroidJavaClass("com.zzq.uiframe.media.GalleryBridge");
            return bridge.CallStatic<string>("poll", id);
#elif UNITY_IOS && !UNITY_EDITOR
            IntPtr value = UFMPoll(id);
            try { return value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value); }
            finally { if (value != IntPtr.Zero) UFMFree(value); }
#else
            throw new PlatformNotSupportedException();
#endif
        }
        static void Cancel(string id)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using var bridge = new AndroidJavaClass("com.zzq.uiframe.media.GalleryBridge");
            bridge.CallStatic("cancel", id);
#elif UNITY_IOS && !UNITY_EDITOR
            UFMCancel(id);
#endif
        }
        static bool Pending(string id)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using var bridge = new AndroidJavaClass("com.zzq.uiframe.media.GalleryBridge");
            return bridge.CallStatic<bool>("pending", id);
#elif UNITY_IOS && !UNITY_EDITOR
            return UFMPending(id);
#else
            return false;
#endif
        }
    }
}
