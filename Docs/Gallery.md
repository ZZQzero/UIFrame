# 图片、图库与备份使用说明

入口位于 `Game.Media`、`Game.Media.Backup`。所有公开入口在 Unity 主线程调用；文件复制、哈希、数据库和平台后台网络在工作线程运行。无需调用 `UI.Init()`。SQLite 是独立的 `Runtime/Sqlite` 模块；照片仓库通过独立原生库复用同一个引擎。

## 当前实现与范围

| 能力 | 入口与约定 |
| --- | --- |
| 系统选图 | `GameGallery.PickImagesAsync`，系统选择器负责选图预览；通常无需整库读取授权 |
| 相册 / 目录 | PhotoKit、MediaStore、普通目录、SAF / iOS 目录书签 |
| 按页读取 | `VisitImagesAsync` 每次最多200项；两端按需读取下一页，不先序列化整个照片库 |
| 图库索引 | `ImageLibraryIndex`，独立的 library.sqlite；版本、范围代次和持久变化游标 |
| 共享缩略图 | `ImageThumbnailCache`，同键合并、独立取消、纹理租约和空闲 LRU |
| 长期备份 | `ImageBackupService`，按账号的 catalog.sqlite；手动准备、分页查询、独立回执 |
| 后台传输 | Android JobScheduler / iOS URLSession，三端共用原生 BackupRepository 状态机 |
| 自动发现 | `AutomaticImageBackup`，前台监听与周期核对、历史基线和持久待准备候选 |
| 批量与清理 | 固定 OperationId、逐页目标选择、执行代次、文件与历史清理 |

本轮只有当前 SQLite 数据格式，不扫描、兼容、迁移或双写旧 JSON。JSON 仍用于 HTTP、系统桥接、服务器凭据配置和构建清单。没有后台发现新照片的系统服务；`SupportsBackgroundDiscovery=false`。操作系统决定后台上传机会，强制结束应用后不承诺继续执行。

`GameGallery.QueryImagesAsync` 和 `GameImageDirectory.QueryAsync` 是明确的全量快照 API，内存随元数据条数增长；大型图库使用下述索引分页。授权目录提供者按完整核对处理；SAF / 目录书签枚举仍有提供者及元数据临时文件成本，不标为可靠增量。

架构、状态机、协议约束与验证边界见 [设计文档](GalleryDesign.md)。计划中的性能数字为目标，不能从桌面编译或缓存预算推断手机峰值内存。

当前备份使用唯一 v2 协议：有界准备、原生持久接收、批量 Plan、独立文件 PUT、服务端确认和滚动批量 Query。业务仓库 ABI 为 4、catalog schema 为 5，图库 library schema 为 3，通用 SQLite ABI 仍为 3。两库只接受各自当前版本，不提供旧协议、旧 schema 或 JSON 数据迁移；旧开发库明确拒绝打开，开发验证须显式选择新的空目录，不自动删库。

## 系统选择与图片所有权

```csharp
using Game.Media;

// 面板打开期间使用自己的取消令牌。
using var selection = await GameGallery.PickImagesAsync(
    new ImagePickOptions { MaxCount = 9 }, OpenCancellationToken);

// 显示资源需保留到 UI 不再引用它时，不能在赋值后立刻 Dispose。
var preview = await GameImageReader.LoadPreviewAsync(
    selection.Items[0], new ImagePreviewOptions { MaxEdge = 1024 }, OpenCancellationToken);
rawImage.texture = preview.Texture;

// 关闭 / 换图：先清 UI 引用，再释放。
rawImage.texture = null;
preview.Dispose();
```

`ImageSelection` 拥有本次选图的暂存文件；用户取消表现为 `OperationCanceledException`，不返回空成功结果。第二个原生窗口请求会收到 `PickerBusy`。原生窗口不能立即关闭时，仍保持窗口占用直到实际结束。

`ImageTexture` 统一拥有其 Texture 和按需创建的 Sprite，业务不要再次 Destroy。`ImageFile` 拥有独立导出的临时文件，释放选择结果不会删除这个文件：

```csharp
using var uploadFile = await GameImageReader.ExportFileAsync(selection.Items[0]);
// 上传任务读取 uploadFile.LocalPath。读取完成后才能释放 uploadFile。
```

选图结果释放后不能发起新读取；已经受理的读取使用短期租约。所有权释放不等于取消在途操作，取消需传 Token。API 成功返回前，来源租约和处理槽位必须完成收尾；收尾失败时，框架回收尚未交付的纹理或导出文件，保留主异常。成功返回后的面板作用域登记失败，由调用方释放已取得的结果。

移动端按目标尺寸原生解码。Editor / 桌面使用有界的托管预览，只接受 JPEG / PNG，源像素上限为 16 MP；大于上限明确报 `ImageTooLarge`。Unity 预览阶段读入的编码文件上限为 128 MiB，限制的是预览缓冲，不限制独立原文件导出。托管预览会完整解码上限内的源图，然后缩小，不承诺与移动端相同的峰值内存。预览和转码的 `MaxEdge` 范围为 1–8192，默认 2048；新增 `MaxPixels` 限制按 MaxEdge 缩放后的目标像素数，默认 `4 * 1024 * 1024`，允许显式设置到 `16 * 1024 * 1024`。超出预算报 `ImageTooLarge`，不会偷偷降低尺寸。需要更大结果时同时设置合适的 MaxEdge 和 MaxPixels；超过 16 MP 的需求使用原文件导出并由独立处理管线处理。

系统照片选择器负责选图界面的缩略图；下述读取优化用于 Unity 自己展示的图片。iOS 相册预览按目标尺寸请求 PhotoKit 图片，不导出完整原图。Android / iOS 转码直接由原生缩放、合成透明背景并编码目标文件；桌面通过 Unity 下载纹理解码器异步解码，GPU 一次完成 EXIF 方向与缩放，JPEG 白底合成也不再创建全图浮点像素数组。桌面原有 16 MP 限制保留。

预览纹理默认 `Readable=false`，不保留 CPU 像素副本，也不生成 mipmap；RawImage / Sprite 显示不需要这些副本。业务确实需要 `GetPixels` / `GetPixels32` 时，显式设置 `ImagePreviewOptions.Readable=true`，并承担对应 CPU 副本。转码内部按编码需要申请可读结果。

所有预览、缩略图和转码共用一个处理槽位，覆盖原生异步请求、Unity 解码及变换；运行时还会等待当前帧的临时纹理销毁后再放行下一次。这是限制在途图片工作集的固定规则，原字节导出不占用该槽位。等待槽位时可取消；Unity 本地纹理解码开始后，取消会等当前解码结束、取得纹理所有权并释放后才返回，避免已创建纹理遗失。它不限制调用者已持有的显示纹理、系统照片提供者缓存或进程总内存；列表仍应及时释放离屏预览。

“保留原图”指保留系统提供者交付的字节，不承诺是最初相机文件。JPEG 转码默认质量 90、透明区域白底；可通过 `ImageExportOptions` 显式修改。保留字节模式不使用 MaxEdge、MaxPixels 和 JPEG 参数，也不受预览的编码文件限制。

## 相册权限与目录

系统选择器不需要全库读取授权。自行查询照片库前：

```csharp
var access = await GameGallery.GetLibraryAccessAsync(token);
// 在用户主动使用该功能时请求，不在启动时无条件弹出。
access = await GameGallery.RequestLibraryAccessAsync(token);
if (access == LibraryAccess.Authorized || access == LibraryAccess.Limited)
{
    var albums = await GameGallery.QueryAlbumsAsync(token);
    var images = await GameGallery.QueryImagesAsync(albumId: null, cancellationToken: token);
    var firstPage = images.GetPage(offset: 0, pageSize: 60);
}
```

Limited 表示只可读取获准的照片。权限请求接口报告授权状态，实际读取在权限不足时失败。`GetCapabilities()` 表示平台具备该功能，不代表已获得用户授权。

在 **Tools → UIFrame → 图片与备份** 中勾选“启用照片库读取权限”，填写实际的 iOS 用途说明。配置保存在项目 `ProjectSettings/UIFrameMediaSettings.asset`。构建处理器只在开启该功能时添加广泛读取权限；仅系统选图不需勾选。

构建前先在 Unity 切换到对应手机平台，等待脚本编译完成，再发起构建。构建处理器会拒绝活动目标不匹配的手机构建，避免平台条件编译使权限 / 系统框架配置回调缺失。Android Java 源码按 Gradle 标准布局放在 `.androidlib/src/main/java`，JNI 入口的混淆保留规则由构建处理器写入。

```csharp
var files = await GameImageDirectory.QueryAsync(absolutePath, recursive: false,
    cancellationToken: token);

// 移动端外部目录，用户显式授权。
var handle = await GameImageDirectory.PickDirectoryAsync(token);
var pictures = await handle.QueryAsync(recursive: true, cancellationToken: token);
string savedBookmark = handle.Bookmark; // 由业务保存，不能当作普通文件路径。
var restoredHandle = ImageDirectoryHandle.FromBookmark(savedBookmark);
```

大量文件的顺序处理可直接使用分页访问：

```csharp
bool completed = await GameImageDirectory.VisitAsync(absolutePath, async page =>
{
    foreach (var image in page)
    {
        using var file = await GameImageReader.ExportFileAsync(image, cancellationToken: token);
        await UploadYourFileAsync(file.LocalPath, token); // 业务提供上传方法。
    }
    return true; // 返回 false 可结束本次遍历。
}, recursive: true, cancellationToken: token);
```

iOS 目录书签按 SHA-256 标识保存一次，每个图片引用只包含短标识与相对路径。请保留应用的目录授权登记文件。图片身份只使用 `bookmarkId` 与 `relative`，创建引用时统一 JSON 字段顺序。任务、回执和原生记录都只读写当前格式，无迁移或历史格式检测。

授权失效、书签过期或文件被删除会明确失败，业务可由用户主动重新选择目录。目录扫描与照片库查询都不修改或删除用户原图。

## 图库索引与监听

```csharp
var library = await ImageLibraryIndex.OpenAsync(
    Path.Combine(Application.persistentDataPath, "Gallery", "library.sqlite"));
var scope = new ImageLibraryScope(ImageLibrarySourceKind.PhotoLibrary);
var subscription = library.Watch(scope, batch => { /* 更新可见页面 */ });
try
{
    await library.RefreshAsync(scope, token);
    var first = await library.QueryAsync(scope, pageSize: 100, token: token);
    var next = first.Next == null ? null : await library.QueryAsync(scope, 100, first.Next, token);
    // 面板使用期间持有 subscription。
}
finally { await subscription.CloseAsync(); }
```

照片库授权由 `RequestLibraryAccessAsync` 显式请求；刷新、监听和自动备份不会主动弹授权框。索引每页最多200条，每次写入最多32张元数据。游标绑定库身份、代次、范围修订及权限代次；变化日志被截断或范围变化时返回 `RequiresRefresh`，旧分页游标明确失败。

`Watch` 使用300ms安静窗口、最长2s合并连续事件；注册、原生轮询和刷新通过同一串行入口，允许先 Watch 后立即 RefreshAsync。普通目录分别监听图片文件和目录结构，忽略数据库等非图片文件；最多保留1,024个变化身份，溢出要求完整核对。Android使用 ContentObserver；MediaStore version / generation 在有变化、完整核对边界及每10秒周期检查时查询，空闲100ms轮询复用已核对边界。iOS使用 PhotoKit fetch result 的增量变化。重开、恢复前台、权限或连续性变化触发完整核对。增量读取和系统权限仍需真机验收。

每个索引最多同时观察16个范围，每范围32个订阅；普通查询不创建长期监听，也不累计占用观察名额。同范围的订阅和在途刷新共享观察者，最后一个使用者结束后释放文件监听及原生观察。单次 `RefreshAsync` 临时持有观察者；需要持续增量读取时应先 `Watch` 并在使用期间保留句柄。观察中断后查询仍可读取缓存，但 `RequiresRefresh` 为 true，下次刷新重新核对来源，不能复用中断期间的连续性假设。

自动驱动失败只停止对应范围，`subscription.Failure` 返回原异常。`subscription.Resume()` 恢复驱动，不重放已失败通知，也不要求全量重扫；`subscription.RequestRefresh()` 明确要求重新核对内容并恢复驱动。索引写入和通知交付分阶段：写入途中失败标记重新核对；等待刷新锁时取消、写入完成后的取消或回调异常不会使已提交索引失效。完整枚举失败不提交缺失删除，已写入页面不回滚。直接等待 `RefreshAsync` 时异常直接传播；后台驱动的异常同时由句柄和日志报告。`PruneChangesAsync` 每次最多清200条日志，旧消费者必须核对刷新。

观察者独立保存通知交付位置。刷新中断前已提交、尚未开始通知的变化，在后续刷新补交；历史截断或权限代次变化时明确通知 `RequiresRefresh`。一个通知页开始调用订阅者时即推进交付位置，该页回调失败后不自动重放；尚未尝试的后续页仍保留。原生请求取消后等待原生工作实际结束，再注销观察或关闭扫描，包括不产生输出文件的请求。

面板退出时 `await subscription.CloseAsync()`；最后一个订阅会等待在途观察使用者结束并完成原生注销，多个订阅共享时仅释放自身引用。关闭幂等，重复等待返回同一结果，释放失败不会自动再试。应用服务退出时 `await library.ShutdownAsync()`。索引与账号备份库分别管理生命周期。

## 共享缩略图与内存

```csharp
var cache = new ImageThumbnailCache(); // 应用服务拥有
var lease = await cache.AcquireAsync(image,
    new ImagePreviewOptions { MaxEdge = 256, Readable = false }, token);
rawImage.texture = lease.Texture;
// 离屏或换图时：
rawImage.texture = null;
lease.Dispose();
// 应用服务退出：
await cache.ShutdownAsync();
```

缓存键包括来源身份、内容版本、尺寸、像素预算和 Readable。同键并发共享一次处理，每个等待者独立取消。最后等待者取消时先移除可复用项；同键可以立即发起新代次，旧完成不会覆盖它。加载本身拥有来源租约，选择集合释放后已受理读取仍可完成；释放后不能再新建请求，包括缓存命中和加入已有在途请求。

空闲纹理默认预算32MiB，最多128个可保留键，按 LRU 回收。大于预算的结果仍按请求返回，最后使用者释放后销毁。`Statistics` 区分空闲、在用、待销毁字节和全局请求数量；这些不是整个进程的内存上限。

缓存、直接预览和导出共用请求准入：最多128个在途键、512个等待者、1MiB请求元数据；在保留来源前检查，超限报 `ImageQueueFull`，释放或等待已有请求后可再次申请。解码并发保持1。关闭取消未完成请求、释放空闲项，已交付租约仍可安全使用并自行释放。业务不得直接 Destroy 共享 Texture / Sprite。

`RefreshAsync(scope, token, completeReconciliation: true)` 明确要求完整核对；自动循环在变化通知时使用增量刷新，并按 scanIntervalSeconds 周期完整核对。

来源版本未知时缓存拒绝受理；原地修改且修改时间 / 大小不变时，业务调用 `Invalidate(image)`。文件的时间与长度不是密码学内容证明；备份确认另行使用流式 SHA-256。首次只提供内存缓存。

## 启动备份与接收图片

```csharp
bool native = Application.platform == RuntimePlatform.Android ||
              Application.platform == RuntimePlatform.IPhonePlayer;
var backup = await ImageBackupService.CreateAsync(new BackupConfiguration {
    ServerUrl = "https://your-backup.example",
    Account = accountId,
    AccessToken = () => currentAccessToken,
    StorageDirectory = native ? ImageBackupService.NativeBackupStorageDirectory :
        Path.Combine(Application.persistentDataPath, "PhotoBackup"),
    EnableNativeBackgroundTransfer = native,
    NativeWifiOnly = true,
    TransferMode = BackupTransferMode.UserInitiated // 用户在前台点击备份
});
string operationId = Guid.NewGuid().ToString("N"); // 调用方在提交前保存
BackupSubmissionResult submitted;
try {
    var submission = await backup.SubmitAsync(operationId, selection.Items, token);
    // 登记成功即持有全部来源租约，可以释放 selection。
    // 取消此等待不会撤销登记；停止未接受成员用 CancelPreparationAsync。
    submitted = await submission.WaitAsync(token);
} catch (BackupSubmissionException error) {
    // Result 可能为 null（仓库结果不明）；InnerExceptions 保留实际错误。
    // 已接受的照片继续由执行端负责，不能把整批重新提交。
    submitted = error.Result;
    throw;
}
// 提交完成只表示执行端已接受；界面分页观察状态和回执。
var page = await backup.QueryTasksAsync(cancellationToken: token);
```

`StorageDirectory` 是根目录。实际路径由标准化服务器地址和账号的 SHA-256 分隔；后台模式必须使用平台私有根，让系统能在 Unity 启动前定位 catalog。服务器、账号与目录身份不匹配明确失败。使用异步工厂，没有同步构造或全量 `GetTasks` 兼容入口。

一次登记1–32张，登记事务固定成员及顺序并返回 `BackupSubmission` 句柄；登记前取得全部来源租约。服务依次执行容量准入、流式准备、SHA-256、刷盘、来源版本检查、接受和交接，调用方可继续登记其他请求；单个源文件损坏不阻止其余有效项受理。共享凭据、仓库或调度失败会停止依赖它的提交步骤，已受理项保留。`submitted.AcceptedTaskIds` 返回已受理任务ID；跨中断核对使用调用前持久保存的 operationId。

`SubmitAsync` 成功只表示登记完成。句柄的 `WaitAsync` 等到各成员准备、交接或失败结束，`IsCompleted` 可查询；它不等待文件上传或服务端回执。取消登记令牌只阻止尚未执行的登记，进入原生事务后观察实际提交结果；取消 `WaitAsync` 只停止这次等待。`CancelPreparationAsync(operationId)` 持久保存未接受成员的停止意图，随后等待原句柄可观察实际收尾；已接受成员使用任务控制接口。`QuerySubmissionAsync(operationId)` 返回原有成员及 Registered / Preparing / Prepared / NativeAccepted / SystemScheduled / Confirmed 阶段；未知ID返回 null，任务历史已清理时明确 `DetailsExpired`，不编造状态。重复提交相同 operationId 被拒绝，应查询原结果。移动系统调度被接受仍不保证立即传输。桌面 `WaitForIdleAsync` 同时等待准备协调器和当前执行器空闲，失败或待核对也属于空闲，因此必须另查任务结果。

最多128个已登记尚未收尾成员、1个实际文件准备者、64个已准备未确认文件；登记并行过程互斥，登记完成后可以在前项等待容量时继续登记。照片暂存预算默认1GiB，单文件上限默认512MiB；预算是上限，不预分配磁盘。较大的默认总预算允许未知大小照片准备与已有文件上传并行，实际仍受磁盘空间约束，文件系统保留16MiB余量；控制清单另有8MiB预算。`AvailablePreparationSlots` 提供当前剩余登记容量，超过上限直接拒绝。`IsWaitingForCapacity` / `CapacityWait` 返回当前等待的请求、成员、原因、所需／可用／占用字节、文件数和采样时间。容量等待不计失败或重试，不阻塞来源扫描；取消准备、暂停或失权会结束对应准备，已接受项保留。释放和控制事件唤醒准备协调器，另以最长1秒间隔核对外部容量变化；实际复制期间以最长500ms间隔检查约束。网络与准备分别推进，不把完整照片放进 JSON、托管 byte[] 或数据库 BLOB。已知长度按当前可用空间等待；真正未知长度先等到 `min(MaxFileBytes, DiskBudgetBytes)` 空间可用，再有界准备，不通过失败后重读来探测大小。单张超出单文件／总暂存上限只使该项准备失败，释放其暂存后继续独立成员；磁盘写入、仓库或授权范围等共享故障仍结束本次提交。空间不足不删除原图或未确认照片。

数据库参数 / 结果每次上限1MiB，返回最多200行；原生最多16条并行准入命令、64个附件。一个 catalog 只有一个准备者，原生后台附件独立保有资源；C#关闭不会取消系统已受理传输。`ShutdownAsync` 先拒绝新调用、取消本门面的长期等待，再等已登记准备、实际导出和已受理操作收尾后释放；所有句柄都会结束，准备或执行器共享故障在关闭时传播。数据库结果不明时停止依赖准备并保留持久事实，须关闭／重开恢复，不自动重试；操作登记覆盖第一次异步等待到最终结束。`Dispose` 仅允许无活动操作时调用。应用先取消并等待自己启动的自动备份循环，再关闭备份门面和图库索引。

## 分页、回执与结果核对

```csharp
var page = await backup.QueryTasksAsync(new BackupTaskQuery {
    State = BackupState.Queued, PageSize = 100
}, token);
var summary = await backup.GetSummaryAsync(token);
var receipts = await backup.QueryBackupsAsync(100, cancellationToken: token);
await backup.DownloadBackupAndVerifyAsync(receipts.Items[0].BackupId,
    absoluteDestination, token);
```

任务游标包含仓库、筛选条件及创建上界；状态实时变化，不宣称跨页冻结快照。任务、回执、变化、批量操作明细、清理故障及准备失败的游标均绑定账号仓库标识与 catalog 的持久 `source_namespace`：同一账号在不同目录独立创建的库不能互用游标，原库关闭后重开仍可续页。汇总按状态持久计数，不加载历史字典。`ReadChangesAsync` 返回有序任务变化页；日志截断返回 `RequiresRefresh`。回执与来源版本关系独立保存，清理任务历史不丢失下载依据或自动判重事实。

`Completed` 只来自服务端确认的任务、代次、账号、来源版本、大小和 SHA-256 回执，仓库原子提交回执与应用标记。存储 PUT 的 200 / 201 / 204 可以没有 JSON，只代表传输成功；随后进入 `Verifying`，归还上传槽位并等待 Query。界面可用 `phase` 区分 Registration、FileTransfer、Confirmation、Finished，用 `acceptance` 展示接管阶段。

Plan / Query / Cancel 每批最多32项，请求256KiB、响应512KiB，单个上传描述8KiB。Plan 最多聚合250ms，首次 Query 聚合1秒；Pending 按服务器 nextCheckAt 继续正常核验。到期 Query 与 Plan 交替，未来 Query 不占当前控制槽。每仓库最多2个照片上传和1个当前控制请求，慢照片不拖住其他回执。第256次 Pending 或24小时截止结束自动确认，转 `NeedsAttention`。nextCheckAt 等于或超过截止也直接结束自动确认，不提前查询。已绑定的过期 Query 由执行器取消，实际结束后才记录释放；暂停 / 恢复不延长截止。该期限不保证操作系统及时唤醒，但下次执行机会会检查。有效的迟到确认回执仍可记录。

业务、存储及网关必须使用可信且不重定向的最终终点。Desktop / Android 禁止自动跳转；iOS 系统后台会话无法在跳转前拦截，客户端只校验最终目标，并在系统提供 metrics 时检测跳转，违规不能计为成功。这种事后检测不能撤回已转发字节，接入生产服务时必须验证网关及存储配置。

传输响应丢失、控制请求失败或结构不符都保留未知结果，进入 `NeedsAttention`，不自动重发。`ReconcileTaskAsync(taskId)` 在旧上传、控制请求和受保护凭据实际释放后，用相同任务和代次显式核对；已经确认则应用回执，仍可上传则按服务器描述继续。已保存取消意图时继续 Cancel 仲裁，普通核对不会撤销取消或恢复上传。一次 Absent 不能证明取消完成。旧资源尚未释放或重复核对同一项会被拒绝，独立照片可以继续。

`RetryAsync` 仅接受结果明确且暂存文件仍有效的失败项，创建新执行代次。未知结果必须先核对，准备失败需重新提交当前来源。`CancelAsync` 保存取消意图并等待仲裁及实际资源释放：服务器已确认则保留回执，取消先成立则记录墓碑，旧签名上传不能再发布备份；取消结果未知会抛出错误，等待显式核对，不能自动重复发送取消请求。

回执提交与平台实际释放分别记录；数据库提交不明停止依赖工作，不能按失败假设删除可能已交接的文件。凭据回调异常保留原对象。桌面请求截止时间默认2分钟；移动后台遵循系统执行规则。下载仍流式校验到临时文件，校验通过才发布，既有目标不覆盖。

## 暂停、批量操作与后台

`PauseAsync` 在同一事务持久设置全局暂停和未接受准备的停止意图，等待准备者与执行者实际停止 / 释放；取消等待不会撤销这些意图，随后的恢复不会复活已停止的准备。全局暂停不打断已经开始的取消仲裁。未提交 Plan 暂停后仍保留登记阶段；未来 Query 保留检查时点。`ResumeAsync` 解除全局暂停并重新授予执行凭据，单项暂停不被清除。队列暂停 / 恢复互斥；暂停可与准备并行。暂停不等于取消远端提交。

```csharp
string id = await backup.SubmitOperationAsync(new BackupOperationCommand {
    OperationId = Guid.NewGuid().ToString("N"),
    Action = BackupAction.Pause,
    State = BackupState.Queued
}, token);
var progress = await backup.QueryOperationAsync(id, cancellationToken: token);
var finished = await backup.WaitOperationAsync(id, token);
```

操作包括 Resume、Pause、Cancel、Retry、RetryCleanup、ClearHistory。显式目标1–128个，或按状态筛选；不能同时传两者。Selecting 每页最多128个，Running 每页最多32个。筛选按逐页当时状态及提交创建上界进行；选择完毕后目标固定。WaitingForRelease 仍未完成，实际系统资源释放后才能结束。取消 Wait 只停止等待。相同ID相同参数返回原操作，参数不同拒绝；失败阶段不自动重跑；仓库已持久记录某个操作失败时，其余独立操作继续推进。无法确认持久结果或平台同步失败时停止当前驱动，后续 SubmitOperationAsync（包括重复ID）在写入前抛出原故障；QueryOperationAsync 和已完成结果的 WaitOperationAsync 仍可读取，未完成等待观察原故障。Shutdown 尝试全部资源释放后传播同一故障。排除故障并关闭 / 重开服务后继续未失败阶段。

逐项结果包括 Applied、AlreadySatisfied、NotApplicable、Missing、Pending、WaitingForRelease；执行故障由整个操作的 Failed 阶段和 Error 表达。清历史不会级联删除操作结果；明细过期后仍保留持久操作头和幂等依据，查询明确标记 `DetailsExpired`。查询未知ID返回 null。

Android 自动模式使用持久 JobScheduler；用户前台主动发起时，API34以上使用用户主动数据传输 Job，API25–33使用带进度及暂停通知的前台服务。启动失败明确报告，不自动降级；每仓库最多3个工作线程，同时最多4个活跃仓库。凭据通过 AndroidKeyStore 加密保存。iOS 的控制清单与照片都使用文件式后台 URLSession，Keychain 保护凭据；所有会话最多16个系统任务，照片最多14个，为控制请求保留空间，未来 Query 使用 earliestBeginDate。平台只负责系统能力，业务状态转换统一在共享仓库。 同一请求只包含相同范围、代次和网络策略；Wi-Fi-only 与任意网络分别保留一个当前控制名额及一个未来 Query 名额，Plan／Query 轮换也分别持久记录。Android 仍同时执行至多1个控制请求和2张照片，选择已到期且网络允许的请求，取消优先，其余按到期／创建时间排序；已有排队Job仅在模式、网络和唤醒时间满足当前需要时复用，已运行的资源所有者只接收唤醒。iOS 分别交给相应网络约束的会话，仍受总16个系统任务、14张照片及每仓库2张照片的上限约束，满额时等待既有任务释放。

系统允许时，已持久交接的照片可以在 Unity 停止后继续登记、上传和确认；未准备的照片不能凭此后台发现或导出。Android 强行停止、iOS 用户划掉及首次解锁前不承诺继续。切后台、锁屏与系统回收的完整行为仍需真机验收。回调数据库失败不报告成功，iOS 仍释放 completion handler；排除持久错误后显式 `RecoverNativeAsync`，未知服务器结果另行核对。

## 自动备份

```csharp
var automatic = await AutomaticImageBackup.CreateAsync(backup, library);
await automatic.ConfigureAsync(new AutomaticBackupPolicy {
    enabled = true, sourceKind = BackupSourceKind.PhotoLibrary,
    source = "", includeExisting = false, wifiOnly = true,
    scanIntervalSeconds = 300
}, token);
await automatic.RunAsync(applicationToken);
```

自动循环每轮先刷新并核对来源 / 授权范围，再按可用槽位登记候选，每轮最多登记128项，扫描返回不等待文件准备完成。准备与上传独立推进；仓库在预留、接受、执行认领、Plan 提交／启动和上传开始时检查范围代次，失效范围不能开启新的准备或传输。日志过期后的完整核对会分批撤下已移出的待准备来源，并暂停关联任务，全部核对结束后才启用范围；扫描期间新发现的版本不会被当作历史基线排除。照片重新加入范围时会同步任务关联，但不会自动恢复已暂停任务。

任务登记时固定控制范围；相同来源的手动任务、不同自动范围各自保有控制权，共享确认回执不会转移任务归属。Plan / Query / Cancel 每个固定请求只含同一范围、同一代次成员；关闭一个范围不会因控制清单混合而暂停另一个范围。登记后的租约由服务持有到该成员实际准备结束；凭据在封存后、接受前读取，避免长时间等待使用旧令牌。`RunAsync` 的监听创建和使用共用收尾，创建失败后由调用方修正来源并显式重新启动。

Wi-Fi策略需要可验证的提供者；桌面可传自己的 `Func<bool>`。原生与自动备份网络策略必须一致。自动循环消费图库变化并按配置周期完整核对，最小周期30秒。目录需要递归时明确设置 `recursive`。

首次不包含历史时，基线按页保存实际看到的来源版本，再短事务激活；中断未激活集合无排除效力。扫描期间新变化继续按持久日志消费。已发现候选按页判重，不逐张重复打开数据库或查询服务器能力。单张准备失败单独保存，可通过 `GetPreparationFailuresAsync` 分页查看并显式 `RetryPreparationFailuresAsync`；空间不足显示 `IsWaitingForCapacity`，候选保留。

同一 AutomaticImageBackup 的配置、扫描、循环、范围确认、重置和准备失败重试共用一个执行归属，进行期间相互拒绝；结束后解除限制。循环内部直接执行扫描核心。配置入参及每次扫描策略使用快照，不会在异步过程中切换来源；只读分页查询仍可进行。停止循环或修改未来发现策略不撤销已登记请求；迟到错误按请求登记时的来源记录，不会关闭后来配置的新范围。准备结果在后续扫描／循环收取，独立来源失败保留到失败列表，共享错误以 `BackupSubmissionException` 传播并保留原异常。

权限撤销使当前循环失败，并逐页暂停该范围尚未完成的任务；授权代次变化同样先暂停该范围既有任务，等待业务确认后显式恢复。范围成员变化通过移出事件处理，不删除服务器副本。恢复授权后显式 `ConfirmScopeAsync`，它等待该范围旧准备实际结束，再确认新代次；随后扫描重新登记受失权阻断的候选，已接受的暂停任务仍须单独恢复。确认范围只能承认权限类的共享错误，不能掩盖仓库、凭据或清理故障；确认不会使旧登记重新获得准入。`ResetScopeAsync` 在循环停止后显式清除当前范围的基线和来源处置，保留确认回执与已接受任务；中断的重置必须继续完成，不能混入新扫描。

显式刷新先读取并保存权限，再访问原生图库。观察轮询、枚举，或备份准备的版本检查 / 导出期间返回 `PermissionDenied` 时，在同一刷新串行入口内将之前可访问的范围记为不可访问，再传播原错误；即使系统权限随后立即恢复，也不能抹掉这次拒绝访问的事实。重复拒绝不重复推进代次，已记录的 Restricted 等不可访问状态保持原值。iOS 目录枚举、stat 和导出统一保留授权错误：失效／缺失书签要求 `ScopeConfirmationRequired`，授权拒绝为 `PermissionDenied`；PhotoKit 根据当前授权状态及 NSError 的错误域／代码识别失权，即使权限迅速恢复也保留已发生的拒绝。普通删图、Limited 下单项不可见和 iCloud 网络失败不单独推断为整库失权。准备期间失权按整个来源范围处理，未接受任务保留失败事实、候选等待范围确认，不记入单张准备失败列表；单张源文件损坏或消失仍独立记录。权限记录失败作为次级错误报告，不替换原始访问错误；正常空闲轮询不增加权限查询或数据库写入。

## 清理与容量

```csharp
var maintenance = new BackupMaintenance(backup);
var policy = new BackupRetentionPolicy(); // 30天 / 10,000条终结历史
var preview = await maintenance.PreviewAsync(policy, token);
var actual = await maintenance.RunAsync(policy, token);
```

每轮最多200个逻辑项目 / 元数据行，默认100ms软时间片；候选查询、单项原生事务及文件操作不能在任意指令中断。若本轮尚未处理项目，即使查询已耗尽时间片，仍尝试首个候选，避免每轮都只查询而不清理。此后在项目之间检查时间；跳过已变化的候选或隔离文件错误也计为一次尝试，取消及数据库错误仍直接结束。`TimeSliceEnded` 表示实际已达到时间预算，不承诺调用在100ms内结束。预览仅供展示，ReclaimableBytes 是待清理意图的字节上限，可能包含执行者尚未释放的文件；执行前仓库重新检查状态和引用。完成或取消的暂存文件仅在实际执行者与凭据释放后删除；失败 / 暂停 / 未知服务器结果不随成功历史回收。返回实际文件删除数量和字节，数据库删行不冒充物理空间释放。

单文件删除失败只有在仓库可靠保存该文件的故障状态后，才以 `BackupRepositoryException.IsIsolatedCleanupFailure` 报告。C# / Android / iOS 自动清理记录此错误并继续其他文件和上传，不自动重试故障文件；任务的服务器确认状态不因暂存清理失败而撤销。

显式 `BackupMaintenance.RunAsync` 同样继续本批其他项，最后抛出 `BackupCleanupException`；其中 `Result` 包含成功清理统计、时间片状态和逐文件 `Failures`，每项保留文件 ID 与原始仓库异常。可用 `catch (BackupCleanupException error)` 读取部分结果并向用户报告。数据库失败或无法可靠保存故障状态时立即传播，不能假定单文件已隔离。

`QueryCleanupFailuresAsync(pageSize, cursor, token)` 按 FileId 分页返回持久清理故障，包括没有任务记录的准备残留；每页1–200项，游标绑定仓库。记录提供 FileId、可选 TaskId、计入预算的 AccountedBytes、Error 和 UpdatedUtc。`RetryCleanupAsync(fileId)` 仅重新清理指定文件；已入队任务的 FileId 与 TaskId 相同。RetryCleanup 批量任务操作仍只开放选定任务文件的清理资格，由后续清理执行。未记录失败的中断清理意图可以继续，已记录失败不会因查询或维护自动重试。可显式执行非阻塞 checkpoint。回执、排除基线、取消处置和操作ID等必要事实保留，元数据磁盘不承诺永远恒定。

无任务引用的文件元数据随物理文件清理完成一起删除；历史任务仍引用的记录随历史清理删除。扫描历史先定位废弃代次，再按稳定键分批删除其明细，不遍历有效基线。

终结历史只要超过保留数量或超过保留时间就可成为候选，但必须满足全部资源释放条件。数量边界利用状态计数及两个状态的有序索引合并，避免对全部历史排序；先取超过数量的候选，页面未满时才补充过期项。没有过期历史时通过时间索引跳过该分支。返回页和每轮删除有上限，SQL 扫描量仍随保留数量、过期记录分布及被占用的记录数变化，不保证任意数据分布下的固定耗时。

控制请求文件另行记录实际释放和清理故障。`GetControlCleanupFailuresAsync(afterRequestId, pageSize, token)` 分页读取，`RetryControlCleanupAsync(requestId, token)` 只重试选中的故障文件；不影响已确认回执或其他上传。文件删除后清空请求正文，少量诊断记录保留30天再有界删除。历史清理跳过仍关联故障控制文件的任务，避免一个文件故障使整批清理失败。

## 本机参考服务与构建

```sh
python3 Tools~/BackupServer/server.py init --root /absolute/private/backup-v2
python3 Tools~/BackupServer/server.py serve --root /absolute/private/backup-v2
```

`init` 创建私有 credentials.json，仅在运行时提供账号和令牌，不提交Git。开发HTTP需要 `AllowDevelopmentHttp=true` 且地址为本机或明确局域网IP；设备访问电脑不能使用设备自己的127.0.0.1。正式部署使用HTTPS与项目认证。

业务 API 默认8787，独立上传入口8788。业务 Bearer 只发给业务 API；照片 PUT 只使用上传描述授权，空204不带业务回执。服务根据持久待核验记录独立完成确认，不需要客户端 commit；账号内已核验内容可以复用，相同内容的不同来源仍保留各自备份记录。已发布内容不可覆盖，取消墓碑阻止迟到请求重新提交。

上传授权默认24小时，到期后再过24小时由有界维护关闭未完成尝试；尚未关闭的尝试允许显式 Plan 续期，续期与过期关闭原子仲裁，旧候选不会清理续期后的文件；无引用发布对象另有24小时保留期。每轮最多64个清理候选，单文件故障持久隔离后继续其余工作，失败由 `--retry-failed-cleanup` 显式重试。已确认和仍有引用的内容不被临时TTL删除。参考服务不代表生产容量或正式认证方案。

`Runtime/MediaBackup/Native~/build/build.py` 依赖对应目标的已验证 SQLite 构建；`install.py` 校验来源及依赖后安装。二进制位于 `Runtime/MediaBackup/Plugins/{macOS,Windows/x86_64,Android/arm64-v8a,iOS}`。构建处理器检查来源哈希、核心版本，自动复制iOS头文件并配置链接、框架和Android keep规则，无需另建原生App。

服务初始化时创建仅当前用户可读的 `credentials.json`，包含测试账号与随机令牌；通过运行时配置提供令牌，不写入场景、日志或 Git。一个数据目录只允许一个服务进程持有；重启沿用同一目录，`.backup-owner` 持久锁文件不要手工删除。修复磁盘问题后可显式重试已隔离的清理故障：

```sh
python3 Tools~/BackupServer/server.py serve --root /absolute/private/backup-v2 --retry-failed-cleanup
```

手机连接本机服务时，业务地址须用手机可达的局域网 IP；上传 origin 也须设置成相同网络可达的最终地址，例如：

```sh
python3 Tools~/BackupServer/server.py serve --root /absolute/private/backup-v2 \
  --host 0.0.0.0 --storage-origin http://192.168.1.2:8788
```

客户端业务 URL 对应 `http://192.168.1.2:8787`。`0.0.0.0` 只是监听地址，不能作为返回给手机的上传地址。测试时须显式启用开发 HTTP 与平台网络规则；正式服务应接入认证、HTTPS、配额、存储、监控和灾备。本机参考服务和测试不证明生产容量。协议字段见 [OpenAPI](../Tools~/BackupServer/backup-protocol.openapi.yaml)，固定夹具位于 `Tools~/BackupServer/fixtures/protocol-v2.json`。

打开 `Tools/UIFrame/图片与备份` 使用索引分页、共享预览、任务分页 / 批量控制和清理预览；UGUI 示例在 `Samples~/GalleryDemo`，提供任务翻页和预览后清理入口。场景中挂载 `GalleryDemo` 到独立于预览面板的应用级对象，绑定 `SelectOne`、`SelectMultiple`、`ReadAlbums`、`BackupSelected`、`PauseBackup`、`ResumeBackup`、`RefreshBackupStatus` 与 `ClearPreview`；令牌通过 `SetAccessToken` 在运行时提供。Editor 文件窗口只支持单选，多选 / 相册窗口 / 授权目录须在移动端核验。关闭预览不取消已接收任务；结束示例对象会等待 C# 服务停止，已交接的移动后台传输由系统继续管理。退出账号前先暂停备份并等待完成。

虚拟图库示例使用 `LoopVerticalScrollRect`、`GalleryVirtualListDemo` 和 `GalleryThumbnailCell`。列表需配置 Viewport / RectMask2D、Content、固定尺寸单元与 RawImage；应用创建并持有 `ImageLibraryIndex` 和 scope 后调用 `InitializeAsync(library, scope)`。`ShowFirstPageAsync` / `ShowNextPageAsync` 查询最多200项元数据，`HasNextPage` 控制翻页；滚动重绑取消旧加载，离屏释放缩略图租约。关页先等待 `list.ShutdownAsync()`，再关闭图库索引。后台加载失败会写 Unity 日志，不伪装成空图片；5000张连续滚动的设备帧预算仍需产品场景验收。

## 接收、索引与内存补充

- 后台交接分为认领、凭据持久化后的 Handoff、系统受理；每个控制请求和照片传输分别记录系统绑定与执行阶段。系统只执行已交接代次；C#关闭或失败不能代替平台宣告凭据已释放。文件统一由共享仓库 Seal 刷盘，再由 AcceptItem 逐项接受。
- `ReadChangesAsync` 在单个只读 SQL 快照中读取身份、权限代次、保留水位和最多200条变化；日志被截断或范围变代时返回 `RequiresRefresh`，不把空页误当已消费。
- `ImageReference.FromFile` 是便宜的文件元数据引用；持久目录索引在此基础上保存内容哈希证明。完整枚举与文件增量均排除符号链接／重解析点及其子项，普通文件变成链接后会移出索引。首次扫描、重开、监听溢出、重新获得焦点或显式 `RequestRefresh` 后重新核对内容；连续监听下的例行完整枚举可复用已核对且 stat 未变的版本，已通知路径始终重新核对。哈希使用池化128 KiB缓冲和工作线程。索引版本用于缩略图键、备份判重及接收校验；目录备份复制前后检查文件元数据，复制时计算的哈希直接与索引版本比较，不在复制前后再次完整读取源文件。普通 `FromFile` 引用只有元数据检查，若需内容证明应使用索引返回的引用；不要自行截断版本字符串。复制器关闭流后由原生 Seal 统一刷盘发布；上传前对暂存文件及下载时的哈希校验仍保留。
- 图库索引保留提供者已知的字节大小，并贯通分页、变化日志与自动候选；未知大小保持未知。图库只接受 schema 3，不再提供 v1 到 v2 的字段升级；不支持的版本明确拒绝，原文件保留。
- Android SAF 枚举和 stat 使用相同的版本规则：缺少修改时间／大小、修改时间为0或大小为负时，枚举不返回版本，stat 返回 `ContentVersionUnavailable`。依赖版本的缓存和备份不能使用占位字符串，直接预览仍按自身读取契约执行。
- Android 外部媒体通知先规范化为枚举使用的 external 图片身份；非图片/未知卷通知要求完整核对。扫描保存开始时的提供者版本/各卷 generation 与观察序号，完成前再次比较。iOS 扫描复用观察者持有的 PHFetchResult，比较观察修订；观察者重建必须完整核对，不把进程内序号当作可跨进程恢复的 PhotoKit token。扫描期间边界变化以 `LibraryChangedDuringScan` 失败，业务显式发起新扫描。
- Limited 的不确定通知、观察者重建或恢复前台只要求完整核对。完成核对时按来源身份排序、每页200项计算可见成员摘要并持久保存；只有授权状态改变，或已核对的 Limited 成员集合改变，才推进权限代次。照片内容或名称变化不会改变成员摘要，相同集合在观察重建、确认后再次扫描或索引重开时不会重复要求确认。Limited 下移出记录为 `AccessChanged`，不推定原图已被删除。范围变化后自动循环抛出 `ScopeConfirmationRequired` 并仅暂停该范围未完成任务。停止并等待循环后，业务向用户展示当前可见范围，再调用 `await automatic.ConfirmScopeAsync(token)`。此调用允许后续扫描，不恢复既有暂停任务；恢复任务仍由业务显式决定。不会自动弹权限窗口。
- 每次开始/完成图库完整核对，都分批清理最多200条废弃或非当前完成扫描。当前完成代次和进行中的扫描始终保留；查询有 scope/phase 与 phase/id 索引。

`ImageThumbnailCache.Statistics` 的 IdleBytes / InUseBytes 属于该缓存。PendingDestroyBytes 覆盖进程内本图片管线经 ImageTexture.Destroy 移交 Unity 的纹理，包括取消、失效和变换临时纹理；预计下一帧释放后减记。EstimatedInFlightDecodeBytes 是当前串行解码的保守工作量估算，不是原生堆实测值，排队请求不重复计入。平台解码器内部、驱动及 RenderTexture 池仍需 Unity Profiler / 系统工具测量；这些数字不能相加得出进程硬上限。Shutdown 只等待本缓存所属请求及销毁移交，不等待无关缓存的生命周期。

`Samples~/GalleryDemo/GalleryVirtualListDemo.cs` 使用现有 LoopVerticalScrollRect，离屏释放租约，重绑隔离旧请求。示例每页最多200条元数据，支持下一页；业务可按产品需要组合分页导航，不能把这个示例视为已完成5000张连续滚动的设备验收。
