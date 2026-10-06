using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Game.Media;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace UIFrame.Regression
{
    public sealed class MediaDirectoryMembershipTests
    {
        [DllImport("libc", SetLastError=true)]
        static extern int symlink(string target, string path);

        [UnityTest]
        public IEnumerator IncrementalRefreshRemovesLinksAndAcceptsRegularFiles() => UniTask.ToCoroutine(async () =>
        {
            if(UnityEngine.Application.platform==UnityEngine.RuntimePlatform.WindowsEditor)Assert.Ignore("This symlink fixture uses POSIX libc.");
            string root=Path.Combine(Path.GetTempPath(),"uiframe-symlink-review-"+Guid.NewGuid().ToString("N"));
            string source=Path.Combine(root,"selected"),outside=Path.Combine(root,"outside.jpg"),link=Path.Combine(source,"linked.jpg");
            Directory.CreateDirectory(source);
            File.WriteAllBytes(outside,new byte[]{1,2,3,4});
            ImageLibraryIndex library=null;
            ImageLibraryIndex.WatchHandle observation=null;
            try
            {
                library=await ImageLibraryIndex.OpenAsync(Path.Combine(root,"library.sqlite"));
                var scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,source);
                observation=library.Watch(scope,batch=>{});
                await library.RefreshAsync(scope);
                Assert.AreEqual(0,(await library.QueryAsync(scope)).Items.Count);
                var observer=typeof(ImageLibraryIndex.WatchHandle).GetField("observer",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(observation);
                ((FileSystemWatcher)observer.GetType().GetField("Watcher",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(observer)).EnableRaisingEvents=false;
                ((FileSystemWatcher)observer.GetType().GetField("DirectoryWatcher",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(observer)).EnableRaisingEvents=false;
                File.WriteAllBytes(link,new byte[]{5});
                observer.GetType().GetMethod("Changed",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(observer,new object[]{link});
                await library.RefreshAsync(scope);
                Assert.AreEqual(1,(await library.QueryAsync(scope)).Items.Count);
                File.Delete(link);
                Assert.AreEqual(0,symlink(outside,link),"symlink creation");
                Assert.IsTrue((File.GetAttributes(link)&FileAttributes.ReparsePoint)!=0);
                // Inject precisely the production callback for a file-created/changed event.
                // The first, separately retained real-watcher probe did not deliver it on this host.
                observer.GetType().GetMethod("Changed",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(observer,new object[]{link});
                var refresh=await library.RefreshAsync(scope);
                Assert.AreEqual(ImageLibraryRefreshKind.Incremental,refresh.Kind);
                Assert.AreEqual(0,(await library.QueryAsync(scope)).Items.Count);
                await library.RefreshAsync(scope,completeReconciliation:true);
                Assert.AreEqual(0,(await library.QueryAsync(scope)).Items.Count);
                File.Delete(link);File.WriteAllBytes(link,new byte[]{6});
                observer.GetType().GetMethod("Changed",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(observer,new object[]{link});
                await library.RefreshAsync(scope);
                Assert.AreEqual(1,(await library.QueryAsync(scope)).Items.Count);
                string external=Path.Combine(root,"external"),directoryLink=Path.Combine(source,"alias");
                Directory.CreateDirectory(external);File.WriteAllBytes(Path.Combine(external,"photo.jpg"),new byte[]{7});
                Assert.AreEqual(0,symlink(external,directoryLink));
                Assert.IsFalse(GameImageDirectory.ContainsImage(source,true,Path.Combine(directoryLink,"photo.jpg")));
                Directory.Delete(directoryLink);
            }
            finally
            {
                if(observation!=null)await observation.CloseAsync();
                if(library!=null)await library.ShutdownAsync();
                if(File.Exists(link))File.Delete(link);
                Directory.Delete(root,true);
            }
        });
    }
}
