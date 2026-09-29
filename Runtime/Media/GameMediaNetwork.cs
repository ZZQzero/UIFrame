using System.Runtime.InteropServices;
using UnityEngine;

namespace Game.Media
{
    public static class GameMediaNetwork
    {
#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")][return: MarshalAs(UnmanagedType.I1)] static extern bool UFMIsUnmeteredWifi();
#endif
        /// <summary>True only for a platform-confirmed usable, unmetered Wi-Fi path. Unknown and Editor return false.</summary>
        public static bool IsUnmeteredWifi()
        {
            MediaThread.Check();
#if UNITY_ANDROID && !UNITY_EDITOR
            using var bridge = new AndroidJavaClass("com.zzq.uiframe.media.GalleryBridge");
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            return bridge.CallStatic<bool>("isUnmeteredWifi", activity);
#elif UNITY_IOS && !UNITY_EDITOR
            return UFMIsUnmeteredWifi();
#else
            return false;
#endif
        }
    }
}
