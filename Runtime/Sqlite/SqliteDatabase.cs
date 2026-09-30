using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UIFrame.Sqlite.Internal;

namespace UIFrame.Sqlite
{
    public sealed class SqliteDatabase
    {
        readonly object gate = new object();
        readonly CompletionDispatcher dispatcher;
        readonly ulong handle;
        Task close;
        int active;
        TaskCompletionSource<bool> idle;
        IDisposable Enter()
        {
            lock (gate)
            {
                if (close != null)
                    throw new ObjectDisposedException(nameof(SqliteDatabase));
                var work = new Work(this);
                active++;
                return work;
            }
        }
        sealed class Work : IDisposable
        {
            SqliteDatabase owner;
            internal Work(SqliteDatabase owner)
            {
                this.owner = owner;
            }
            public void Dispose()
            {
                var value = Interlocked.Exchange(ref owner, null);
                if (value == null)
                    return;
                lock (value.gate)
                {
                    if (--value.active == 0)
                        value.idle?.TrySetResult(true);
                }
            }
        }
        internal SqliteDatabase(CompletionDispatcher dispatcher, ulong handle)
        {
            this.dispatcher = dispatcher;
            this.handle = handle;
        }
        public static async Task<SqliteDatabase> OpenAsync(SqliteOpenOptions options,
                                                           CancellationToken token = default)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            var dispatcher = SqliteRuntime.BeginOpen();
            try
            {
                var size = checked(20 + Wire.StringSize(options.Path));
                var result = await dispatcher.Submit(0, 1, size, () => Wire.Open(options), 0, 0, 5000, token)
                                 .ConfigureAwait(false);
                ulong nativeHandle = result.Completion.Database;
                try
                {
                    result.Dispose();
                    var database = new SqliteDatabase(dispatcher, nativeHandle);
                    SqliteRuntime.Register(database);
                    return database;
                }
                catch (Exception primary)
                {
                    result.ReleaseAfter(primary);
                    try
                    {
                        await dispatcher.CloseAsync(nativeHandle).ConfigureAwait(false);
                    }
                    catch (Exception cleanup)
                    {
                        SqliteRuntime.ReportCleanup(cleanup);
                    }
                    throw;
                }
            }
            finally
            {
                SqliteRuntime.EndOpen();
            }
        }
        internal Task<NativeResult> Submit(uint kind, SqliteCommand[] commands, SqliteQueryBudget budget,
                                           CancellationToken token)
        {
            if (budget == null)
                throw new ArgumentNullException(nameof(budget));
            budget.Validate();
            int size = Wire.Measure(commands);
            lock (gate)
            {
                return dispatcher.Submit(handle, kind, size, () => Wire.Encode(commands, size),
                                         (uint)budget.MaxRows, (uint)budget.MaxBytes,
                                         (uint)budget.Timeout.TotalMilliseconds, token);
            }
        }
        public Task<int> ExecuteAsync(SqliteCommand command, CancellationToken token = default) =>
            ExecuteAsync(command, TimeSpan.FromSeconds(5), token);
        public async Task<int> ExecuteAsync(SqliteCommand command, TimeSpan timeout, CancellationToken token = default)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            using (Enter())
            {
                var result = await Submit(2, new[] { command }, new SqliteQueryBudget(1, 64, timeout), token)
                                 .ConfigureAwait(false);
                Exception primary = null;
                try
                {
                    return checked((int)new ResultPage(result.Copy()).Changes(0));
                }
                catch (Exception error)
                {
                    primary = error;
                    throw;
                }
                finally
                {
                    result.ReleaseAfter(primary);
                }
            }
        }
        public async Task<IReadOnlyList<T>> QueryPageAsync<T>(SqliteCommand command, SqliteQueryBudget budget,
                                                              Func<SqliteRow, T> map,
                                                              CancellationToken token = default)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            if (map == null)
                throw new ArgumentNullException(nameof(map));
            using (Enter())
            {
                var result = await Submit(3, new[] { command }, budget, token).ConfigureAwait(false);
                Exception primary = null;
                try
                {
                    await SqliteRuntime.MappingSlots.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        return await Task.Run(() => SqliteBatchResult.Decode(result.Copy(), 0, map, out _))
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        SqliteRuntime.MappingSlots.Release();
                    }
                }
                catch (Exception error)
                {
                    primary = error;
                    throw;
                }
                finally
                {
                    result.ReleaseAfter(primary);
                }
            }
        }
        public async Task<SqliteBatchResult> ExecuteTransactionAsync(SqliteBatch batch,
                                                                     CancellationToken token = default)
        {
            if (batch == null)
                throw new ArgumentNullException(nameof(batch));
            using (Enter())
            {
                var result = await Submit(4, batch.Commands, batch.Budget, token).ConfigureAwait(false);
                try
                {
                    return new SqliteBatchResult(result);
                }
                catch (Exception error)
                {
                    result.ReleaseAfter(error);
                    throw;
                }
            }
        }
        public async Task CreateSnapshotAsync(SqliteSnapshotOptions options,
                                              CancellationToken token = default)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            using (Enter())
            {
                int size = checked(4 + Wire.Utf8.GetByteCount(options.Destination) + 16);
                Task<NativeResult> request;
                lock (gate) request = dispatcher.Submit(handle, 6, size, () => Wire.Snapshot(options), 0, 0,
                                                        (uint)options.Timeout.TotalMilliseconds, token);
                using (await request.ConfigureAwait(false))
                {
                }
            }
        }
        public SqliteDiagnostics GetDiagnostics() => SqliteRuntime.GetDiagnostics();
        public Task<SqliteStorageInfo> GetStorageInfoAsync(CancellationToken token = default) =>
            MaintainAsync(8, Array.Empty<byte>(), token);
        public Task<SqliteStorageInfo> CheckpointAsync(
            SqliteCheckpointMode mode = SqliteCheckpointMode.Passive, CancellationToken token = default)
        {
            if (mode < SqliteCheckpointMode.Passive || mode > SqliteCheckpointMode.Truncate)
                throw new ArgumentOutOfRangeException(nameof(mode));
            return MaintainAsync(7, new[] { (byte)mode, (byte)0, (byte)0, (byte)0 }, token);
        }
        async Task<SqliteStorageInfo> MaintainAsync(uint kind, byte[] payload, CancellationToken token)
        {
            using (Enter())
            {
                NativeResult result =
                    await dispatcher.Submit(handle, kind, payload.Length, () => payload, 1, 1024, 5000, token)
                        .ConfigureAwait(false);
                Exception primary = null;
                try
                {
                    return SqliteBatchResult.Decode(result.Copy(), 0, row => new SqliteStorageInfo(row),
                                                    out _)[0];
                }
                catch (Exception error)
                {
                    primary = error;
                    throw;
                }
                finally
                {
                    result.ReleaseAfter(primary);
                }
            }
        }
        public Task CloseAsync()
        {
            lock (gate)
            {
                return close ?? (close = CloseCore());
            }
        }
        async Task CloseCore()
        {
            Task wait;
            lock (gate)
            {
                wait = active == 0 ? Task.CompletedTask
                                   : (idle = new TaskCompletionSource<bool>(
                                          TaskCreationOptions.RunContinuationsAsynchronously))
                                         .Task;
            }
            await wait.ConfigureAwait(false);
            await dispatcher.CloseAsync(handle).ConfigureAwait(false);
            SqliteRuntime.Unregister(this);
        }
    }
    public static class SqliteRuntime
    {
        static readonly object gate = new object();
        static readonly HashSet<SqliteDatabase> databases = new HashSet<SqliteDatabase>();
        static CompletionDispatcher dispatcher;
        static Task shutdown;
        static int opening;
        static TaskCompletionSource<bool> opened;
        static Exception cleanupError;
        internal static readonly SemaphoreSlim MappingSlots = new SemaphoreSlim(2, 2);
        /// <summary>Most recent secondary/finalizer failure; normal errors remain on their operation
        /// Task.</summary>
        public static Exception LastCleanupError => Volatile.Read(ref cleanupError);
        internal static void ReportCleanup(Exception error)
        {
            Interlocked.Exchange(ref cleanupError, error);
            System.Diagnostics.Trace.TraceError(error.ToString());
        }
        internal static CompletionDispatcher BeginOpen()
        {
            lock (gate)
            {
                if (shutdown != null)
                    throw new InvalidOperationException("SQLite runtime is shutting down.");
                var value = dispatcher ?? (dispatcher = new CompletionDispatcher());
                opening++;
                return value;
            }
        }
        internal static void EndOpen()
        {
            lock (gate)
            {
                if (--opening == 0)
                    opened?.TrySetResult(true);
            }
        }
        internal static void Register(SqliteDatabase database)
        {
            lock (gate) databases.Add(database);
        }
        internal static void Unregister(SqliteDatabase database)
        {
            lock (gate) databases.Remove(database);
        }
        public static string SourceId =>
            System.Runtime.InteropServices.Marshal.PtrToStringAnsi(NativeMethods.ufsqlite_source_id());
        public static string BuildId =>
            System.Runtime.InteropServices.Marshal.PtrToStringAnsi(NativeMethods.ufsqlite_build_id());
        public static SqliteDiagnostics GetDiagnostics()
        {
            var value = new NativeMethods.Diagnostics {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.Diagnostics>(),
                Abi = NativeMethods.Abi
            };
            NativeMethods.Check(NativeMethods.ufsqlite_get_diagnostics(ref value));
            return new SqliteDiagnostics(value);
        }
        /// <summary>Close all databases after finishing mappings and disposing batch results. Call once at
        /// application shutdown.</summary>
        public static Task ShutdownAsync()
        {
            lock (gate)
            {
                return shutdown ?? (shutdown = ShutdownCore(false));
            }
        }
        internal static Task ShutdownDomainAsync()
        {
            lock (gate)
            {
                return shutdown ?? (shutdown = ShutdownCore(true));
            }
        }
        internal static void StartEditorLifetime()
        {
            lock (gate)
            {
                if (shutdown == null)
                    return;
                if (!shutdown.IsCompleted || (dispatcher != null && !dispatcher.IsReleased))
                    throw new InvalidOperationException("Previous SQLite shutdown still owns native resources.");
                databases.Clear();
                dispatcher = null;
                shutdown = null;
                opened = null;
            }
        }
        static async Task ShutdownCore(bool releaseResults)
        {
            Task wait;
            lock (gate)
            {
                wait = opening == 0 ? Task.CompletedTask
                                    : (opened = new TaskCompletionSource<bool>(
                                           TaskCreationOptions.RunContinuationsAsynchronously))
                                          .Task;
            }
            await wait.ConfigureAwait(false);
            SqliteDatabase[] owned;
            CompletionDispatcher completion;
            lock (gate)
            {
                owned = new SqliteDatabase[databases.Count];
                databases.CopyTo(owned);
                completion = dispatcher;
            }
            Exception primary = null;
            foreach (var database in owned)
                try
                {
                    await database.CloseAsync().ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    if (primary == null)
                        primary = error;
                    else
                        ReportCleanup(error);
                }
            try
            {
                completion?.Shutdown(releaseResults);
            }
            catch (Exception error)
            {
                if (primary == null)
                    primary = error;
                else
                    ReportCleanup(error);
            }
            if (completion == null || completion.IsReleased)
                lock (gate) databases.Clear();
            if (primary != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }
}
