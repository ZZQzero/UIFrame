using System;
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
                               new SqliteCommand("PRAGMA user_version=1"),
                               new SqliteCommand("CREATE TABLE saves(slot TEXT PRIMARY KEY, revision " +
                                                 "INTEGER NOT NULL, payload BLOB NOT NULL) STRICT"),
                               new SqliteCommand("CREATE TABLE inventory(item TEXT PRIMARY KEY, quantity " +
                                                 "INTEGER NOT NULL CHECK(quantity>=0)) STRICT"),
                               new SqliteCommand(
                                   "CREATE TABLE receipts(operation TEXT PRIMARY KEY, item TEXT NOT NULL, " +
                                   "delta INTEGER NOT NULL, balance INTEGER NOT NULL) STRICT")),
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
            if (application[0] != 1430669127 || version[0] != 1)
                throw new InvalidOperationException("Unexpected game database identity or schema.");
        }
    }
    public sealed class SaveRepository
    {
        readonly SqliteDatabase database;
        public SaveRepository(SqliteDatabase database) => this.database =
            database ?? throw new ArgumentNullException(nameof(database));
        public Task SaveAsync(string slot, long revision, byte[] payload, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(slot) || revision < 0 || payload == null)
                throw new ArgumentException("A slot, nonnegative revision and bounded payload are required.");
            return database.ExecuteAsync(
                new SqliteCommand("INSERT INTO saves VALUES(?,?,?) ON CONFLICT(slot) DO UPDATE SET " +
                                  "revision=excluded.revision,payload=excluded.payload",
                                  slot, revision, payload),
                token);
        }
        public async Task<byte[]> LoadAsync(string slot, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(slot))
                throw new ArgumentException("A slot is required.");
            var rows = await database
                           .QueryPageAsync(new SqliteCommand("SELECT payload FROM saves WHERE slot=?", slot),
                                           new SqliteQueryBudget(1), row => (byte[])row[0], token)
                           .ConfigureAwait(false);
            return rows.Count == 0 ? null : rows[0];
        }
    }
    public sealed class InventoryRepository
    {
        readonly SqliteDatabase database;
        public InventoryRepository(SqliteDatabase database) => this.database =
            database ?? throw new ArgumentNullException(nameof(database));
        // An operation ID is provided by the business action, not generated again on every attempt.
        // Duplicate IDs fail atomically; QueryReceiptAsync determines an earlier commit after lost delivery.
        public async Task<long> ApplyAsync(string operationId, string item, long delta,
                                           CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(operationId) || string.IsNullOrWhiteSpace(item) || delta == 0)
                throw new ArgumentException("An operation ID, item and nonzero delta are required.");
            using (var result =
                       await database
                           .ExecuteTransactionAsync(
                               new SqliteBatch(
                                   new SqliteQueryBudget(1, 1024),
                                   new SqliteCommand("INSERT INTO inventory(item,quantity) VALUES(?,0) ON " +
                                                     "CONFLICT(item) DO NOTHING",
                                                     item),
                                   new SqliteCommand("UPDATE inventory SET quantity=quantity+? WHERE item=?",
                                                     delta, item)
                                       .ExpectAffectedRows(1),
                                   new SqliteCommand("INSERT INTO receipts SELECT ?,item,?,quantity FROM " +
                                                     "inventory WHERE item=? RETURNING balance",
                                                     operationId, delta, item)
                                       .ExpectAffectedRows(1)),
                               token)
                           .ConfigureAwait(false)) return result.MapRows(2, row => row.GetInt64(0))[0];
        }
        public async Task<Receipt> QueryReceiptAsync(string operationId, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentException("An operation ID is required.");
            var rows = await database
                           .QueryPageAsync(
                               new SqliteCommand("SELECT item,delta,balance FROM receipts WHERE operation=?",
                                                 operationId),
                               new SqliteQueryBudget(1, 1024),
                               row => new Receipt(row.GetString(0), row.GetInt64(1), row.GetInt64(2)), token)
                           .ConfigureAwait(false);
            return rows.Count == 0 ? null : rows[0];
        }
        public sealed class Receipt
        {
            public string Item { get; }
            public long Delta { get; }
            public long Balance { get; }
            internal Receipt(string item, long delta, long balance)
            {
                Item = item;
                Delta = delta;
                Balance = balance;
            }
        }
    }
}
