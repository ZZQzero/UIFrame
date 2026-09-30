using System;
using System.IO;
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
            if ((await inventory.QueryReceiptAsync("reward-1")).Balance != 100)
                throw new Exception("Receipt was rewritten");
        }
        finally
        {
            await db.CloseAsync();
        }
        await SqliteRuntime.ShutdownAsync();
        Directory.Delete(directory, true);
        Console.WriteLine("Game samples passed: save roundtrip, missing slot, duplicate reward, " +
                          "insufficient balance, immutable receipt.");
    }
}
