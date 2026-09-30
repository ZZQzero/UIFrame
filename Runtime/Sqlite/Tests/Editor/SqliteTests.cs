using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace UIFrame.Sqlite.Tests
{
    public sealed class SqliteTests
    {
        string directory;
        SqliteDatabase database;
        static void Run(Func<Task> body)
        {
            var task = Task.Run(body);
            Assert.IsTrue(Task.WhenAny(task, Task.Delay(15000)).GetAwaiter().GetResult() == task,
                          "SQLite test exceeded its deadline.");
            task.GetAwaiter().GetResult();
        }
        [SetUp]
        public void Setup() => Run(
            async () =>
            {
                directory = Path.Combine(Path.GetTempPath(),
                                         "uiframe-照片 😀-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                database = await SqliteDatabase.OpenAsync(
                    new SqliteOpenOptions(Path.Combine(directory, "store.sqlite"), SqliteOpenMode.CreateNew));
                await database.ExecuteAsync(
                    new SqliteCommand("CREATE TABLE items(id INTEGER PRIMARY KEY,value TEXT, data BLOB)"));
            });
        [TearDown]
        public void Teardown() => Run(async () =>
                                      {
                                          if (database != null)
                                              await database.CloseAsync();
                                          Directory.Delete(directory, true);
                                      });
        static object Member(object target, string name) => target.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
        static object Invoke(object target, string name, params object[] arguments)
        {
            try { return target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(target, arguments); }
            catch (TargetInvocationException error)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }
        static async Task<(object dispatcher, SqliteDatabase database)> OpenIsolated(string path)
        {
            var type = typeof(SqliteDatabase).Assembly.GetType("UIFrame.Sqlite.Internal.CompletionDispatcher");
            var completion = Activator.CreateInstance(type, true);
            byte[] payload;
            using (var bytes = new MemoryStream())
            {
                using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, true))
                {
                    var encoded = System.Text.Encoding.UTF8.GetBytes(path);
                    writer.Write(0); writer.Write(encoded.Length); writer.Write(encoded);
                    writer.Write(0L); writer.Write(128L * 1024 * 1024);
                }
                payload = bytes.ToArray();
            }
            var opened = (Task)Invoke(completion, "Submit", 0UL, 1U, payload.Length,
                (Func<byte[]>)(() => payload), 0U, 0U, 5000U, CancellationToken.None);
            await opened;
            var result = opened.GetType().GetProperty("Result").GetValue(opened);
            ulong handle = (ulong)Member(Member(result, "Completion"), "Database");
            ((IDisposable)result).Dispose();
            var isolated = (SqliteDatabase)Activator.CreateInstance(typeof(SqliteDatabase),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { completion, handle }, null);
            return (completion, isolated);
        }
        [Test]
        public void DomainCleanupReclaimsResultWhoseWeakTargetWasCleared() => Run(async () =>
        {
            var owner = await OpenIsolated(Path.Combine(directory, "domain.sqlite"));
            var retained = await owner.database.ExecuteTransactionAsync(new SqliteBatch(
                new SqliteQueryBudget(1, 128), new SqliteCommand("SELECT 23")));
            await owner.database.CloseAsync();
            var entries = (System.Collections.IDictionary)Member(owner.dispatcher, "results");
            var id = entries.Keys.Cast<object>().Single();
            // Reproduce the CLR ordering deterministically: weak target cleared,
            // then domain cleanup, then the deferred finalizer's Dispose call.
            entries[id] = Activator.CreateInstance(entries[id].GetType(), new object[] { null });
            Invoke(owner.dispatcher, "Shutdown", true);
            Assert.AreEqual(0, SqliteRuntime.GetDiagnostics().OutstandingOperations);
            Assert.AreEqual(0, SqliteRuntime.GetDiagnostics().ReservedBytes);
            retained.Dispose();
            Assert.AreEqual(1, SqliteRuntime.GetDiagnostics().OpenDatabases);
        });
        [Test]
        public void CompletionFaultClosesNativeStoreAndPreservesDeliveredResults() => Run(async () =>
        {
            string path = Path.Combine(directory, "fault.sqlite");
            var owner = await OpenIsolated(path);
            var completion = owner.dispatcher;
            var type = completion.GetType();
            var isolated = owner.database;
            var retained = await isolated.ExecuteTransactionAsync(new SqliteBatch(
                new SqliteQueryBudget(1, 128), new SqliteCommand("SELECT 19")));
            var original = new InvalidOperationException("completion fault sentinel");
            // Suspend the consumer at an idle boundary, then execute its production
            // failure handler with an undelivered native operation still in flight.
            type.GetField("stopping", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(completion, true);
            Assert.IsTrue(((Thread)Member(completion, "pump")).Join(3000));
            type.GetField("stopping", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(completion, false);
            var pending = isolated.QueryPageAsync(new SqliteCommand(
                "WITH RECURSIVE r(x) AS (VALUES(0) UNION ALL SELECT x+1 FROM r WHERE x<100000000) SELECT sum(x) FROM r"),
                new SqliteQueryBudget(1, 128), row => row.GetInt64(0));
            Invoke(completion, "Fail", original);
            Assert.AreSame(original, Assert.ThrowsAsync<InvalidOperationException>(async () => await pending));
            Assert.AreSame(original, Assert.ThrowsAsync<InvalidOperationException>(async () => await isolated.CloseAsync()));
            try { Assert.AreEqual(19, retained.MapRows(0, row => row.GetInt64(0))[0]); }
            finally { retained.Dispose(); }
            Assert.AreSame(original, Assert.Throws<InvalidOperationException>(() => Invoke(completion, "Shutdown", false)));
            // The independent runtime client remains usable, and the stopped owner
            // has released its OS file lock and native database registration.
            var reopened = await SqliteDatabase.OpenAsync(new SqliteOpenOptions(path, SqliteOpenMode.OpenExistingReadWrite));
            await reopened.CloseAsync();
            Assert.AreEqual(7, (await database.QueryPageAsync(new SqliteCommand("SELECT 7"),
                new SqliteQueryBudget(1, 128), row => row.GetInt64(0)))[0]);
            Assert.AreEqual(1, SqliteRuntime.GetDiagnostics().OpenDatabases);
            Assert.AreEqual(0, SqliteRuntime.GetDiagnostics().OutstandingOperations);
            Assert.AreEqual(0, SqliteRuntime.GetDiagnostics().ReservedBytes);
        });
        [Test]
        public void DiagnosticsTrackWorkBuffersAndFailurePhase() => Run(async () =>
        {
            var before = SqliteRuntime.GetDiagnostics();
            for (int i = 0; i < 2; i++)
                await database.QueryPageAsync(new SqliteCommand("SELECT 123"), new SqliteQueryBudget(1, 128),
                    row => row.GetInt64(0));
            await database.ExecuteAsync(new SqliteCommand("INSERT INTO items VALUES(1,'metrics',NULL)"));
            var failure = Assert.ThrowsAsync<SqliteException>(async () =>
                await database.ExecuteAsync(new SqliteCommand("DELETE FROM missing_metrics_table")));
            Assert.AreEqual(SqliteExecutionPhase.Prepare, failure.Phase);
            using (var result = await database.ExecuteTransactionAsync(new SqliteBatch(
                new SqliteQueryBudget(1, 128), new SqliteCommand("SELECT 8"))))
            {
                var held = SqliteRuntime.GetDiagnostics();
                Assert.Greater(held.HeldResultBytes, 0);
                Assert.GreaterOrEqual(held.ReservedResultBytes, 128);
                Assert.Greater(held.ParameterBytes, 0);
            }
            var after = SqliteRuntime.GetDiagnostics();
            Assert.Greater(after.StatementCacheHits, before.StatementCacheHits);
            Assert.Greater(after.StatementCacheMisses, before.StatementCacheMisses);
            Assert.Greater(after.CommittedTransactions, before.CommittedTransactions);
            Assert.Greater(after.FailedOperations, before.FailedOperations);
            Assert.Greater(after.QueueWaitNanoseconds, before.QueueWaitNanoseconds);
            Assert.Greater(after.ExecutionNanoseconds, before.ExecutionNanoseconds);
            Assert.Greater(after.CommitNanoseconds, before.CommitNanoseconds);
            Assert.AreEqual(0, after.QueuedOperations);
            Assert.AreEqual(0, after.ActiveOperations);
            Assert.AreEqual(0, after.HeldResultBytes);
            Assert.AreEqual(0, after.ParameterBytes);
            Assert.AreEqual(0, after.ReservedResultBytes);
        });
        [Test]
        public void ExecuteDeadlineRollsBackAndKeepsIndependentWorkUsable() => Run(async () =>
        {
            var before = SqliteRuntime.GetDiagnostics().TimedOutOperations;
            var error = Assert.ThrowsAsync<SqliteException>(async () => await database.ExecuteAsync(
                new SqliteCommand("INSERT INTO items(id,value) WITH RECURSIVE r(x) AS " +
                    "(VALUES(0) UNION ALL SELECT x+1 FROM r WHERE x<100000000) SELECT 1,sum(x) FROM r"),
                TimeSpan.FromMilliseconds(1)));
            Assert.AreEqual(SqliteError.Timeout, error.Error);
            Assert.AreEqual(0, (await database.QueryPageAsync(new SqliteCommand("SELECT count(*) FROM items"),
                new SqliteQueryBudget(1, 128), row => row.GetInt64(0)))[0]);
            Assert.AreEqual(1, await database.ExecuteAsync(new SqliteCommand("INSERT INTO items VALUES(1,'after-timeout',NULL)")));
            Assert.AreEqual(before + 1, SqliteRuntime.GetDiagnostics().TimedOutOperations);
        });
        [Test]
        public void NativeErrorsPreserveUtf8Identifiers() => Run(
            () =>
            {
                var error = Assert.ThrowsAsync<SqliteException>(
                    async () => await database.ExecuteAsync(new SqliteCommand("DELETE FROM 不存在的表")));
                StringAssert.Contains("不存在的表", error.Message);
                return Task.CompletedTask;
            });
        [Test]
        public void ParametersAndCommittedReadsRoundTrip() => Run(
            async () =>
            {
                var bytes = new byte[] { 0, 128, 255 };
                Assert.AreEqual(1, await database.ExecuteAsync(new SqliteCommand(
                                       "INSERT INTO items VALUES(?,?,?)", 1L, "照片'😀\0tail", bytes)));
                var page = await database.QueryPageAsync(new SqliteCommand("SELECT id,value,data FROM items"),
                                                         new SqliteQueryBudget(10, 4096),
                                                         r => (r.GetInt64(0), r.GetString(1), (byte[])r[2]));
                Assert.AreEqual(1, page.Count);
                Assert.AreEqual("照片'😀\0tail", page[0].Item2);
                CollectionAssert.AreEqual(bytes, page[0].Item3);
            });
        [Test]
        public void ConditionFailureRollsBackWholeBatch() => Run(
            async () =>
            {
                var error = Assert.ThrowsAsync<SqliteException>(
                    async () =>
                    {
                        using (await database.ExecuteTransactionAsync(new SqliteBatch(
                            new SqliteQueryBudget(10, 4096),
                            new SqliteCommand("INSERT INTO items VALUES(1,'first',NULL)"),
                            new SqliteCommand("UPDATE items SET value='second'").ExpectAffectedRows(2))))
                        {
                        }
                    });
                Assert.AreEqual(SqliteError.ConditionFailed, error.Error);
                var counts = await database.QueryPageAsync(new SqliteCommand("SELECT count(*) FROM items"),
                                                           new SqliteQueryBudget(1, 128), r => r.GetInt64(0));
                Assert.AreEqual(0, counts[0]);
            });
        [Test]
        public void MappingFailurePreservesExceptionAndReleasesPage() => Run(
            async () =>
            {
                var original = new InvalidOperationException("mapping sentinel");
                var error = Assert.ThrowsAsync<InvalidOperationException>(
                    async () => await database.QueryPageAsync<int>(
                        new SqliteCommand("SELECT 1"), new SqliteQueryBudget(1, 128), r => throw original));
                Assert.AreSame(original, error);
                Assert.AreEqual(
                    7, (await database.QueryPageAsync(new SqliteCommand("SELECT 7"),
                                                      new SqliteQueryBudget(1, 128), r => r.GetInt64(0)))[0]);
            });
        [Test]
        public void CancelDoesNotInterruptNextOperation() => Run(
            async () =>
            {
                using (var cancel = new CancellationTokenSource())
                {
                    var operation = database.QueryPageAsync(
                        new SqliteCommand("WITH RECURSIVE r(x) AS (VALUES(0) UNION ALL SELECT x+1 FROM r " +
                                          "WHERE x<100000000) SELECT sum(x) FROM r"),
                        new SqliteQueryBudget(1, 128), r => r.GetInt64(0), cancel.Token);
                    cancel.Cancel();
                    Assert.CatchAsync<OperationCanceledException>(async () => await operation);
                }
                Assert.AreEqual(
                    8, (await database.QueryPageAsync(new SqliteCommand("SELECT 8"),
                                                      new SqliteQueryBudget(1, 128), r => r.GetInt64(0)))[0]);
            });
        [Test]
        public void CommittedResultOutlivesConnection() => Run(
            async () =>
            {
                using (var result = await database.ExecuteTransactionAsync(new SqliteBatch(
                           new SqliteQueryBudget(1, 1024),
                           new SqliteCommand("INSERT INTO items VALUES(1,'saved',NULL) RETURNING id"))))
                {
                    await database.CloseAsync();
                    Assert.AreEqual(1, result.MapRows(0, r => r.GetInt64(0))[0]);
                }
                var reopened = await SqliteDatabase.OpenAsync(new SqliteOpenOptions(
                    Path.Combine(directory, "store.sqlite"), SqliteOpenMode.OpenExistingReadOnly));
                try
                {
                    Assert.AreEqual("saved", (await reopened.QueryPageAsync(
                                                 new SqliteCommand("SELECT value FROM items"),
                                                 new SqliteQueryBudget(1, 1024), r => r.GetString(0)))[0]);
                }
                finally
                {
                    await reopened.CloseAsync();
                }
            });
        [Test]
        public void ResultOverflowRollsBackReturningWrite() =>
            Run(async () =>
                {
                    var error = Assert.ThrowsAsync<SqliteException>(
                        async () =>
                        {
                            using (await database.ExecuteTransactionAsync(new SqliteBatch(
                                new SqliteQueryBudget(1, 128),
                                new SqliteCommand(
                                    "INSERT INTO items VALUES(1,'x',NULL) RETURNING randomblob(4096)"))))
                            {
                            }
                        });
                    Assert.AreEqual(SqliteError.ResultLimit, error.Error);
                    Assert.AreEqual(1, await database.ExecuteAsync(
                                           new SqliteCommand("INSERT INTO items VALUES(1,'ok',NULL)")));
                });
        [Test]
        public void MissingExistingStoreIsNotCreated() =>
            Run(() =>
                {
                    var path = Path.Combine(directory, "missing.sqlite");
                    Assert.ThrowsAsync<SqliteException>(
                        async () => await SqliteDatabase.OpenAsync(
                            new SqliteOpenOptions(path, SqliteOpenMode.OpenExistingReadWrite)));
                    Assert.IsFalse(File.Exists(path));
                    return Task.CompletedTask;
                });

        [Test]
        public void SnapshotIsStandaloneAndNeverOverwrites() => Run(
            async () =>
            {
                await database.ExecuteAsync(new SqliteCommand("INSERT INTO items VALUES(1,'snapshot',NULL)"));
                string path = Path.Combine(directory, "snapshot.sqlite");
                await database.CreateSnapshotAsync(new SqliteSnapshotOptions(path));
                var read = await SqliteDatabase.OpenAsync(
                    new SqliteOpenOptions(path, SqliteOpenMode.OpenExistingReadOnly));
                try
                {
                    Assert.AreEqual("snapshot", (await read.QueryPageAsync(
                                                    new SqliteCommand("SELECT value FROM items"),
                                                    new SqliteQueryBudget(), r => r.GetString(0)))[0]);
                }
                finally
                {
                    await read.CloseAsync();
                }
                var before = File.ReadAllBytes(path);
                Assert.ThrowsAsync<SqliteException>(
                    async () => await database.CreateSnapshotAsync(new SqliteSnapshotOptions(path)));
                CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
            });
        [Test]
        public void CachedQueryAdaptsToChangedSchema() => Run(
            async () =>
            {
                await database.ExecuteAsync(new SqliteCommand("INSERT INTO items VALUES(1,'before',NULL)"));
                Assert.AreEqual(3, (await database.QueryPageAsync(new SqliteCommand("SELECT * FROM items"),
                                                                  new SqliteQueryBudget(), r => r.Count))[0]);
                await database.ExecuteAsync(
                    new SqliteCommand("ALTER TABLE items ADD COLUMN revision INTEGER NOT NULL DEFAULT 7"));
                Assert.AreEqual(4, (await database.QueryPageAsync(new SqliteCommand("SELECT * FROM items"),
                                                                  new SqliteQueryBudget(), r => r.Count))[0]);
            });
        [Test]
        public void DuplicateOwnerIsRejectedWithoutBreakingOriginal() => Run(
            async () =>
            {
                var error = Assert.ThrowsAsync<SqliteException>(
                    async () => await SqliteDatabase.OpenAsync(new SqliteOpenOptions(
                        Path.Combine(directory, "store.sqlite"), SqliteOpenMode.OpenExistingReadWrite)));
                Assert.AreEqual(SqliteError.InvalidState, error.Error);
                Assert.AreEqual(1, await database.ExecuteAsync(
                                       new SqliteCommand("INSERT INTO items VALUES(1,'still-open',NULL)")));
            });
        [Test]
        public void DiskWatermarkStopsWritesButKeepsReadsAndMaintenanceAvailable() => Run(
            async () =>
            {
                await database.CloseAsync();
                database = await SqliteDatabase.OpenAsync(new SqliteOpenOptions(
                    Path.Combine(directory, "store.sqlite"), SqliteOpenMode.OpenExistingReadWrite,
                    minFreeDiskBytes: long.MaxValue));
                var error = Assert.ThrowsAsync<SqliteException>(
                    async () => await database.ExecuteAsync(
                        new SqliteCommand("INSERT INTO items VALUES(1,'blocked',NULL)")));
                Assert.AreEqual(SqliteError.CapacityExceeded, error.Error);
                Assert.AreEqual(
                    0, (await database.QueryPageAsync(new SqliteCommand("SELECT count(*) FROM items"),
                                                      new SqliteQueryBudget(), row => row.GetInt64(0)))[0]);
                var storage = await database.CheckpointAsync(SqliteCheckpointMode.Truncate);
                Assert.IsFalse(storage.CheckpointBlocked);
                Assert.AreEqual(0, storage.WalBytes);
                Assert.Greater(storage.DatabaseBytes, 0);
                Assert.Greater((await database.GetStorageInfoAsync()).AvailableDiskBytes, 0);
            });
        [Test]
        public void CloseWaitsForMappingAndRejectsNewWork() => Run(
            async () =>
            {
                using (var started = new ManualResetEventSlim()) using (var release =
                                                                            new ManualResetEventSlim())
                {
                    var query =
                        database.QueryPageAsync(new SqliteCommand("SELECT 1"), new SqliteQueryBudget(),
                                                r =>
                                                {
                                                    started.Set();
                                                    if (!release.Wait(5000))
                                                        throw new TimeoutException();
                                                    return r.GetInt64(0);
                                                });
                    Assert.IsTrue(started.Wait(3000));
                    var closing = database.CloseAsync();
                    try
                    {
                        Assert.IsFalse(closing.IsCompleted);
                        Assert.ThrowsAsync<ObjectDisposedException>(
                            async () => await database.ExecuteAsync(new SqliteCommand("DELETE FROM items")));
                    }
                    finally
                    {
                        release.Set();
                    }
                    await query;
                    await closing;
                }
            });
    }
}
