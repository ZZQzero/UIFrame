using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UIFrame.Sqlite;

namespace Game.Storage.Sample
{
    // The application owns this database. These repositories do not close shared storage.
    public static class GameSchema
    {
        public static async Task CreateAsync(SqliteDatabase database, CancellationToken token = default)
        {
            using (await database
                       .ExecuteTransactionAsync(
                           new SqliteBatch(
                               new SqliteQueryBudget(), new SqliteCommand("PRAGMA application_id=1430669127"),
                               new SqliteCommand("PRAGMA user_version=2"),
                               new SqliteCommand("CREATE TABLE saves(slot TEXT PRIMARY KEY, revision " +
                                                 "INTEGER NOT NULL, payload BLOB NOT NULL) STRICT"),
                               new SqliteCommand("CREATE TABLE inventory(item TEXT PRIMARY KEY, quantity " +
                                                 "INTEGER NOT NULL CHECK(quantity>=0)) STRICT"),
                               new SqliteCommand("CREATE TABLE inventory_state(singleton INTEGER PRIMARY KEY CHECK(singleton=1), revision INTEGER NOT NULL CHECK(revision>=0)) STRICT"),
                               new SqliteCommand("INSERT INTO inventory_state VALUES(1,0)"),
                               new SqliteCommand("CREATE TABLE receipts(operation TEXT PRIMARY KEY, revision INTEGER NOT NULL UNIQUE CHECK(revision>0)) STRICT"),
                               new SqliteCommand("CREATE TABLE receipt_items(operation TEXT NOT NULL REFERENCES receipts(operation), item TEXT NOT NULL, delta INTEGER NOT NULL, balance INTEGER NOT NULL CHECK(balance>=0), PRIMARY KEY(operation,item)) WITHOUT ROWID, STRICT")),
                           token)
                       .ConfigureAwait(false))
            {
            }
        }
        public static async Task ValidateAsync(SqliteDatabase database, CancellationToken token = default)
        {
            var application =
                await database
                    .QueryPageAsync(new SqliteCommand("PRAGMA application_id"), new SqliteQueryBudget(1, 128),
                                    row => row.GetInt64(0), token)
                    .ConfigureAwait(false);
            var version = await database
                              .QueryPageAsync(new SqliteCommand("PRAGMA user_version"),
                                              new SqliteQueryBudget(1, 128), row => row.GetInt64(0), token)
                              .ConfigureAwait(false);
            if (application[0] != 1430669127 || version[0] != 2)
                throw new InvalidOperationException("Unexpected game database identity or schema.");
        }
    }
    public sealed class SaveRepository
    {
        // Reserve room for the revision, column names and result framing in the
        // core's 1 MiB page. Every accepted payload must fit LoadVersionedAsync.
        public const int MaximumPayloadBytes = 1024 * 1024 - 64;
        readonly SqliteDatabase database;
        public SaveRepository(SqliteDatabase database) => this.database =
            database ?? throw new ArgumentNullException(nameof(database));
        public Task SaveAsync(string slot, long revision, byte[] payload, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(slot) || revision < 0 || payload == null)
                throw new ArgumentException("A slot, nonnegative revision and bounded payload are required.");
            if (payload.Length > MaximumPayloadBytes)
                throw new ArgumentOutOfRangeException(nameof(payload), "The save payload must leave room for its versioned query result.");
            return database.ExecuteAsync(
                new SqliteCommand("INSERT INTO saves VALUES(?,?,?) ON CONFLICT(slot) DO UPDATE SET " +
                                  "revision=excluded.revision,payload=excluded.payload WHERE saves.revision<excluded.revision",
                                  slot, revision, payload).ExpectAffectedRows(1),
                token);
        }
        public async Task<SaveRecord> LoadVersionedAsync(string slot, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(slot)) throw new ArgumentException("A slot is required.");
            var rows = await database.QueryPageAsync(
                new SqliteCommand("SELECT revision,payload FROM saves WHERE slot=?", slot),
                new SqliteQueryBudget(1), row => new SaveRecord(row.GetInt64(0), (byte[])row[1]), token).ConfigureAwait(false);
            return rows.Count == 0 ? null : rows[0];
        }
        public async Task<byte[]> LoadAsync(string slot, CancellationToken token = default)
            => (await LoadVersionedAsync(slot, token).ConfigureAwait(false))?.Payload;
        public sealed class SaveRecord
        {
            public long Revision { get; }
            public byte[] Payload { get; }
            internal SaveRecord(long revision, byte[] payload) { Revision = revision; Payload = payload; }
        }
    }

    public sealed class InventoryChange
    {
        public string Item { get; }
        public long Delta { get; }
        public InventoryChange(string item, long delta)
        {
            InventoryRepository.ValidateId(item, nameof(item));
            if (delta == 0) throw new ArgumentOutOfRangeException(nameof(delta));
            Item = item; Delta = delta;
        }
    }
    public sealed class InventoryResult
    {
        public string Item { get; }
        public long Delta { get; }
        public long Balance { get; }
        internal InventoryResult(string item, long delta, long balance) { Item = item; Delta = delta; Balance = balance; }
    }
    public sealed class InventoryReceipt
    {
        public string OperationId { get; }
        public long Revision { get; }
        public IReadOnlyList<InventoryResult> Items { get; }
        internal InventoryReceipt(string operation, long revision, IReadOnlyList<InventoryResult> items)
        { OperationId = operation; Revision = revision; Items = new List<InventoryResult>(items).AsReadOnly(); }
    }
    public sealed class InventoryRepository
    {
        static readonly UTF8Encoding IdentityEncoding = new UTF8Encoding(false, true);
        readonly SqliteDatabase database;
        public InventoryRepository(SqliteDatabase database) => this.database =
            database ?? throw new ArgumentNullException(nameof(database));
        internal static void ValidateId(string value, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value) || value.IndexOf('\0') >= 0 || IdentityEncoding.GetByteCount(value) > 512)
                throw new ArgumentException("An identity requires 1–512 UTF-8 bytes and no NUL.", parameter);
        }
        // At most 64 members keeps all statements within one atomic 200-statement batch.
        // Duplicate operation IDs fail; callers query the immutable receipt explicitly.
        public async Task<InventoryReceipt> ApplyAsync(string operationId, IReadOnlyList<InventoryChange> changes,
            long? expectedRevision = null, CancellationToken token = default)
        {
            ValidateId(operationId, nameof(operationId));
            if (changes == null || changes.Count < 1 || changes.Count > 64) throw new ArgumentException("An operation requires 1–64 changes.", nameof(changes));
            if (expectedRevision < 0) throw new ArgumentOutOfRangeException(nameof(expectedRevision));
            var copy = new List<InventoryChange>(changes.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var change in changes)
            {
                if (change == null || !seen.Add(change.Item)) throw new ArgumentException("Each item must occur once.", nameof(changes));
                copy.Add(change);
            }
            var commands = new List<SqliteCommand>();
            commands.Add(expectedRevision.HasValue
                ? new SqliteCommand("UPDATE inventory_state SET revision=revision+1 WHERE singleton=1 AND revision=?", expectedRevision.Value).ExpectAffectedRows(1)
                : new SqliteCommand("UPDATE inventory_state SET revision=revision+1 WHERE singleton=1").ExpectAffectedRows(1));
            commands.Add(new SqliteCommand("INSERT INTO receipts SELECT ?,revision FROM inventory_state WHERE singleton=1", operationId).ExpectAffectedRows(1));
            foreach (var change in copy)
            {
                commands.Add(new SqliteCommand("INSERT INTO inventory(item,quantity) VALUES(?,0) ON CONFLICT(item) DO NOTHING", change.Item));
                commands.Add(new SqliteCommand("UPDATE inventory SET quantity=quantity+? WHERE item=?", change.Delta, change.Item).ExpectAffectedRows(1));
                commands.Add(new SqliteCommand("INSERT INTO receipt_items SELECT ?,item,?,quantity FROM inventory WHERE item=?", operationId, change.Delta, change.Item).ExpectAffectedRows(1));
            }
            int header = commands.Count;
            commands.Add(new SqliteCommand("SELECT revision FROM receipts WHERE operation=?", operationId));
            commands.Add(new SqliteCommand("SELECT item,delta,balance FROM receipt_items WHERE operation=? ORDER BY item", operationId));
            using (var result = await database.ExecuteTransactionAsync(
                new SqliteBatch(new SqliteQueryBudget(65, 64 * 1024), commands.ToArray()), token).ConfigureAwait(false))
                return new InventoryReceipt(operationId, result.MapRows(header, row => row.GetInt64(0))[0],
                    result.MapRows(header + 1, MapItem));
        }
        public async Task<long> ApplyAsync(string operationId, string item, long delta, CancellationToken token = default)
            => (await ApplyAsync(operationId, new[] { new InventoryChange(item, delta) }, null, token).ConfigureAwait(false)).Items[0].Balance;
        static InventoryResult MapItem(SqliteRow row) => new InventoryResult(row.GetString(0), row.GetInt64(1), row.GetInt64(2));
        public async Task<InventoryReceipt> QueryReceiptAsync(string operationId, CancellationToken token = default)
        {
            ValidateId(operationId, nameof(operationId));
            var rows = await database.QueryPageAsync(new SqliteCommand(
                "SELECT r.revision,i.item,i.delta,i.balance FROM receipts r JOIN receipt_items i ON i.operation=r.operation WHERE r.operation=? ORDER BY i.item", operationId),
                new SqliteQueryBudget(64, 64 * 1024), row => (revision: row.GetInt64(0), item: new InventoryResult(row.GetString(1), row.GetInt64(2), row.GetInt64(3))), token).ConfigureAwait(false);
            if (rows.Count == 0) return null;
            var items = new List<InventoryResult>(rows.Count);
            foreach (var row in rows) items.Add(row.item);
            return new InventoryReceipt(operationId, rows[0].revision, items.AsReadOnly());
        }
        internal async Task<(long revision, Dictionary<string, long> items)> LoadAsync(CancellationToken token)
        {
            var items = new Dictionary<string, long>(StringComparer.Ordinal);
            string after = ""; long? revision = null;
            for (;;)
            {
                var rows = await database.QueryPageAsync(new SqliteCommand(
                    "SELECT s.revision,i.item,i.quantity FROM inventory_state s LEFT JOIN (SELECT item,quantity FROM inventory WHERE item>? ORDER BY item LIMIT 199) i ON 1=1 WHERE s.singleton=1 ORDER BY i.item", after),
                    new SqliteQueryBudget(199, 128 * 1024), row => (revision: row.GetInt64(0), item: row.IsNull(1) ? null : row.GetString(1), quantity: row.IsNull(2) ? 0 : row.GetInt64(2)), token).ConfigureAwait(false);
                if (rows.Count == 0 || (revision.HasValue && revision.Value != rows[0].revision))
                    throw new InvalidOperationException("Inventory changed while loading; explicitly reload after the other writer completes.");
                revision = rows[0].revision;
                foreach (var row in rows) if (row.item != null) { items.Add(row.item, row.quantity); after = row.item; }
                if (rows.Count < 199) return (revision.Value, items);
            }
        }
    }

    // One session owns the game's inventory view. UI reads memory; commands are
    // serialized through commit and publication. The application owns the database.
    public sealed class InventorySession
    {
        readonly InventoryRepository repository;
        readonly SemaphoreSlim commands = new SemaphoreSlim(1, 1);
        readonly object viewGate = new object();
        Dictionary<string, long> items;
        long revision;
        public event Action<InventoryReceipt> Changed;
        InventorySession(InventoryRepository repository) { this.repository = repository; }
        public static async Task<InventorySession> OpenAsync(SqliteDatabase database, CancellationToken token = default)
        {
            var session = new InventorySession(new InventoryRepository(database));
            await session.ReloadAsync(token).ConfigureAwait(false);
            return session;
        }
        public long Revision { get { lock (viewGate) return revision; } }
        public long GetQuantity(string item)
        {
            InventoryRepository.ValidateId(item, nameof(item));
            lock (viewGate) return items.TryGetValue(item, out var quantity) ? quantity : 0;
        }
        public async Task ReloadAsync(CancellationToken token = default)
        {
            await commands.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var loaded = await repository.LoadAsync(token).ConfigureAwait(false);
                lock (viewGate) { items = loaded.items; revision = loaded.revision; }
            }
            finally { commands.Release(); }
        }
        public async Task<InventoryReceipt> ApplyAsync(string operationId, IReadOnlyList<InventoryChange> changes, CancellationToken token = default)
        {
            // Capture caller-owned collections before waiting behind another command.
            InventoryRepository.ValidateId(operationId, nameof(operationId));
            if (changes == null || changes.Count < 1 || changes.Count > 64)
                throw new ArgumentException("An operation requires 1–64 changes.", nameof(changes));
            var copy = new List<InventoryChange>(changes);
            await commands.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var receipt = await repository.ApplyAsync(operationId, copy, revision, token).ConfigureAwait(false);
                lock (viewGate)
                {
                    foreach (var result in receipt.Items) items[result.Item] = result.Balance;
                    revision = receipt.Revision;
                }
                // Notification follows commit and memory publication. A callback
                // failure propagates; it cannot roll back this committed receipt.
                Changed?.Invoke(receipt);
                return receipt;
            }
            finally { commands.Release(); }
        }
        public Task<InventoryReceipt> PurchaseAsync(string operationId, string currency, long cost, string item, long quantity, CancellationToken token = default)
        {
            if (cost <= 0) throw new ArgumentOutOfRangeException(nameof(cost));
            if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
            return ApplyAsync(operationId, new[] { new InventoryChange(currency, -cost), new InventoryChange(item, quantity) }, token);
        }
        // Crafting and rewards supply their complete resource deltas to ApplyAsync.
        public Task<InventoryReceipt> QueryReceiptAsync(string operationId, CancellationToken token = default)
            => repository.QueryReceiptAsync(operationId, token);
    }
}
