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
        ImageSnapshot snapshot;
        ImageSelection selection;
        ImageReference selected;
        ImageTexture preview;
        ImageBackupService backup;
        CancellationTokenSource lifetime;
        Vector2 scroll;
        bool busy, opening;
        int page;
        [MenuItem("Tools/UIFrame/图片与备份")]
        public static void Open() => GetWindow<GalleryTestWindow>("图片与备份");
        void OnEnable() { lifetime = new CancellationTokenSource(); }
        async void OnDisable()
        {
            lifetime?.Cancel();
            try { while (opening) await UniTask.Yield(); if (backup != null) await backup.ShutdownAsync(); }
            catch (Exception error) { Debug.LogException(error); }
            finally { preview?.Dispose(); selection?.Dispose(); lifetime?.Dispose(); backup = null; preview = null; selection = null; token = ""; }
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
                if (GUILayout.Button("浏览目录")) Run(async ct => { snapshot = await GameImageDirectory.QueryAsync(folder, cancellationToken: ct); page = 0; status = $"找到 {snapshot.Count} 张候选图片。"; });
            }
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(160));
            if (snapshot != null)
                foreach (var item in snapshot.GetPage(Math.Min(page * 60, snapshot.Count)))
                    using (new EditorGUI.DisabledScope(busy))
                        if (GUILayout.Button(item.FileName)) Run(ct => Preview(item, ct));
            EditorGUILayout.EndScrollView();
            if (snapshot != null && snapshot.Count > 60)
            {
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(page == 0)) if (GUILayout.Button("上一页")) { page--; scroll = Vector2.zero; }
                EditorGUILayout.LabelField($"{page + 1} / {(snapshot.Count + 59) / 60}");
                using (new EditorGUI.DisabledScope((page + 1) * 60 >= snapshot.Count)) if (GUILayout.Button("下一页")) { page++; scroll = Vector2.zero; }
                EditorGUILayout.EndHorizontal();
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
                    await EnsureBackup(ct); await backup.EnqueueAsync(new[] { selected }, ct); await backup.ProcessAsync(ct);
                    var tasks = backup.GetTasks(); status = $"已备份 {tasks.Count(x => x.state == BackupState.Completed)}；失败或需处理 {tasks.Count(x => x.state == BackupState.Failed || x.state == BackupState.NeedsAttention)}。";
                });
            }
            using (new EditorGUI.DisabledScope(busy || backup == null))
            {
                if (GUILayout.Button("继续处理已保存的任务")) Run(ct => backup.ProcessAsync(ct));
                if (GUILayout.Button("下载并校验最近的备份")) Run(async ct =>
                {
                    var task = backup.GetTasks().LastOrDefault(x => x.state == BackupState.Completed);
                    if (task == null) throw new InvalidOperationException("没有已完成的备份。");
                    var destination = EditorUtility.SaveFilePanel("下载验证", "", task.name, Path.GetExtension(task.name).TrimStart('.'));
                    if (string.IsNullOrEmpty(destination)) return;
                    await backup.DownloadAndVerifyAsync(task.id, destination, ct); status = "下载成功，内容校验一致。";
                });
            }
            EditorGUILayout.HelpBox(status, MessageType.Info);
            EditorGUILayout.Space(); EditorGUILayout.LabelField("移动端构建", EditorStyles.boldLabel);
            var settings = GalleryBuildSettings.instance;
            EditorGUI.BeginChangeCheck();
            bool library = EditorGUILayout.Toggle("启用照片库读取权限", settings.enableLibraryRead);
            string description = EditorGUILayout.TextField("iOS 权限用途说明", settings.photoLibraryUsageDescription);
            if (EditorGUI.EndChangeCheck()) { settings.enableLibraryRead = library; settings.photoLibraryUsageDescription = description; settings.SaveSettings(); }
            EditorGUILayout.HelpBox("系统选图无需开启照片库权限。移动端原生选择器需在设备上验证。Editor 用于前台上传测试。移动端可显式启用原生后台传输；自动发现新照片仍需应用运行。", MessageType.None);
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
            var next = await GameImageReader.LoadPreviewAsync(image, cancellationToken: ct);
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
