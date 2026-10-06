using System;
using System.Collections;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Game.Media;
using NUnit.Framework;
using UIFrame.Sqlite;
using UnityEngine.TestTools;

namespace UIFrame.Regression
{
    public sealed class MediaLibrarySizeTests
    {
        [UnityTest] public IEnumerator SizeSurvivesPagingChangesAndVersionOneUpgrade() => UniTask.ToCoroutine(async () => {
            string root=Path.Combine(Path.GetTempPath(),"uiframe-library-size-"+Guid.NewGuid().ToString("N"));
            string photos=Path.Combine(root,"photos"),path=Path.Combine(root,"library.sqlite");
            Directory.CreateDirectory(photos);File.WriteAllBytes(Path.Combine(photos,"photo.jpg"),new byte[17]);
            var scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,photos);
            ImageLibraryIndex library=null;ImageLibraryIndex.WatchHandle observation=null;
            try {
                library=await ImageLibraryIndex.OpenAsync(path);
                observation=library.Watch(scope,_=>{});
                await library.RefreshAsync(scope);
                Assert.AreEqual(17,(await library.QueryAsync(scope)).Items.Single().ByteCount);
                var position=await library.GetPositionAsync(scope);
                File.WriteAllBytes(Path.Combine(photos,"photo.jpg"),new byte[23]);
                await library.RefreshAsync(scope,completeReconciliation:true);
                var changes=await library.ReadChangesAsync(scope,position);
                Assert.AreEqual(23,changes.Items.Last().ByteCount);
                await observation.CloseAsync();observation=null;
                await library.ShutdownAsync();library=null;
                // A real v1 catalog has the same records and identity, without the added column.
                var db=await SqliteDatabase.OpenAsync(new SqliteOpenOptions(path,SqliteOpenMode.OpenExistingReadWrite));
                try {
                    using(await db.ExecuteTransactionAsync(new SqliteBatch(new SqliteQueryBudget(),
                        new SqliteCommand("ALTER TABLE assets DROP COLUMN byte_count"),new SqliteCommand("PRAGMA user_version=1")))){}
                } finally {await db.CloseAsync();}
                library=await ImageLibraryIndex.OpenAsync(path);
                Assert.AreEqual(position.LibraryId,(await library.GetPositionAsync(scope)).LibraryId);
                Assert.AreEqual(1,(await library.QueryAsync(scope)).Items.Count);
                Assert.IsNull((await library.QueryAsync(scope)).Items.Single().ByteCount);
                await library.RefreshAsync(scope);
                Assert.AreEqual(23,(await library.QueryAsync(scope)).Items.Single().ByteCount);
            } finally {
                if(observation!=null)await observation.CloseAsync();
                if(library!=null)await library.ShutdownAsync();
                Directory.Delete(root,true);
            }
        });
    }
}
