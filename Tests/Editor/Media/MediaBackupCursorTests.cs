using System;
using System.Collections;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Game.Media;
using Game.Media.Backup;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace UIFrame.Regression
{
    public sealed class MediaBackupCursorTests
    {
        string root;
        ImageBackupService first,second;
        BackupConfiguration Config(string directory)=>new BackupConfiguration {
            ServerUrl="https://api.invalid",Account="integration-user",AccessToken=()=>"fixture",
            StorageDirectory=Path.Combine(root,directory)};
        async UniTask Run(Func<UniTask> body)
        {
            root=Path.Combine(Path.GetTempPath(),"uiframe-cursor-boundary-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
            try {
                first=await ImageBackupService.CreateAsync(Config("first"),NativeBackup.Call,false,new BackupProtocolFixture {ConfirmOnPlan=true});
                second=await ImageBackupService.CreateAsync(Config("second"),NativeBackup.Call,false,new BackupProtocolFixture {ConfirmOnPlan=true});
                await body();
            } finally {
                try {if(first!=null)await first.ShutdownAsync();}
                finally {try {if(second!=null)await second.ShutdownAsync();}finally {Directory.Delete(root,true);}}
            }
        }
        ImageReference Photo(string name)
        {string path=Path.Combine(root,name+".jpg");File.WriteAllBytes(path,new byte[]{1,2,3,4});return ImageReference.FromFile(path);}
        async UniTask Seed()
        {await first.SubmitAndWaitAsync("aa",new[]{Photo("one"),Photo("two")});await first.WaitForIdleAsync();}
        async UniTask Reopen()
        {await first.ShutdownAsync();first=null;first=await ImageBackupService.CreateAsync(Config("first"),NativeBackup.Call,false,new BackupProtocolFixture());}
        static async UniTask Reject<T>(UniTask<T> task)
        {Exception failure=null;try{await task;}catch(Exception error){failure=error;}Assert.IsInstanceOf<ArgumentException>(failure);}

        [UnityTest] public IEnumerator ReceiptCursorRejectsIndependentCatalogAndSurvivesReopen()=>UniTask.ToCoroutine(()=>Run(async()=>
        {
            await Seed();var page=await first.QueryBackupsAsync(1);Assert.IsNotNull(page.Next);
            await Reject(second.QueryBackupsAsync(1,page.Next));
            await Reopen();var next=await first.QueryBackupsAsync(1,page.Next);
            Assert.AreEqual(1,next.Items.Count);Assert.AreNotEqual(page.Items[0].Source,next.Items[0].Source);
        }));

        [UnityTest] public IEnumerator ChangeCursorRejectsIndependentCatalogAndSurvivesReopen()=>UniTask.ToCoroutine(()=>Run(async()=>
        {
            await Seed();var page=await first.ReadChangesAsync(pageSize:1);Assert.AreEqual(1,page.Items.Count);
            await Reject(second.ReadChangesAsync(page.Next,1));
            await Reopen();var next=await first.ReadChangesAsync(page.Next,1);
            Assert.IsFalse(next.RequiresRefresh);Assert.AreEqual(1,next.Items.Count);Assert.Greater(next.Items[0].Sequence,page.Items[0].Sequence);
        }));

        [UnityTest] public IEnumerator OperationCursorRejectsIndependentCatalogAndSurvivesReopen()=>UniTask.ToCoroutine(()=>Run(async()=>
        {
            first.SetDesktopNetworkPolicy(()=>false);await first.SubmitAndWaitAsync("aa",new[]{Photo("one"),Photo("two")});
            await first.SubmitOperationAsync(new BackupOperationCommand {OperationId="bb",Action=BackupAction.Pause});await first.WaitOperationAsync("bb");
            var page=await first.QueryOperationAsync("bb",pageSize:1);Assert.IsNotNull(page.Next);
            await Reject(second.QueryOperationAsync("bb",page.Next,1));
            await Reopen();var next=await first.QueryOperationAsync("bb",page.Next,1);
            Assert.AreEqual(1,next.Items.Count);Assert.AreNotEqual(page.Items[0].TaskId,next.Items[0].TaskId);
        }));

        [UnityTest] public IEnumerator CleanupCursorRejectsIndependentCatalogAndSurvivesReopen()=>UniTask.ToCoroutine(()=>Run(async()=>
        {
            string owner=(string)typeof(ImageBackupService).GetField("owner",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(first);
            await first.Db(BackupRepository.Command.Prepare,default,"aa",owner,DateTime.UtcNow.Ticks,2,"b1","file:first","v1","first.jpg","image/jpeg","b2","file:second","v1","second.jpg","image/jpeg","",0L);
            string folder=Path.Combine(first.RepositoryRoot,first.StoreId,"payloads");
            foreach(string id in new[]{"b1","b2"}) {
                await first.Db(BackupRepository.Command.TryPrepare,default,id,owner,4L,4L,100L,DateTime.UtcNow.Ticks);
                Directory.CreateDirectory(Path.Combine(folder,id+".payload"));File.WriteAllText(Path.Combine(folder,id+".payload","occupied"),"fixture");
                await first.Db(BackupRepository.Command.FailItem,default,id,owner,"interrupted",DateTime.UtcNow.Ticks);
                LogAssert.Expect(LogType.Exception,new System.Text.RegularExpressions.Regex("BackupRepositoryException"));
            }
            await first.CleanupFilesAsync();var page=await first.QueryCleanupFailuresAsync(1);Assert.IsNotNull(page.Next);
            await Reject(second.QueryCleanupFailuresAsync(1,page.Next));
            await Reopen();var next=await first.QueryCleanupFailuresAsync(1,page.Next);
            Assert.AreEqual(1,next.Items.Count);Assert.AreNotEqual(page.Items[0].FileId,next.Items[0].FileId);
        }));

        [UnityTest] public IEnumerator DiscoveryCursorRejectsIndependentCatalogAndSurvivesReopen()=>UniTask.ToCoroutine(()=>Run(async()=>
        {
            await first.ShutdownAsync();first=null;var config=Config("first");config.MaxFileBytes=1;
            first=await ImageBackupService.CreateAsync(config,NativeBackup.Call,false,new BackupProtocolFixture());
            Photo("one");Photo("two");var library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"library.sqlite"));
            try {
                // Both real source files exceed this service's preparation limit.
                var policy=new AutomaticBackupPolicy {enabled=true,sourceKind=BackupSourceKind.Directory,source=root,includeExisting=true,wifiOnly=false};
                var automatic=await AutomaticImageBackup.CreateAsync(first,library);await automatic.ConfigureAsync(policy);await automatic.ScanOnceAsync();
                await first.WaitForIdleAsync();Assert.AreEqual(2,(await automatic.GetPreparationFailuresAsync()).Items.Count);
                var page=await automatic.GetPreparationFailuresAsync(1);Assert.IsNotNull(page.Next);
                var other=await AutomaticImageBackup.CreateAsync(second,library);await other.ConfigureAsync(policy);
                await Reject(other.GetPreparationFailuresAsync(1,page.Next));
                await Reopen();automatic=await AutomaticImageBackup.CreateAsync(first,library);
                var next=await automatic.GetPreparationFailuresAsync(1,page.Next);
                Assert.AreEqual(1,next.Items.Count);Assert.AreNotEqual(page.Items[0].Source,next.Items[0].Source);
            } finally {await library.ShutdownAsync();}
        }));
    }
}
