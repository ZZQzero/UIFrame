# 图片选择与备份示例

1. 在场景中建立应用级对象，挂载 `GalleryDemo`。示例对象应独立于会被关闭的预览面板。
2. 配置 UGUI `RawImage`、`Text`，为按钮绑定 `SelectOne`、`SelectMultiple`、`ReadAlbums`、`BackupSelected`、`ContinueBackup`、`PauseBackup`、`ResumeBackup`、`RefreshBackupStatus`、`ClearPreview`。
3. 运行本机服务，配置服务器与账号；由运行时输入或认证服务调用 `SetAccessToken`，不要把令牌写在 Prefab。
4. 选图、预览、备份。关闭预览不取消已经接收的备份任务；结束示例对象会等待 C# 服务停止；移动端已交给系统的上传继续保留，队列在下次创建相同服务目录后仍可恢复。退出账号前先暂停备份并等待完成。
5. Editor 文件窗口只支持单选。多选与相册窗口、授权目录需在移动端验证。

相册查询前在 **Tools → UIFrame → 图片与备份** 开启照片库读取构建配置。系统选图本身不要求开启全库权限。

示例在 Android / iOS Player 显式启用原生后台传输（默认非计费网络），Editor 使用前台上传。`SubmitAsync` 返回登记句柄，示例继续等待其 `WaitAsync` 完成准备与系统交接；完成仍不代表服务端已确认，通过刷新读取已确认的备份数量；新照片的后台发现尚未实现。完整接口、错误与资源归属见 `Docs/Gallery.md`。

## 虚拟图库列表

创建带 Viewport / RectMask2D、Content 和固定单元尺寸的 `LoopVerticalScrollRect`，在列表根对象挂载 `GalleryVirtualListDemo`。单元 Prefab 挂载 `GalleryThumbnailCell` 并配置 RawImage；列表配置 scroll 与 cellPrefab。Content 使用现有 LoopScroll 支持的 VerticalLayoutGroup 或 GridLayoutGroup。

应用负责创建、刷新并持有 ImageLibraryIndex 与 scope，再调用 `await list.InitializeAsync(library, scope)`。`ShowFirstPageAsync` / `ShowNextPageAsync` 切换最多200项的元数据页，`HasNextPage` 控制下一页按钮。屏幕只创建可见单元，滚动重绑取消旧加载，离屏释放租约；缓存由示例持有。

列表初始化时持有图库订阅并刷新首页，`HasLibraryChanges` 可提示重新加载；`ShowFirstPageAsync` 读取当前第一页。关闭页面先 `await list.ShutdownAsync()`（等待订阅释放），之后应用再关闭图库索引。不要在列表仍查询时关闭索引。后台加载失败会向 Unity 日志传播，不以空图片伪装成功。5000张/10轮滚动、最低设备帧预算仍需产品场景验收。
