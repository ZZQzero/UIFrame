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
        public bool recursive;
    }
    [Serializable] internal sealed class MediaItem
    {
        public string id, name, mime, path, version, source;
        public int width, height, count = -1;
        public long size = -1;
    }
    [Serializable] internal sealed class MediaResponse
    {
        public string status, code, error, access;
        public bool more;
        public MediaItem[] items;
    }

    internal static class NativeMedia
    {
        static readonly HashSet<string> active = new HashSet<string>();
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
            try
            {
                token.ThrowIfCancellationRequested();
                if (!quitRegistered) { Application.quitting += CancelAll; quitRegistered = true; }
                Start(JsonUtility.ToJson(request)); active.Add(request.id);
            }
            catch { if (!string.IsNullOrEmpty(request.output)) ImagePaths.CleanAfterFailure(request.output); throw; }
            bool finished = false;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    string json = Poll(request.id);
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
            finally
            {
                active.Remove(request.id);
                if (!finished)
                {
                    Cancel(request.id);
                    // A caller may own the parent of output. Do not release that parent while native code can still write.
                    if (!string.IsNullOrEmpty(request.output))
                        while (Pending(request.id)) await UniTask.Yield(PlayerLoopTiming.Update);
                }
            }
        }
        static void CancelAll()
        {
            foreach (var id in active)
            {
                try { Cancel(id); }
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
