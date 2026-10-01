using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Game.Media.Backup
{
    public sealed class BackupRepositoryException : Exception
    {
        public int Code { get; }
        public int SqliteCode { get; }
        public int Phase { get; }
        public bool CommitOutcomeUnknown { get; }
        public bool IsIsolatedCleanupFailure => Code == 1001;
        internal BackupRepositoryException(int code, int sqlite, int phase, int committed, string message) : base(message)
        { Code = code; SqliteCode = sqlite; Phase = phase; CommitOutcomeUnknown = committed < 0; }
    }

    // SQL and task transitions live exclusively in the common native repository.
    // This type only transports typed commands and maps bounded result pages.
    internal sealed class BackupRepository : IDisposable
    {
        internal enum Command
        {
            Info=1, Prepare=2, Seal=3, Accept=4, Abandon=5, Tasks=6, Task=7, Summary=8,
            Claim=9, Submitted=10, Progress=11, Finish=12, Release=13, Action=14, Pause=15,
            Receipts=16, Receipt=17, CleanupPage=18, CleanupRun=19, CleanupRetry=21,
            Start=22, Attempts=23, Changes=24, Policy=25, SetPolicy=26,
            Scope=30, BeginScan=31, ScanPage=32, ActivateScan=33, Discover=34, Discoveries=35, Disposition=36,
            Operation=40, SelectOperation=41, ApplyOperation=42, OperationStatus=43, OperationItems=44,
            HistoryPage=50, PruneTask=51, PruneOperation=52, Storage=53, Checkpoint=54,
            RecoverPreparations=55, FileSummary=56, RecoverAttempt=57, BindPreparer=58,
            ScheduleRetry=59, Attempt=60, Ready=62, Preparation=64, ReservePayload=65, Operations=66, PruneChanges=67, PruneScans=68, SuspendScope=69, ResetScope=70, ResetScopePage=71, OperationCleanupPage=72, ScopeState=73, Handoff=74, Schedulable=75, CleanupFailures=76, BeginReconcile=77, EndReconcile=78
        }
        const int Limit = 1024 * 1024;
        const string Library =
#if UNITY_IOS && !UNITY_EDITOR
            "__Internal";
#else
            "uiframe_backup";
#endif
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        [StructLayout(LayoutKind.Sequential)]
        struct Status
        {
            internal uint size, abi;
            internal int error, sqlite, committed;
            internal uint phase, length;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst=256)] internal byte[] message;
            internal static Status New() => new Status { size=284, abi=1, message=new byte[256] };
        }
        [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] static extern uint ufbackup_abi();
        [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] static extern int ufbackup_open(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string root, [MarshalAs(UnmanagedType.LPUTF8Str)] string id,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string server, [MarshalAs(UnmanagedType.LPUTF8Str)] string account,
            int create, out ulong handle, ref Status status);
        [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] static extern int ufbackup_close(ulong handle, ref Status status);
        [DllImport(Library, CallingConvention=CallingConvention.Cdecl)] static extern int ufbackup_call(
            ulong handle, uint command, byte[] input, uint length, [Out] byte[] output, uint capacity, ref Status status);
        readonly object gate = new object();
        ulong handle;
        int pending;
        static int globalPending;
        internal readonly string Root, StoreId, Directory;

        internal BackupRepository(string root, string storeId, string server, string account)
        {
            Root=Path.GetFullPath(root); StoreId=storeId; Directory=Path.Combine(Root,storeId);
            if (ufbackup_abi()!=1) throw new InvalidOperationException("Backup repository native ABI mismatch.");
            var status=Status.New();
            Check(ufbackup_open(Root,storeId,server,account,File.Exists(Path.Combine(Directory,"catalog.sqlite"))?0:1,out handle,ref status),status);
        }
        static void Check(int code, Status status)
        {
            if (code==0) return;
            int count=Array.IndexOf(status.message,(byte)0); if (count<0) count=status.message.Length;
            // Diagnostics may end at the fixed native byte limit. Decode their
            // text without replacing the actual repository failure identity.
            throw new BackupRepositoryException(code,status.sqlite,(int)status.phase,status.committed,Encoding.UTF8.GetString(status.message,0,count));
        }
        internal async UniTask<Result> ExecuteAsync(Command command, CancellationToken token=default, params object[] arguments)
        {
            token.ThrowIfCancellationRequested();
            lock(gate)
            {
                if(handle==0) throw new ObjectDisposedException(nameof(BackupRepository));
                if(pending>=16) throw new InvalidOperationException("Backup repository has 16 admitted calls; await existing work before submitting more.");
                if(Interlocked.Increment(ref globalPending)>16) { Interlocked.Decrement(ref globalPending); throw new InvalidOperationException("Backup command admission is full; await existing work."); }
                pending++;
            }
            try
            {
                // Cancellation before entering native code aborts admission. Once
                // entered, the caller observes the definite commit/error result.
                return await UniTask.RunOnThreadPool(() => { token.ThrowIfCancellationRequested(); return Execute(command,arguments); });
            }
            finally { lock(gate) pending--; Interlocked.Decrement(ref globalPending); }
        }
        internal Result Execute(Command command, params object[] arguments)
        {
            ulong current;
            lock(gate) { if(handle==0) throw new ObjectDisposedException(nameof(BackupRepository)); current=handle; }
            byte[] input=Encode(arguments), output=ArrayPool<byte>.Shared.Rent(Limit);
            try
            {
                var status=Status.New();
                Check(ufbackup_call(current,(uint)command,input,(uint)input.Length,output,Limit,ref status),status);
                return Decode(output,checked((int)status.length));
            }
            finally { ArrayPool<byte>.Shared.Return(output); }
        }
        internal static byte[] Encode(object[] values)
        {
            if(values==null || values.Length>2048) throw new ArgumentException("At most 2048 typed values are accepted.");
            long size=4;
            foreach(var value in values)
            {
                if(value==null) size++;
                else if(value is long || value is int || value is bool) size+=9;
                else if(value is string text) size+=5+Utf8.GetByteCount(text);
                else if(value is byte[] bytes) size+=5+bytes.Length;
                else throw new ArgumentException("Unsupported repository value.");
                if(size>Limit) throw new ArgumentException("Backup command exceeds 1 MiB.");
            }
            using var stream=new MemoryStream((int)size);
            using var writer=new BinaryWriter(stream,Utf8,true); writer.Write(values.Length);
            foreach(var value in values)
            {
                if(value==null) writer.Write((byte)0);
                else if(value is long || value is int || value is bool) { writer.Write((byte)1); writer.Write(Convert.ToInt64(value)); }
                else
                {
                    bool text=value is string; writer.Write((byte)(text?3:4));
                    byte[] bytes=text?Utf8.GetBytes((string)value):(byte[])value;
                    writer.Write(bytes.Length); writer.Write(bytes);
                }
            }
            return stream.ToArray();
        }
        internal sealed class Row
        {
            readonly Dictionary<string,int> columns;
            readonly object[] values;
            internal Row(Dictionary<string,int> columns,object[] values) { this.columns=columns; this.values=values; }
            internal object this[string name] => values[columns[name]];
            internal string Text(string name) => (string)this[name];
            internal long Number(string name) => this[name]==null?0:(long)this[name];
            internal bool Flag(string name) => Number(name)!=0;
        }
        internal sealed class Result
        {
            internal readonly List<IReadOnlyList<Row>> Tables = new List<IReadOnlyList<Row>>();
            internal IReadOnlyList<Row> Rows => Tables.Count==0?Array.Empty<Row>():Tables[Tables.Count-1];
            internal Row Single => Rows.Count==1?Rows[0]:throw new InvalidOperationException("Expected one repository result.");
        }
        static Result Decode(byte[] bytes,int count)
        {
            var result=new Result(); if(count==0) return result;
            using var stream=new MemoryStream(bytes,0,count,false);
            using var reader=new BinaryReader(stream,Utf8);
            int tables=reader.ReadInt32(); if(tables<0 || tables>200) throw new InvalidDataException("Invalid repository table count.");
            string ReadString()
            {
                int n=reader.ReadInt32(); if(n<0 || n>stream.Length-stream.Position) throw new InvalidDataException("Invalid repository string length.");
                return Utf8.GetString(reader.ReadBytes(n));
            }
            for(int t=0;t<tables;t++)
            {
                int columns=reader.ReadInt32(), rows=reader.ReadInt32(); reader.ReadInt64();
                if(columns<0 || columns>1024 || rows<0 || rows>200) throw new InvalidDataException("Invalid repository table dimensions.");
                var names=new Dictionary<string,int>(columns,StringComparer.Ordinal);
                for(int c=0;c<columns;c++) names.Add(ReadString(),c);
                var items=new List<Row>(rows);
                for(int r=0;r<rows;r++)
                {
                    var values=new object[columns];
                    for(int c=0;c<columns;c++)
                    {
                        switch(reader.ReadByte())
                        {
                            case 0: break;
                            case 1: values[c]=reader.ReadInt64(); break;
                            case 2: values[c]=reader.ReadDouble(); break;
                            case 3: values[c]=ReadString(); break;
                            case 4:
                                int n=reader.ReadInt32(); if(n<0 || n>stream.Length-stream.Position) throw new InvalidDataException("Invalid repository blob length.");
                                values[c]=reader.ReadBytes(n); break;
                            default: throw new InvalidDataException("Invalid repository value tag.");
                        }
                    }
                    items.Add(new Row(names,values));
                }
                result.Tables.Add(items.AsReadOnly());
            }
            if(stream.Position!=count) throw new InvalidDataException("Trailing repository result bytes.");
            return result;
        }
        public void Dispose()
        {
            lock(gate)
            {
                if(handle==0) return;
                if(pending!=0) throw new InvalidOperationException("Await repository calls before closing its attachment.");
                ulong old=handle; handle=0; var status=Status.New(); Check(ufbackup_close(old,ref status),status);
            }
        }
    }
}
