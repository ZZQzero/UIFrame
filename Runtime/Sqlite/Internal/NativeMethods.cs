using System;
using System.Runtime.InteropServices;
using System.Text;

namespace UIFrame.Sqlite.Internal
{
    internal static class NativeMethods
    {
#if UNITY_IOS && !UNITY_EDITOR
        const string Library = "__Internal";
#else
        const string Library = "uiframe_sqlite";
#endif
        internal const uint Abi = 2;
        [StructLayout(LayoutKind.Sequential)]
        internal struct Completion
        {
            internal uint Size, Abi;
            internal ulong Operation, Database;
            internal int Error, Code, Committed, Reserved;
            internal IntPtr Data;
            internal ulong DataSize;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
            internal byte[] MessageBytes;
            // The C ABI always carries UTF-8, including on Windows with a non-UTF8 code page.
            internal string Message
            {
                get
                {
                    int end = Array.IndexOf(MessageBytes, (byte)0);
                    return Encoding.UTF8.GetString(MessageBytes, 0, end < 0 ? MessageBytes.Length : end);
                }
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Diagnostics
        {
            internal uint Size, Abi;
            internal ulong Databases, Operations, ReservedBytes, Completed, SqliteBytes;
        }
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern uint ufsqlite_abi();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ufsqlite_source_id();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr ufsqlite_build_id();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ufsqlite_client_create(uint abi, out ulong client);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ufsqlite_client_release(ulong client);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ufsqlite_submit(ulong client, ulong db, uint kind, byte[] payload,
                                                   ulong size, uint rows, uint bytes, uint timeout,
                                                   out ulong operation);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ufsqlite_reserve(ulong client, ulong db, uint kind, ulong size, uint rows,
                                                    uint bytes, uint timeout, out ulong operation);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ufsqlite_commit_request(ulong client, ulong operation, byte[] payload,
                                                           ulong size);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ufsqlite_discard_request(ulong client, ulong operation);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ufsqlite_cancel(ulong client, ulong operation);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ufsqlite_wait(ulong client, [Out] Completion[] output, uint capacity,
                                                 uint timeout, out uint count);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ufsqlite_release_result(ulong client, ulong operation);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ufsqlite_get_diagnostics(ref Diagnostics diagnostics);
        internal static void Check(int status)
        {
            if (status != 0)
                throw new SqliteException((SqliteError)status,
                                          "Native SQLite request failed: " + (SqliteError)status);
        }
    }
}
