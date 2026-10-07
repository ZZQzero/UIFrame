using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Game.Storage.Sample;
using UIFrame.Sqlite;

static class SampleRunner
{
    static int Main()
    {
        Run().GetAwaiter().GetResult();
        return 0;
    }
    static async Task Run()
    {
        string directory =
            Path.Combine(Path.GetTempPath(), "uiframe-game-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var db = await SqliteDatabase.OpenAsync(
            new SqliteOpenOptions(Path.Combine(directory, "game.sqlite"), SqliteOpenMode.CreateNew));
        try
        {
            await GameSchema.CreateAsync(db);
            await GameSchema.ValidateAsync(db);
            await db.ExecuteAsync(new SqliteCommand("PRAGMA user_version=1"));
            try { await GameSchema.ValidateAsync(db); throw new Exception("Old sample schema accepted."); }
            catch (InvalidOperationException) {}
            await db.ExecuteAsync(new SqliteCommand("PRAGMA user_version=2"));
            var saves = new SaveRepository(db);
            if (await saves.LoadAsync("missing") != null)
                throw new Exception("Missing save fabricated");
            await saves.SaveAsync("slot-1", 1, new byte[] { 1, 2, 3 });
            if ((await saves.LoadAsync("slot-1"))[2] != 3)
                throw new Exception("Save lost");
            var inventory = new InventoryRepository(db);
            if (await inventory.ApplyAsync("reward-1", "gold", 100) != 100)
                throw new Exception("Reward failed");
            bool duplicate = false, overdraft = false;
            try
            {
                await inventory.ApplyAsync("reward-1", "gold", 100);
            }
            catch (SqliteException)
            {
                duplicate = true;
            }
            try
            {
                await inventory.ApplyAsync("spend-1", "gold", -101);
            }
            catch (SqliteException)
            {
                overdraft = true;
            }
            if (!duplicate || !overdraft || await inventory.QueryReceiptAsync("spend-1") != null)
                throw new Exception("Transaction did not reject duplicate/overdraft atomically");
            if (await inventory.ApplyAsync("spend-2", "gold", -25) != 75)
                throw new Exception("Failed writes changed balance");
            if ((await inventory.QueryReceiptAsync("reward-1")).Items[0].Balance != 100)
                throw new Exception("Receipt was rewritten");
            await VerifyVersionedSaves(saves);
            await VerifySavePayloadBoundary(saves);
            await VerifyGameCommands(db, inventory);
            var beforeReopen = await InventorySession.OpenAsync(db);
            long quantity = beforeReopen.GetQuantity("gold"), revision = beforeReopen.Revision;
            await db.CloseAsync();
            db = await SqliteDatabase.OpenAsync(new SqliteOpenOptions(Path.Combine(directory, "game.sqlite"), SqliteOpenMode.OpenExistingReadWrite));
            await GameSchema.ValidateAsync(db);
            var reopened = await InventorySession.OpenAsync(db);
            Require(reopened.Revision == revision && reopened.GetQuantity("gold") == quantity && reopened.GetQuantity("item-255") == 1,
                    "Inventory did not survive closing and reopening the database.");
            Require((await reopened.QueryReceiptAsync("purchase-1")).Items.Count == 2, "Purchase receipt did not survive reopening.");
            await Reject(async () => { await reopened.PurchaseAsync("purchase-1", "gold", 25, "potion", 2); });
            Require(reopened.Revision == revision && reopened.GetQuantity("gold") == quantity, "Reopened duplicate changed inventory.");
            var savedAgain = await new SaveRepository(db).LoadVersionedAsync("boundary");
            Require(savedAgain.Revision == 1 && savedAgain.Payload.Length == SaveRepository.MaximumPayloadBytes && savedAgain.Payload[0] == 17,
                    "Bounded versioned save did not survive reopening.");
        }
        finally
        {
            await db.CloseAsync();
        }
        await SqliteRuntime.ShutdownAsync();
        Directory.Delete(directory, true);
        Console.WriteLine("Game samples passed: save roundtrip, missing slot, duplicate reward, " +
                          "insufficient balance, immutable receipt, versioned and bounded saves, atomic purchases/crafting, memory publication, concurrent commands, conflicts, cancellation, queued input capture, pagination and reopening.");
    }
    static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
    static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (SqliteException) { return; }
        throw new Exception("Expected transaction rejection.");
    }
    static async Task VerifyVersionedSaves(SaveRepository saves)
    {
        await saves.SaveAsync("ordered", 2, new byte[] { 2 });
        await Reject(() => saves.SaveAsync("ordered", 1, new byte[] { 1 }));
        await Reject(() => saves.SaveAsync("ordered", 2, new byte[] { 9 }));
        var stored = await saves.LoadVersionedAsync("ordered");
        Require(stored.Revision == 2 && stored.Payload[0] == 2, "Stale save replaced committed data.");
        await saves.SaveAsync("ordered", 3, new byte[] { 3 });
        Require((await saves.LoadVersionedAsync("ordered")).Revision == 3, "Valid newer save rejected.");
    }
    static async Task VerifySavePayloadBoundary(SaveRepository saves)
    {
        const int maximum = 1024 * 1024 - 64;
        var payload = new byte[maximum]; payload[0] = 17; payload[maximum - 1] = 29;
        await saves.SaveAsync("boundary", 1, payload);
        var loaded = await saves.LoadVersionedAsync("boundary");
        Require(loaded.Revision == 1 && loaded.Payload.Length == maximum && loaded.Payload[0] == 17 && loaded.Payload[maximum - 1] == 29,
                "Maximum supported save did not roundtrip.");
        foreach (int size in new[] { 1024 * 1024 - 48, maximum + 1, 1024 * 1024 })
        {
            try { await saves.SaveAsync("boundary", 2, new byte[size]); throw new Exception("Oversized save was accepted and could replace a readable save."); }
            catch (ArgumentOutOfRangeException) {}
            loaded = await saves.LoadVersionedAsync("boundary");
            Require(loaded.Revision == 1 && loaded.Payload[maximum - 1] == 29, "Rejected save changed persisted data.");
        }
        await saves.SaveAsync("empty", 0, Array.Empty<byte>());
        Require((await saves.LoadAsync("empty")).Length == 0, "Empty valid save was rejected.");
    }
    static async Task VerifyGameCommands(SqliteDatabase db, InventoryRepository repository)
    {
        var session = await InventorySession.OpenAsync(db);
        int notifications = 0;
        Action<InventoryReceipt> observe = receipt => {
            Require(session.Revision == receipt.Revision, "Event preceded memory publication.");
            foreach (var item in receipt.Items) Require(session.GetQuantity(item.Item) == item.Balance, "Event saw old balance.");
            notifications++;
        };
        session.Changed += observe;
        var purchase = await session.PurchaseAsync("purchase-1", "gold", 25, "potion", 2);
        try { ((IList<InventoryResult>)purchase.Items).Clear(); throw new Exception("Receipt results were mutable."); }
        catch (NotSupportedException) {}
        Require(session.GetQuantity("gold") == 50 && session.GetQuantity("potion") == 2, "Purchase state incorrect.");
        await Reject(async () => { await session.PurchaseAsync("purchase-1", "gold", 25, "potion", 2); });
        Require(session.Revision == purchase.Revision && notifications == 1, "Failed command changed view or notification.");
        await Reject(async () => { await session.ApplyAsync("bad-craft", new[] {
            new InventoryChange("sword", 1), new InventoryChange("potion", -3) }); });
        Require(session.GetQuantity("sword") == 0 && session.GetQuantity("potion") == 2 &&
                await session.QueryReceiptAsync("bad-craft") == null, "Failed craft partially committed.");
        await session.ApplyAsync("craft", new[] { new InventoryChange("potion", -1), new InventoryChange("sword", 1) });
        Require(session.GetQuantity("potion") == 1 && session.GetQuantity("sword") == 1, "Craft failed.");
        var concurrent = new List<Task<InventoryReceipt>>();
        for (int i = 0; i < 5; i++) concurrent.Add(session.PurchaseAsync("parallel-" + i, "gold", 5, "potion", 1));
        await Task.WhenAll(concurrent);
        Require(session.GetQuantity("gold") == 25 && session.GetQuantity("potion") == 6, "Concurrent command publication lost updates.");

        var other = await InventorySession.OpenAsync(db);
        await session.ApplyAsync("first-writer", new[] { new InventoryChange("gold", 1) });
        await Reject(async () => { await other.ApplyAsync("stale-writer", new[] { new InventoryChange("gold", 10) }); });
        Require(await other.QueryReceiptAsync("stale-writer") == null && session.GetQuantity("gold") == 26, "Stale view overwrote newer inventory.");
        await other.ReloadAsync();
        await other.ApplyAsync("refreshed-writer", new[] { new InventoryChange("gold", 1) });
        await session.ReloadAsync();
        Require(session.GetQuantity("gold") == 27, "Explicit reload did not restore writable view.");

        var notificationError = new InvalidOperationException("notification failure");
        Action<InventoryReceipt> fail = receipt => { throw notificationError; };
        session.Changed += fail;
        try { await session.ApplyAsync("committed-event-failure", new[] { new InventoryChange("gold", 1) }); throw new Exception("Event error swallowed."); }
        catch (InvalidOperationException error) { Require(ReferenceEquals(error, notificationError), "Event error identity changed."); }
        session.Changed -= fail;
        Require(session.GetQuantity("gold") == 28 && await session.QueryReceiptAsync("committed-event-failure") != null, "Event failure rolled back committed state.");
        await session.ApplyAsync("after-event-failure", new[] { new InventoryChange("gold", 1) });

        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<InventoryReceipt> captured = null;
        using (var release = new ManualResetEventSlim())
        using (var canceled = new CancellationTokenSource())
        {
            Action<InventoryReceipt> hold = receipt => { if (receipt.OperationId == "held") { entered.SetResult(true); release.Wait(); } };
            session.Changed += hold;
            var first = session.ApplyAsync("held", new[] { new InventoryChange("gold", 1) });
            try
            {
                await entered.Task;
                var queued = session.ApplyAsync("canceled", new[] { new InventoryChange("gold", 1) }, canceled.Token);
                canceled.Cancel();
                try { await queued; throw new Exception("Queued cancellation ignored."); }
                catch (OperationCanceledException) {}
                var supplied = new List<InventoryChange> { new InventoryChange("captured", 2) };
                captured = session.ApplyAsync("captured-input", supplied);
                supplied.Clear(); supplied.Add(new InventoryChange("substituted", 99));
            }
            finally { release.Set(); await first; session.Changed -= hold; }
        }
        Require(await session.QueryReceiptAsync("canceled") == null, "Canceled queued command committed.");
        await captured;
        Require(session.GetQuantity("captured") == 2 && session.GetQuantity("substituted") == 0 &&
                (await session.QueryReceiptAsync("captured-input")).Items[0].Item == "captured", "Queued command used the caller's later mutation.");

        for (int batch = 0; batch < 4; batch++)
        {
            var changes = new List<InventoryChange>();
            for (int i = 0; i < 64; i++) changes.Add(new InventoryChange("item-" + (batch * 64 + i).ToString("D3"), 1));
            await session.ApplyAsync("page-" + batch, changes);
        }
        var reopened = await InventorySession.OpenAsync(db);
        Require(reopened.GetQuantity("item-255") == 1 && reopened.Revision == session.Revision, "Inventory page load lost tail or revision.");
        var maximumIds = new List<InventoryChange>();
        for (int i = 0; i < 64; i++) maximumIds.Add(new InventoryChange(new string('x', 509) + i.ToString("D3"), 1));
        var maximumReceipt = await session.ApplyAsync(new string('r', 512), maximumIds);
        Require(maximumReceipt.Items.Count == 64 && (await session.QueryReceiptAsync(maximumReceipt.OperationId)).Items.Count == 64,
                "Valid 64-item command with maximum IDs exceeded the result budget.");
        await session.ReloadAsync();
        Require(session.GetQuantity(maximumIds[63].Item) == 1, "Reload lost maximum-sized item IDs.");
        try { new InventoryChange(new string('x', 513), 1); throw new Exception("Oversized ID accepted."); }
        catch (ArgumentException) {}
        var tooMany = new List<InventoryChange>();
        for (int i = 0; i < 65; i++) tooMany.Add(new InventoryChange("limit-" + i, 1));
        try { await session.ApplyAsync("too-many", tooMany); throw new Exception("Oversized atomic command accepted."); }
        catch (ArgumentException) {}
        try { await session.ApplyAsync("duplicate-item", new[] { new InventoryChange("gold", 1), new InventoryChange("gold", 2) }); throw new Exception("Duplicate item accepted."); }
        catch (ArgumentException) {}
        Require(await session.QueryReceiptAsync("too-many") == null && await session.QueryReceiptAsync("duplicate-item") == null, "Invalid batch left a receipt.");
        await session.ApplyAsync("max-int", new[] { new InventoryChange("huge", long.MaxValue) });
        await Reject(async () => { await session.ApplyAsync("overflow", new[] { new InventoryChange("huge", 1) }); });
        Require(session.GetQuantity("huge") == long.MaxValue, "Overflow changed memory.");
        foreach (string invalid in new[] { "\uD800", "\uDC00", "a\0b", " ", new string('\u4E2D', 171) })
        {
            try { new InventoryChange(invalid, 1); throw new Exception("Invalid identity accepted by a change."); }
            catch (ArgumentException) {}
            try { session.GetQuantity(invalid); throw new Exception("Invalid identity was reported as a missing item."); }
            catch (ArgumentException) {}
            try { await session.QueryReceiptAsync(invalid); throw new Exception("Invalid operation identity accepted."); }
            catch (ArgumentException) {}
        }
        string unicodeItem = new string('\u4E2D', 170) + "ab";
        await session.ApplyAsync("unicode-item", new[] { new InventoryChange(unicodeItem, 1) });
        await session.ReloadAsync();
        Require(session.GetQuantity(unicodeItem) == 1 && (await session.QueryReceiptAsync("unicode-item")).Items[0].Item == unicodeItem,
                "Valid 512-byte UTF-8 identity did not roundtrip.");
        session.Changed -= observe;
    }

}
