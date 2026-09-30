using System;
using System.IO;
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
    CancellationTokenSource lifetime;
    bool busy;
    bool opening;
    void Awake() { lifetime = new CancellationTokenSource(); }
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
        await EnsureBackup(ct); await backup.EnqueueAsync(selection.Items, ct); await backup.ProcessAsync(ct);
        int completed = 0, failed = 0;
        foreach (var job in backup.GetTasks()) { if (job.state == BackupState.Completed) completed++; if (job.state == BackupState.Failed || job.state == BackupState.NeedsAttention) failed++; }
        SetStatus($"已备份 {completed} 张，失败或需处理 {failed} 张。" + (backup.UsesNativeBackgroundTransfer ? "其余已提交系统后台，请刷新状态查看结果。" : ""));
    });
    public void ContinueBackup() => Run(async ct => { await EnsureBackup(ct); await backup.ProcessAsync(ct); SetStatus("本轮备份任务处理完毕，请查看任务状态。"); });
    public void PauseBackup() => Run(async ct => { await EnsureBackup(ct); await backup.PauseAsync(ct); SetStatus("备份已暂停。"); });
    public void ResumeBackup() => Run(async ct => { await EnsureBackup(ct); backup.Resume(); await backup.ProcessAsync(ct); SetStatus("已提交继续备份。"); });
    public void RefreshBackupStatus() => Run(async ct =>
    {
        await EnsureBackup(ct); int completed = 0;
        foreach (var job in backup.GetTasks()) if (job.state == BackupState.Completed) completed++;
        SetStatus($"已确认备份 {completed} 张。");
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
            texture = await GameImageReader.LoadPreviewAsync(next.Items[0], cancellationToken: ct);
            ct.ThrowIfCancellationRequested(); ClearPreview(); selection = next; preview = texture; previewTarget.texture = preview.Texture;
            SetStatus($"已选择 {selection.Items.Count} 张图片。");
        }
        catch { texture?.Dispose(); next.Dispose(); throw; }
    }
    async UniTask EnsureBackup(CancellationToken ct)
    {
        if (backup != null) return;
        opening = true;
        try { backup = await ImageBackupService.CreateAsync(new BackupConfiguration { ServerUrl = server, Account = account, AccessToken = () => token,
            AllowDevelopmentHttp = true,
            EnableNativeBackgroundTransfer = Application.platform == RuntimePlatform.Android || Application.platform == RuntimePlatform.IPhonePlayer,
            NativeWifiOnly = true, StorageDirectory = Path.Combine(Application.persistentDataPath, "UIFrameBackup", Hash128.Compute(server + "|" + account).ToString()) }, ct); }
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
        lifetime.Cancel();
        try { while (opening) await UniTask.Yield(); if (backup != null) await backup.ShutdownAsync(); }
        finally { ClearPreview(); lifetime.Dispose(); }
    }
}
