using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace UIFrame.Sqlite.Internal
{
    internal sealed class NativeResult : IDisposable
    {
        internal NativeMethods.Completion Completion;
        internal readonly CompletionDispatcher Owner;
        int released;
        internal NativeResult(CompletionDispatcher owner)
        {
            Owner = owner;
        }
        internal void Abandon()
        {
            released = 1;
            GC.SuppressFinalize(this);
        }
        internal byte[] Copy()
        {
            lock (this)
            {
                if (released != 0)
                    throw new ObjectDisposedException(nameof(NativeResult));
                var bytes = new byte[checked((int)Completion.DataSize)];
                if (bytes.Length != 0)
                    Marshal.Copy(Completion.Data, bytes, 0, bytes.Length);
                return bytes;
            }
        }
        public void Dispose()
        {
            lock (this)
            {
                if (released != 0)
                    return;
                released = 1;
                if (Completion.Operation != 0)
                    Owner.Release(Completion.Operation);
            }
            GC.SuppressFinalize(this);
        }
        internal void ReleaseAfter(Exception primary)
        {
            try
            {
                Dispose();
            }
            catch (Exception cleanup)
            {
                if (primary == null)
                    throw;
                SqliteRuntime.ReportCleanup(cleanup);
            }
        }
        ~NativeResult()
        {
            try
            {
                Dispose();
            }
            catch (Exception error)
            {
                SqliteRuntime.ReportCleanup(error);
            }
        }
    }
    internal sealed class CompletionDispatcher
    {
        sealed class Pending
        {
            internal readonly TaskCompletionSource<NativeResult> Source =
                new TaskCompletionSource<NativeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            internal CancellationTokenRegistration Cancellation;
            internal CancellationToken Token;
            internal NativeResult Result;
        }
        readonly object gate = new object();
        readonly Dictionary<ulong, Pending> pending = new Dictionary<ulong, Pending>();
        readonly Dictionary<ulong, WeakReference<NativeResult>> results =
            new Dictionary<ulong, WeakReference<NativeResult>>();
        readonly ulong client;
        readonly Thread pump;
        readonly TaskCompletionSource<bool> faultCleanup =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        volatile bool stopping;
        bool nativeStopped;
        internal bool IsReleased { get; private set; }
        Exception fault;
        internal CompletionDispatcher()
        {
            if (NativeMethods.ufsqlite_abi() != NativeMethods.Abi)
                throw new InvalidOperationException("SQLite native ABI mismatch.");
            NativeMethods.Check(NativeMethods.ufsqlite_client_create(NativeMethods.Abi, out client));
            try
            {
                pump = new Thread(Drain) { IsBackground = true, Name = "UIFrame.Sqlite completion" };
                pump.Start();
            }
            catch
            {
                try
                {
                    NativeMethods.Check(NativeMethods.ufsqlite_client_release(client));
                }
                catch (Exception cleanup)
                {
                    SqliteRuntime.ReportCleanup(cleanup);
                }
                throw;
            }
        }
        internal Task<NativeResult> Submit(ulong database, uint kind, int size, Func<byte[]> encode,
                                           uint rows, uint bytes, uint timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (fault != null)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fault).Throw();
                if (stopping)
                    throw new ObjectDisposedException(nameof(CompletionDispatcher));
                var operation = new Pending { Token = token, Result = new NativeResult(this) };
                var weakResult = new WeakReference<NativeResult>(operation.Result);
                NativeMethods.Check(NativeMethods.ufsqlite_reserve(client, database, kind, (ulong)size, rows,
                                                                   bytes, timeout, out var id));
                bool registered = false;
                try
                {
                    var data = encode();
                    if (data.Length != size)
                        throw new InvalidOperationException("Parameters changed during admission.");
                    token.ThrowIfCancellationRequested();
                    pending.Add(id, operation);
                    registered = true;
                    results.Add(id, weakResult);
                    operation.Cancellation = token.Register(() => Cancel(id));
                    NativeMethods.Check(
                        NativeMethods.ufsqlite_commit_request(client, id, data, (ulong)data.Length));
                    return operation.Source.Task;
                }
                catch (Exception primary)
                {
                    if (registered)
                        pending.Remove(id);
                    results.Remove(id);
                    operation.Result.Abandon();
                    // Registration callbacks only call native cancel; they never acquire this gate.
                    try
                    {
                        operation.Cancellation.Dispose();
                    }
                    catch (Exception cleanup)
                    {
                        SqliteRuntime.ReportCleanup(cleanup);
                    }
                    try
                    {
                        NativeMethods.Check(NativeMethods.ufsqlite_discard_request(client, id));
                    }
                    catch (Exception cleanup)
                    {
                        SqliteRuntime.ReportCleanup(cleanup);
                    }
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
                    throw;
                }
            }
        }
        void Cancel(ulong id)
        {
            int status = NativeMethods.ufsqlite_cancel(client, id);
            if (status != 0 && status != (int)SqliteError.InvalidHandle)
                SqliteRuntime.ReportCleanup(new SqliteException((SqliteError)status, "Cancellation failed."));
        }
        void Drain()
        {
            try
            {
                var batch = new NativeMethods.Completion[32];
                while (!stopping)
                {
                    NativeMethods.Check(
                        NativeMethods.ufsqlite_wait(client, batch, (uint)batch.Length, 1000, out var count));
                    for (int i = 0; i < count; i++)
                    {
                        var completion = batch[i];
                        Pending operation;
                        lock (gate)
                        {
                            operation = pending[completion.Operation];
                        }
                        var result = operation.Result;
                        result.Completion = completion;
                        // Keep this operation pending until it has a terminal Task state.
                        // Cancellation cleanup cannot strand the current completion.
                        try
                        {
                            operation.Cancellation.Dispose();
                        }
                        catch (Exception cleanup)
                        {
                            SqliteRuntime.ReportCleanup(cleanup);
                        }
                        lock (gate)
                        {
                            if (completion.Error != 0)
                            {
                                try
                                {
                                    result.Dispose();
                                }
                                catch (Exception cleanup)
                                {
                                    SqliteRuntime.ReportCleanup(cleanup);
                                }
                                if (completion.Error == (int)SqliteError.Canceled &&
                                    operation.Token.IsCancellationRequested)
                                    operation.Source.TrySetCanceled(operation.Token);
                                else
                                    operation.Source.TrySetException(new SqliteException(
                                        (SqliteError)completion.Error, completion.Message, completion.Code,
                                        completion.Operation, completion.Committed, completion.Reserved,
                                        (SqliteExecutionPhase)completion.Phase));
                            }
                            else
                            {
                                operation.Source.TrySetResult(result);
                            }
                            pending.Remove(completion.Operation);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                Fail(error);
            }
        }
        void Fail(Exception error)
        {
            lock (gate)
            {
                fault = error;
                stopping = true;
            }
            try
            {
                // Native lifetime cleanup is independent of completion marshalling.
                // It preserves buffers already handed to callers and other clients.
                try { StopNative(); }
                catch (Exception cleanup) { SqliteRuntime.ReportCleanup(cleanup); }
                lock (gate)
                {
                    foreach (var entry in pending)
                    {
                        var operation = entry.Value;
                        try { operation.Cancellation.Dispose(); }
                        catch (Exception cleanup) { SqliteRuntime.ReportCleanup(cleanup); }
                        if (operation.Source.Task.Status != TaskStatus.RanToCompletion &&
                            results.Remove(entry.Key))
                        {
                            operation.Result.Abandon();
                            try { NativeMethods.Check(NativeMethods.ufsqlite_discard_result(client, entry.Key)); }
                            catch (Exception cleanup) { SqliteRuntime.ReportCleanup(cleanup); }
                        }
                        operation.Source.TrySetException(error);
                    }
                    pending.Clear();
                }
            }
            finally
            {
                faultCleanup.TrySetResult(true);
                SqliteRuntime.ReportCleanup(error);
            }
        }
        internal Task CloseAsync(ulong database)
        {
            lock (gate)
            {
                if (fault != null)
                    return CloseAfterFault();
                return CloseNormally(database);
            }
        }
        async Task CloseAfterFault()
        {
            await faultCleanup.Task.ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(fault).Throw();
        }
        async Task CloseNormally(ulong database)
        {
            using (await Submit(database, 5, 0, () => Array.Empty<byte>(), 0, 0, 5000,
                                CancellationToken.None).ConfigureAwait(false)) { }
        }
        internal void Release(ulong operation)
        {
            lock (gate)
            {
                if (results.Remove(operation))
                    NativeMethods.Check(NativeMethods.ufsqlite_release_result(client, operation));
            }
        }
        void StopNative()
        {
            lock (gate)
            {
                if (nativeStopped)
                    return;
                nativeStopped = true;
                NativeMethods.Check(NativeMethods.ufsqlite_client_stop(client));
            }
        }
        internal void Shutdown(bool releaseResults)
        {
            List<NativeResult> outstanding = null;
            lock (gate)
            {
                if (releaseResults)
                {
                    outstanding = new List<NativeResult>();
                    foreach (var weak in results.Values)
                        if (weak.TryGetTarget(out var result))
                            outstanding.Add(result);
                }
            }
            Exception primary = fault;
            if (outstanding != null)
                foreach (var result in outstanding)
                    try
                    {
                        result.Dispose();
                    }
                    catch (Exception error)
                    {
                        if (primary == null)
                            primary = error;
                        else
                            SqliteRuntime.ReportCleanup(error);
                    }
            lock (gate)
            {
                // A short weak reference is cleared before its finalizer runs.
                // Domain shutdown also owns those buffers; a later finalizer sees
                // the removed registration and cannot release the same handle twice.
                if (releaseResults && pending.Count == 0)
                    while (results.Count != 0)
                    {
                        ulong operation = 0;
                        foreach (var entry in results) { operation = entry.Key; break; }
                        results.Remove(operation);
                        try { NativeMethods.Check(NativeMethods.ufsqlite_discard_result(client, operation)); }
                        catch (Exception error)
                        {
                            if (primary == null) primary = error;
                            else SqliteRuntime.ReportCleanup(error);
                        }
                    }
                if (pending.Count != 0 || results.Count != 0)
                    throw new InvalidOperationException(
                        "Finish queries and dispose batch results before shutting down SQLite.");
                stopping = true;
            }
            pump.Join();
            if (primary == null)
                primary = fault;
            try { StopNative(); }
            catch (Exception error)
            {
                if (primary == null) primary = error;
                else SqliteRuntime.ReportCleanup(error);
            }
            try
            {
                NativeMethods.Check(NativeMethods.ufsqlite_client_release(client));
                IsReleased = true;
            }
            catch (Exception error)
            {
                if (primary == null)
                    primary = error;
                else
                    SqliteRuntime.ReportCleanup(error);
            }
            if (primary != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }
}
