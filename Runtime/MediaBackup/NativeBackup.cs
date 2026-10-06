using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Game.Media.Backup
{
    [Serializable] internal sealed class NativeBackupRequest
    {
        public string op, repository, id, token;
        public long generation;
        public bool cancel;
    }
    [Serializable] internal sealed class NativeBackupStatus
    {
        public string error, root;
        public bool exists;
    }
    internal static class NativeBackup
    {
        internal static bool Available => Application.platform == RuntimePlatform.Android || Application.platform == RuntimePlatform.IPhonePlayer;
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] static extern IntPtr UFBCall(string json);
        [DllImport("__Internal")] static extern void UFBFree(IntPtr pointer);
#endif
        internal static NativeBackupStatus Call(NativeBackupRequest request)
        {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            string json = JsonUtility.ToJson(request), result;
#endif
#if UNITY_ANDROID && !UNITY_EDITOR
            bool attach = !Cysharp.Threading.Tasks.PlayerLoopHelper.IsMainThread;
            if (attach && AndroidJNI.AttachCurrentThread() != 0) throw new InvalidOperationException("Cannot attach backup worker to Android runtime.");
            try
            {
                using var unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
                using var bridge = new AndroidJavaClass("com.zzq.uiframe.media.BackupBridge");
                result = bridge.CallStatic<string>("call", activity, json);
            }
            finally { if (attach) AndroidJNI.DetachCurrentThread(); }
#elif UNITY_IOS && !UNITY_EDITOR
            var pointer = UFBCall(json);
            try { result = Marshal.PtrToStringAnsi(pointer); } finally { UFBFree(pointer); }
#else
            throw new PlatformNotSupportedException("Native background backup requires an Android or iOS player.");
#endif
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
            var status = JsonUtility.FromJson<NativeBackupStatus>(result);
            // Bridge failures are distinct from recorded per-transfer errors.
            if (!status.exists && !string.IsNullOrEmpty(status.error)) throw new InvalidOperationException(status.error);
            return status;
#endif
        }
    }
}
