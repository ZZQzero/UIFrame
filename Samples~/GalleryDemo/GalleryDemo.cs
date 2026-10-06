using System;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Game.Media;
using Game.Media.Backup;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Attach to an application-owned object. Bind UGUI buttons to the public methods.</summary>
public sealed class GalleryDemo : MonoBehaviour
{
    [SerializeField] RawImage previewTarget;
    [SerializeField] Text statusTarget;
    [SerializeField] string server = "http://127.0.0.1:8787";
    [SerializeField] string account = "local-user";
    string token;
    ImageSelection selection;
    ImageTexture preview;
    ImageBackupService backup;
    ImageThumbnailCache cache;
    BackupTaskCursor taskCursor;
    BackupStorageSummary cleanupPreview;
    CancellationTokenSource lifetime;
    bool busy;
    bool opening;
    void Awake() { lifetime = new CancellationTokenSource();cache=new ImageThumbnailCache(); }
    // Supply credentials at runtime. The token is never serialized with a scene or prefab.
    public void SetAccessToken(string value) { token = value; }
    public void SelectOne() => Run(ct => Select(1, ct));
    public void SelectMultiple() => Run(ct => Select(9, ct));
    public void ReadAlbums() => Run(async ct =>
    {
        var access = await GameGallery.RequestLibraryAccessAsync(ct);
        if (access != LibraryAccess.Authorized && access != LibraryAccess.Limited) { SetStatus("未获得相册读取授权。"); return; }
        var albums = await GameGallery.QueryAlbumsAsync(ct); SetStatus($"可访问 {albums.Count} 个相册，授权状态：{access}。");
    });
    public void BackupSelected() => Run(async ct =>
    {
        if (selection == null) throw new InvalidOperationException("请先选择图片。");
        await EnsureBackup(ct);await (await backup.SubmitAsync(Guid.NewGuid().ToString("N"),selection.Items,ct)).WaitAsync(ct);
        var summary=await backup.GetSummaryAsync(ct);long completed=summary[BackupState.Completed],failed=summary[BackupState.Failed]+summary[BackupState.NeedsAttention];
        SetStatus($"已备份 {completed} 张，失败或需处理 {failed} 张。" + (backup.UsesNativeBackgroundTransfer ? "其余已提交系统后台，请刷新状态查看结果。" : ""));
    });
    public void ContinueBackup() => ResumeBackup();
    public void PauseBackup() => Run(async ct => { await EnsureBackup(ct); await backup.PauseAsync(ct); SetStatus("备份已暂停。"); });
    public void ResumeBackup() => Run(async ct => { await EnsureBackup(ct); await backup.ResumeAsync(ct); SetStatus("已提交继续备份。"); });
    public void RefreshBackupStatus() => Run(async ct =>
    {
        await EnsureBackup(ct);long completed=(await backup.GetSummaryAsync(ct))[BackupState.Completed];
        SetStatus($"已确认备份 {completed} 张。");
    });
    public void ShowTaskFirstPage(){taskCursor=null;ShowTaskNextPage();}
    public void ShowTaskNextPage()=>Run(async ct=>
    {
        await EnsureBackup(ct);var page=await backup.QueryTasksAsync(new BackupTaskQuery {PageSize=20,Cursor=taskCursor},ct);taskCursor=page.Next;
        SetStatus(string.Join("\n",page.Items.Select(task=>task.name+" · "+task.state+" · "+task.phase))+(page.Next==null?"\n已到末页。":"\n还有下一页。"));
    });
    public void PreviewCleanup()=>Run(async ct=>
    {
        await EnsureBackup(ct);cleanupPreview=await new BackupMaintenance(backup).PreviewAsync(token:ct);
        SetStatus($"待回收上限 {cleanupPreview.ReclaimableBytes/1048576.0:F1} MiB，仍在使用的文件会跳过。");
    });
    public void CleanupOnePage()=>Run(async ct=>
    {
        if(cleanupPreview==null)throw new InvalidOperationException("请先预览清理空间。");
        var result=await new BackupMaintenance(backup).RunAsync(token:ct);cleanupPreview=null;
        SetStatus($"已释放 {result.FileBytesFreed/1048576.0:F1} MiB，移除 {result.HistoryRemoved} 条历史。");
    });
    // Closing the preview does not own or cancel already accepted backup jobs.
    public void ClearPreview()
    {
        if (previewTarget != null) previewTarget.texture = null;
        preview?.Dispose(); preview = null; selection?.Dispose(); selection = null;
    }
    async UniTask Select(int count, CancellationToken ct)
    {
        if (previewTarget == null) throw new InvalidOperationException("请配置预览 RawImage。");
        var next = await GameGallery.PickImagesAsync(new ImagePickOptions { MaxCount = count }, ct);
        ImageTexture texture = null;
        try
        {
            texture = await cache.AcquireAsync(next.Items[0],new ImagePreviewOptions(),ct);
            ct.ThrowIfCancellationRequested(); ClearPreview(); selection = next; preview = texture; previewTarget.texture = preview.Texture;
            SetStatus($"已选择 {selection.Items.Count} 张图片。");
        }
        catch { try{texture?.Dispose();}catch(Exception cleanup){Debug.LogException(cleanup);}try{next.Dispose();}catch(Exception cleanup){Debug.LogException(cleanup);}throw; }
    }
    async UniTask EnsureBackup(CancellationToken ct)
    {
        if (backup != null) return;
        opening = true;
        try { backup = await ImageBackupService.CreateAsync(new BackupConfiguration { ServerUrl = server, Account = account, AccessToken = () => token,
            AllowDevelopmentHttp = true,
            EnableNativeBackgroundTransfer = Application.platform == RuntimePlatform.Android || Application.platform == RuntimePlatform.IPhonePlayer,
            NativeWifiOnly = true, StorageDirectory = Application.platform==RuntimePlatform.Android || Application.platform==RuntimePlatform.IPhonePlayer?ImageBackupService.NativeBackupStorageDirectory:Path.Combine(Application.persistentDataPath,"UIFrameBackup") }, ct); }
        finally { opening = false; }
    }
    async void Run(Func<CancellationToken, UniTask> operation)
    {
        if (busy) { SetStatus("请等待当前操作完成。"); return; }
        busy = true;
        try { await operation(lifetime.Token); }
        catch (OperationCanceledException) { SetStatus("已取消。"); }
        catch (Exception error) { SetStatus(error.Message); Debug.LogException(error); }
        finally { busy = false; }
    }
    void SetStatus(string value) { if (statusTarget != null) statusTarget.text = value; }
    async void OnDestroy()
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo first=null;
        void Capture(Exception error){if(first==null)first=System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);else Debug.LogException(error);}
        try{lifetime.Cancel();}catch(Exception error){Capture(error);}
        while(opening || busy)await UniTask.Yield();
        if(previewTarget!=null)previewTarget.texture=null;
        var oldPreview=preview;var oldSelection=selection;preview=null;selection=null;
        try{oldPreview?.Dispose();}catch(Exception error){Capture(error);}
        try{oldSelection?.Dispose();}catch(Exception error){Capture(error);}
        if(backup!=null)try{await backup.ShutdownAsync();}catch(Exception error){Capture(error);}
        try{await cache.ShutdownAsync();}catch(Exception error){Capture(error);}
        try{lifetime.Dispose();}catch(Exception error){Capture(error);}
        if(first!=null)Debug.LogException(first.SourceException);
    }
}
