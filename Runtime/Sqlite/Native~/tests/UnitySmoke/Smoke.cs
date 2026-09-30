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
            }
            finally
            {
                await db.CloseAsync();
            }
            await SqliteRuntime.ShutdownAsync();
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
