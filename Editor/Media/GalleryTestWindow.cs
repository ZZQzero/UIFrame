using System;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Media;
using Game.Media.Backup;
using UnityEditor;
using UnityEngine;

namespace UIFrame.Editor
{
    public sealed class GalleryTestWindow : EditorWindow
    {
        string folder = "", server = "http://127.0.0.1:8787", account = "local-user", token = "", status = "选择图片或浏览目录。";
        ImageLibraryIndex library;
        ImageLibraryScope scope;
        ImageLibraryPage libraryPage;
        ImageThumbnailCache cache;
        ImageSelection selection;
        ImageReference selected;
        ImageTexture preview;
        ImageBackupService backup;
        BackupTaskPage taskPage;
        BackupSummary taskSummary;
        BackupStorageSummary storage;
        BackupAction batchAction=BackupAction.Pause;
        BackupState filterState=BackupState.Queued;
        bool filterTasks;
        readonly System.Collections.Generic.HashSet<string> selectedTasks=new System.Collections.Generic.HashSet<string>();
        CancellationTokenSource lifetime;
        Vector2 scroll;
        bool busy, opening;
        int page;
        [MenuItem("Tools/UIFrame/图片与备份")]
        public static void Open() => GetWindow<GalleryTestWindow>("图片与备份");
        void OnEnable() { lifetime = new CancellationTokenSource();cache=new ImageThumbnailCache(); }
        async void OnDisable()
        {
            var cleanup=new CleanupFailure();if(lifetime!=null)cleanup.Run(lifetime.Cancel);
            while(opening || busy)await UniTask.Yield();
            var oldBackup=backup;var oldLibrary=library;var oldCache=cache;var oldPreview=preview;var oldSelection=selection;var oldLifetime=lifetime;
            backup=null;library=null;cache=null;preview=null;selection=null;lifetime=null;token="";taskPage=null;taskSummary=null;storage=null;selectedTasks.Clear();
            if(oldPreview!=null)cleanup.Run(oldPreview.Dispose);if(oldSelection!=null)cleanup.Run(oldSelection.Dispose);
            if(oldBackup!=null)try{await oldBackup.ShutdownAsync();}catch(Exception error){cleanup.Capture(error);}
            if(oldLibrary!=null)try{await oldLibrary.ShutdownAsync();}catch(Exception error){cleanup.Capture(error);}
            if(oldCache!=null)try{await oldCache.ShutdownAsync();}catch(Exception error){cleanup.Capture(error);}
            if(oldLifetime!=null)cleanup.Run(oldLifetime.Dispose);
            try{cleanup.Throw();}catch(Exception error){Debug.LogException(error);}
        }
        void OnInspectorUpdate() { Repaint(); }
        void OnGUI()
        {
            EditorGUILayout.LabelField("图片读取", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(busy))
            {
                if (GUILayout.Button("选择一张图片")) Run(async ct =>
                {
                    var next = await GameGallery.PickImagesAsync(cancellationToken: ct);
                    selection?.Dispose(); selection = next; await Preview(next.Items[0], ct);
                });
                folder = EditorGUILayout.TextField("图片目录", folder);
                if (GUILayout.Button("浏览目录")) Run(async ct => { if(library==null)library=await ImageLibraryIndex.OpenAsync(Path.Combine(Application.persistentDataPath,"UIFrameGalleryDemo","library.sqlite"),ct);scope=new ImageLibraryScope(ImageLibrarySourceKind.Directory,folder);await library.RefreshAsync(scope,ct);libraryPage=await library.QueryAsync(scope,60,token:ct);page=0;status="已读取图库首页。"; });
            }
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(160));
            if (libraryPage != null)
                foreach (var item in libraryPage.Items)
                    using (new EditorGUI.DisabledScope(busy))
                        if (GUILayout.Button(item.FileName)) Run(ct => Preview(item, ct));
            EditorGUILayout.EndScrollView();
            using(new EditorGUI.DisabledScope(busy || libraryPage==null))
            {
                if(GUILayout.Button("返回首页"))Run(async ct=>{libraryPage=await library.QueryAsync(scope,60,token:ct);page=0;});
                using(new EditorGUI.DisabledScope(libraryPage?.Next==null))if(GUILayout.Button("下一页"))Run(async ct=>{libraryPage=await library.QueryAsync(scope,60,libraryPage.Next,ct);page++;});
            }
            if (preview != null) GUI.DrawTexture(GUILayoutUtility.GetRect(160, 160), preview.Texture, ScaleMode.ScaleToFit);
            EditorGUILayout.Space(); EditorGUILayout.LabelField("本机备份", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(backup != null || busy))
            {
                server = EditorGUILayout.TextField("服务器", server); account = EditorGUILayout.TextField("账号", account); token = EditorGUILayout.PasswordField("访问令牌", token);
            }
            using (new EditorGUI.DisabledScope(busy || selected == null))
            {
                if (GUILayout.Button("备份当前图片")) Run(async ct =>
                {
                    await EnsureBackup(ct);await (await backup.SubmitAsync(Guid.NewGuid().ToString("N"),new[] { selected },ct)).WaitAsync(ct);
                    status="图片已受理，请刷新任务查看上传和确认状态。";
                });
            }
            using (new EditorGUI.DisabledScope(busy || backup == null))
            {
                if (GUILayout.Button("恢复已暂停的任务")) Run(ct => backup.ResumeAsync(ct));
                if (GUILayout.Button("下载并校验最近的备份")) Run(async ct =>
                {
                    var task=(await backup.QueryBackupsAsync(1,cancellationToken:ct)).Items.FirstOrDefault();
                    if (task == null) throw new InvalidOperationException("没有已完成的备份。");
                    var destination = EditorUtility.SaveFilePanel("下载验证", "", task.Name, Path.GetExtension(task.Name).TrimStart('.'));
                    if (string.IsNullOrEmpty(destination)) return;
                    await backup.DownloadBackupAndVerifyAsync(task.BackupId, destination, ct); status = "下载成功，内容校验一致。";
                });
            }
            EditorGUILayout.Space();EditorGUILayout.LabelField("任务与空间",EditorStyles.boldLabel);
            using(new EditorGUI.DisabledScope(busy))
            {
                EditorGUI.BeginChangeCheck();filterTasks=EditorGUILayout.Toggle("按状态筛选",filterTasks);filterState=(BackupState)EditorGUILayout.EnumPopup("任务状态",filterState);
                if(EditorGUI.EndChangeCheck()){taskPage=null;selectedTasks.Clear();}
                if(GUILayout.Button("刷新任务首页"))Run(async ct=>{await EnsureBackup(ct);await RefreshTasks(null,ct);});
                if(taskSummary!=null)EditorGUILayout.LabelField($"待处理 {taskSummary[BackupState.Queued]} · 已完成 {taskSummary[BackupState.Completed]} · 需核对 {taskSummary[BackupState.NeedsAttention]}");
                if(taskPage!=null)
                {
                    foreach(var task in taskPage.Items)
                    {
                        bool chosen=EditorGUILayout.ToggleLeft($"{task.name} · {task.state} · {task.acceptance} · {task.phase}",selectedTasks.Contains(task.id));
                        if(chosen)selectedTasks.Add(task.id);else selectedTasks.Remove(task.id);
                    }
                    using(new EditorGUI.DisabledScope(taskPage.Next==null))if(GUILayout.Button("下一页任务"))Run(ct=>RefreshTasks(taskPage.Next,ct));
                }
                batchAction=(BackupAction)EditorGUILayout.EnumPopup("批量操作",batchAction);
                using(new EditorGUI.DisabledScope(backup==null || selectedTasks.Count==0))if(GUILayout.Button("应用到勾选任务"))Run(async ct=>
                {
                    string id=Guid.NewGuid().ToString("N");
                    await backup.SubmitOperationAsync(new BackupOperationCommand {OperationId=id,Action=batchAction,TaskIds=selectedTasks.ToArray()},ct);
                    var result=await backup.WaitOperationAsync(id,ct);status=$"操作 {result.Phase}：已应用 {result.AppliedCount}/{result.SelectedCount}。";
                    await RefreshTasks(null,ct);
                });
                using(new EditorGUI.DisabledScope(backup==null))
                {
                    if(GUILayout.Button("预览可清理空间"))Run(async ct=>{storage=await new BackupMaintenance(backup).PreviewAsync(token:ct);});
                    if(storage!=null)EditorGUILayout.HelpBox($"待上传 {storage.UploadBytes/1048576.0:F1} MiB；待回收上限 {storage.ReclaimableBytes/1048576.0:F1} MiB；清理失败 {storage.CleanupFailedBytes/1048576.0:F1} MiB。",MessageType.None);
                    using(new EditorGUI.DisabledScope(storage==null))if(GUILayout.Button("执行一轮清理"))Run(async ct=>
                    {
                        var maintenance=new BackupMaintenance(backup);var result=await maintenance.RunAsync(token:ct);
                        storage=await maintenance.PreviewAsync(token:ct);status=$"释放 {result.FileBytesFreed/1048576.0:F1} MiB，清除 {result.HistoryRemoved} 条历史。";await RefreshTasks(null,ct);
                    });
                }
            }
            EditorGUILayout.HelpBox(status, MessageType.Info);
            EditorGUILayout.Space(); EditorGUILayout.LabelField("移动端构建", EditorStyles.boldLabel);
            var settings = GalleryBuildSettings.instance;
            EditorGUI.BeginChangeCheck();
            bool enableRead = EditorGUILayout.Toggle("启用照片库读取权限", settings.enableLibraryRead);
            string description = EditorGUILayout.TextField("iOS 权限用途说明", settings.photoLibraryUsageDescription);
            if (EditorGUI.EndChangeCheck()) { settings.enableLibraryRead = enableRead; settings.photoLibraryUsageDescription = description; settings.SaveSettings(); }
            EditorGUILayout.HelpBox("系统选图无需开启照片库权限。移动端原生选择器需在设备上验证。Editor 用于前台上传测试。移动端可显式启用原生后台传输；自动发现新照片仍需应用运行。", MessageType.None);
        }
        async UniTask RefreshTasks(BackupTaskCursor cursor,CancellationToken ct)
        {
            taskPage=await backup.QueryTasksAsync(new BackupTaskQuery {PageSize=20,State=filterTasks?filterState:(BackupState?)null,Cursor=cursor},ct);
            taskSummary=await backup.GetSummaryAsync(ct);selectedTasks.Clear();
        }
        async UniTask EnsureBackup(CancellationToken ct)
        {
            if (backup != null) return;
            opening = true;
            try { backup = await ImageBackupService.CreateAsync(new BackupConfiguration
            {
                ServerUrl = server, Account = account, AccessToken = () => token, AllowDevelopmentHttp = true,
                StorageDirectory = Path.Combine(Application.persistentDataPath, "UIFrameBackupDemo", Hash128.Compute(server + "|" + account).ToString())
            }, ct); }
            finally { opening = false; }
        }
        async UniTask Preview(ImageReference image, CancellationToken ct)
        {
            var next = await cache.AcquireAsync(image,new ImagePreviewOptions(),ct);
            preview?.Dispose(); preview = next; selected = image; status = $"{image.FileName} · {preview.Texture.width} × {preview.Texture.height}";
        }
        async void Run(Func<CancellationToken, UniTask> operation)
        {
            if (busy) return; busy = true;
            try { await operation(lifetime.Token); }
            catch (OperationCanceledException) { status = "已取消。"; }
            catch (Exception error) { status = error.Message; }
            finally { busy = false; if (this != null) Repaint(); }
        }
    }
}
