using System;
using System.IO;
using UnityEngine;
using UIFrame.Sqlite;
public static class Smoke
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static async void Run()
    {
        try
        {
            string path =
                Path.Combine(Application.persistentDataPath, "照片 😀-" + Guid.NewGuid().ToString("N") + ".sqlite");
            var db = await SqliteDatabase.OpenAsync(new SqliteOpenOptions(path, SqliteOpenMode.CreateNew));
            try
            {
                await db.ExecuteAsync(new SqliteCommand("CREATE TABLE records(value TEXT)"));
                await db.ExecuteAsync(new SqliteCommand("INSERT INTO records VALUES(?)", "照片😀"));
                var rows = await db.QueryPageAsync(new SqliteCommand("SELECT value FROM records"),
                                                   new SqliteQueryBudget(), row => row.GetString(0));
                if (rows.Count != 1 || rows[0] != "照片😀")
                    throw new Exception("Roundtrip failed");
                try
                {
                    await db.ExecuteAsync(new SqliteCommand("DELETE FROM missing_smoke_table"));
                    throw new Exception("Invalid SQL succeeded");
                }
                catch (SqliteException error)
                {
                    if (error.Phase != SqliteExecutionPhase.Prepare)
                        throw new Exception("Native error phase did not survive marshalling", error);
                }
            }
            finally
            {
                await db.CloseAsync();
            }
            var reopened = await SqliteDatabase.OpenAsync(new SqliteOpenOptions(path, SqliteOpenMode.OpenExistingReadOnly));
            try
            {
                var rows = await reopened.QueryPageAsync(new SqliteCommand("SELECT value FROM records"),
                    new SqliteQueryBudget(), row => row.GetString(0));
                if (rows.Count != 1 || rows[0] != "照片😀") throw new Exception("Committed data was not retained");
            }
            finally { await reopened.CloseAsync(); }
            await SqliteRuntime.ShutdownAsync();
            var diagnostics = SqliteRuntime.GetDiagnostics();
            if (diagnostics.OpenDatabases != 0 || diagnostics.OutstandingOperations != 0 || diagnostics.ReservedBytes != 0 ||
                diagnostics.CommittedTransactions == 0 || diagnostics.FailedOperations == 0)
                throw new Exception("Diagnostics or native lifetime contract failed");
            Debug.Log("UIFRAME_SQLITE_DEVICE_SMOKE_PASSED " + SqliteRuntime.BuildId);
            Application.Quit(0);
        }
        catch (Exception error)
        {
            Debug.LogException(error);
            Application.Quit(1);
        }
    }
}
