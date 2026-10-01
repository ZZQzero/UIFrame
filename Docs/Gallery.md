# 图片、图库与长期备份

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

构建与真机验收状态见 [GalleryValidation.md](GalleryValidation.md)。计划中的性能数字为目标，不能从桌面编译或缓存预算推断手机峰值内存。

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

选图结果释放后不能发起新读取；已经受理的读取使用短期租约。所有权释放不等于取消在途操作，取消需传 Token。面板作用域登记失败时，调用方仍负责释放尚未移交的结果。

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
await library.RefreshAsync(scope, token);
var first = await library.QueryAsync(scope, pageSize: 100, token: token);
var next = first.Next == null ? null : await library.QueryAsync(scope, 100, first.Next, token);
using var subscription = library.Watch(scope, batch => { /* 更新可见页面 */ });
```

照片库授权由 `RequestLibraryAccessAsync` 显式请求；刷新、监听和自动备份不会主动弹授权框。索引每页最多200条，每次写入最多32张元数据。游标绑定库身份、代次、范围修订及权限代次；变化日志被截断或范围变化时返回 `RequiresRefresh`，旧分页游标明确失败。

`Watch` 使用300ms安静窗口、最长2s合并连续事件，刷新不并发重入。普通目录分别监听图片文件和目录结构，忽略数据库等非图片文件；最多保留1,024个变化身份，溢出要求完整核对。Android使用 ContentObserver，支持系统版本时同时检查 MediaStore version / generation；iOS使用 PhotoKit fetch result 的增量变化。重开、恢复前台、权限或连续性变化触发完整核对。增量读取和系统权限仍需真机验收。

每个索引最多16个观察范围，每范围32个订阅。失败只停止对应范围的自动驱动，`GetWatchFailure` 可查询；调用 `RequestRefresh` 显式重新开始。完整枚举失败不提交缺失删除；已成功写入的页面不回滚。订阅回调异常保留错误，不重放该回调。`PruneChangesAsync` 每次最多清200条日志，旧消费者必须核对刷新。

应用服务退出时 `await library.ShutdownAsync()`，面板仅释放订阅。索引与账号备份库分别管理生命周期。

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
    NativeWifiOnly = true
});
string operationId = Guid.NewGuid().ToString("N"); // 调用方在提交前保存
var ids = await backup.EnqueueAsync(operationId, selection.Items, token);
await backup.ProcessAsync(token);
```

`StorageDirectory` 是根目录。实际路径由标准化服务器地址和账号的 SHA-256 分隔；后台模式必须使用平台私有根，让系统能在 Unity 启动前定位 catalog。服务器、账号与目录身份不匹配明确失败。使用异步工厂，没有同步构造或全量 `GetTasks` 兼容入口。

一次原子准备批次为1–32张，大量照片分批提交。先持久记录准备ID和字节预留，再流式复制、SHA-256、刷盘和检查来源版本，全部就绪后一次事务接受整批。默认暂存预算与单文件上限均为512MiB，另保留16MiB文件系统余量。未知长度按剩余预算预留；失败的部分文件也有持久清理归属。空间不足保留自动发现候选，不删除原图或未备份文件。

接受结果不明时，保留原 OperationId，使用 `QueryPreparationAsync` 查询 Accepted / Abandoned 和原任务ID；不要另造ID重发。已接受ID在任务历史删除后仍可核对。普通 `EnqueueAsync(images, token)` 适合无需跨中断核对的临时交互。

数据库参数 / 结果每次上限1MiB，返回最多200行；原生最多16条并行准入命令、64个附件。一个 catalog 只有一个准备者，原生后台附件独立保有资源；C#关闭不会取消系统已受理传输。`ShutdownAsync` 等待自身操作结束后释放，`Dispose` 仅允许无活动操作时调用。

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

任务游标包含仓库、筛选条件及创建上界；状态实时变化，不宣称跨页冻结快照。汇总按状态持久计数，不加载历史字典。`ReadChangesAsync` 返回有序任务变化页；日志截断返回 `RequiresRefresh`。回执与来源版本关系独立保存，清理任务历史不丢失下载依据或自动判重事实。

状态成功仅来自服务器确认身份、大小和 SHA-256，随后由共享仓库原子记录。HTTP响应丢失或格式错误不能证明服务器没提交，任务进入 `NeedsAttention`。`ReconcileTaskAsync` 按原幂等身份查询；已完成则保存回执，未完成则通过与提交互斥的服务端删除会话取得确定结果。失败保留待核对状态。`RetryAsync` 是显式再执行，继续使用原内容幂等身份，不自动删除未知结果。

已声明的可选托管暂时故障重试最多5次，默认关闭；不与原生后台模式组合。所有网络请求有完整响应读取截止时间，默认2分钟；响应上限64KiB、分片上限4MiB。凭据回调异常终止当前处理并保留原异常。下载流式校验到临时文件，校验通过才发布，既有目标不覆盖。

## 暂停、批量操作与后台

`PauseAsync` 持久设置全局暂停并等待执行者停止 / 释放；取消等待不会撤销已经保存的暂停。`ResumeAsync` 只解除全局暂停。单项暂停不会被全局恢复清除；未知服务器结果需要核对，暂停并不等于取消远端提交。

```csharp
string id = await backup.SubmitOperationAsync(new BackupOperationCommand {
    OperationId = Guid.NewGuid().ToString("N"),
    Action = BackupAction.Pause,
    State = BackupState.Queued
}, token);
var progress = await backup.QueryOperationAsync(id, cancellationToken: token);
var finished = await backup.WaitOperationAsync(id, token);
```

操作包括 Resume、Pause、Cancel、Retry、RetryCleanup、ClearHistory。显式目标1–128个，或按状态筛选；不能同时传两者。Selecting 每页最多128个，Running 每页最多32个。筛选按逐页当时状态及提交创建上界进行；选择完毕后目标固定。WaitingForRelease 仍未完成，实际系统资源释放后才能结束。取消 Wait 只停止等待。相同ID相同参数返回原操作，参数不同拒绝；失败阶段不自动重跑；仓库已持久记录某个操作失败时，其余独立操作继续推进。无法确认持久结果或平台同步失败时停止当前驱动，等待者观察原错误，排除故障并关闭 / 重开服务后继续未失败阶段。

逐项结果包括 Applied、AlreadySatisfied、NotApplicable、Missing、Failed、Pending、WaitingForRelease。清历史不会级联删除操作结果；明细过期后仍保留持久操作头和幂等依据，查询明确标记 `DetailsExpired`。查询未知ID返回 null。

Android使用 JobScheduler 和 AndroidKeyStore，iOS使用后台 URLSession 和 Keychain；平台只负责调度、传输、凭据和实际资源释放，SQL及状态转换只有一份。iOS同时最多16个系统上传任务。上传切后台尽力继续；没有系统执行时间保证，用户强制停止或系统限制需重新打开应用。后台回调数据库失败不会报告成功，iOS仍释放系统 completion handler；排除持久错误后可显式 `RecoverNativeAsync`，未知服务器结果另行核对。

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

Wi-Fi策略需要可验证的提供者；桌面可传自己的 `Func<bool>`。原生与自动备份网络策略必须一致。自动循环消费图库变化并按配置周期完整核对，最小周期30秒。目录需要递归时明确设置 `recursive`。

首次不包含历史时，基线按页保存实际看到的来源版本，再短事务激活；中断未激活集合无排除效力。扫描期间新变化继续按持久日志消费。已发现候选按页判重，不逐张重复打开数据库或查询服务器能力。单张准备失败单独保存，可通过 `GetPreparationFailuresAsync` 分页查看并显式 `RetryPreparationFailuresAsync`；空间不足显示 `IsWaitingForCapacity`，候选保留。

权限撤销使当前循环失败，并逐页暂停该范围尚未完成的任务；授权代次变化同样先暂停该范围既有任务，等待业务确认后显式恢复。范围成员变化通过移出事件处理，不删除服务器副本。恢复授权后显式确认范围再恢复对应任务。`ResetScopeAsync` 在循环停止后显式清除当前范围的基线和来源处置，保留确认回执与已接受任务；中断的重置必须继续完成，不能混入新扫描。

## 清理与容量

```csharp
var maintenance = new BackupMaintenance(backup);
var policy = new BackupRetentionPolicy(); // 30天 / 10,000条终结历史
var preview = await maintenance.PreviewAsync(policy, token);
var actual = await maintenance.RunAsync(policy, token);
```

每轮最多200个逻辑项目 / 元数据行，默认100ms时间片；单项原生事务/文件操作不能在任意指令中断。预览仅供展示，ReclaimableBytes 是待清理意图的字节上限，可能包含执行者尚未释放的文件；执行前仓库重新检查状态和引用。完成或取消的暂存文件仅在实际执行者与凭据释放后删除；失败 / 暂停 / 未知服务器结果不随成功历史回收。返回实际文件删除数量和字节，数据库删行不冒充物理空间释放。

清理失败持久保存，不自动重试；调用 `RetryCleanupAsync` 或 RetryCleanup 批量操作。未记录失败的中断清理意图可以继续。可显式执行非阻塞 checkpoint。回执、排除基线、取消处置和操作ID等必要事实保留，元数据磁盘不承诺永远恒定。

## 本机参考服务与构建

```sh
python3 Tools~/BackupServer/server.py init --root /absolute/private/backup
python3 Tools~/BackupServer/server.py serve --root /absolute/private/backup
# 显式启用未完成会话7天过期：
python3 Tools~/BackupServer/server.py serve --root /absolute/private/backup --upload-ttl-days 7
```

`init` 创建私有 credentials.json，仅在运行时提供账号和令牌，不提交Git。开发HTTP需要 `AllowDevelopmentHttp=true` 且地址为本机或明确局域网IP；设备访问电脑不能使用设备自己的127.0.0.1。正式部署使用HTTPS与项目认证。

TTL仅适用于未完成上传。上传、校验或提交中的会话不能被回收；先事务认领，禁止继续使用，再删除分片并提交完成。中断恢复同一清理意图，失败需要 `--retry-failed-cleanup` 显式处理。旧会话返回410，重新登记是显式操作，幂等身份保持；已完成副本和共享blob不受TTL影响。此服务为单进程本机参考实现，生产配额、认证、部署和灾备由项目配置。

`Runtime/MediaBackup/Native~/build/build.py` 依赖对应目标的已验证 SQLite 构建；`install.py` 校验来源及依赖后安装。二进制位于 `Runtime/MediaBackup/Plugins/{macOS,Windows/x86_64,Android/arm64-v8a,iOS}`。构建处理器检查来源哈希、核心版本，自动复制iOS头文件并配置链接、框架和Android keep规则，无需另建原生App。

打开 `Tools/UIFrame/图片与备份` 使用索引分页、共享预览、任务分页 / 批量控制和清理预览；UGUI示例在 `Samples~/GalleryDemo`，提供任务翻页和预览后清理入口。设备能力与未完成验收见 [验证记录](GalleryValidation.md)，原生命令语义见 [RepositoryContract.md](../Runtime/MediaBackup/Native~/include/RepositoryContract.md)。

## 2026-10-01 接收、索引与内存契约补充

- 后台交接分为认领、凭据持久化后的 Handoff、系统任务绑定、Start。系统只查询已交接代次；未交接的终结代次仅用于释放资源。C# 关闭或失败不能代替平台宣告凭据已经释放。接收文件统一由共享仓库 Seal 刷盘，再由 Accept 发布任务。
- `ReadChangesAsync` 在单个只读 SQL 快照中读取身份、权限代次、保留水位和最多200条变化；日志被截断或范围变代时返回 `RequiresRefresh`，不把空页误当已消费。
- `ImageReference.FromFile` 是便宜的文件元数据引用；持久目录索引在此基础上保存内容哈希证明。首次扫描、重开、监听溢出、重新获得焦点或显式 `RequestRefresh` 后重新核对内容；连续监听下的例行完整枚举可复用已核对且 stat 未变的版本，已通知路径始终重新核对。哈希使用128 KiB缓冲和工作线程。索引查询返回的内容版本同时用于缩略图键、备份判重和准备前后校验；不要自行截断版本字符串。
- Android 外部媒体通知先规范化为枚举使用的 external 图片身份；非图片/未知卷通知要求完整核对。扫描保存开始时的提供者版本/各卷 generation 与观察序号，完成前再次比较。iOS 扫描复用观察者持有的 PHFetchResult，比较观察修订；观察者重建必须完整核对，不把进程内序号当作可跨进程恢复的 PhotoKit token。扫描期间边界变化以 `LibraryChangedDuringScan` 失败，业务显式发起新扫描。
- Limited 状态不能证明授权集合未变。iOS 集合成员变化、Android 无法区分的 Limited 通知，以及观察者重建/回到前台后的 Limited 范围均按“授权范围需要核对”处理：推进权限代次，旧游标失效。Limited 下移出记录为 `AccessChanged`，不推定原图已被删除。自动循环抛出 `ScopeConfirmationRequired` 并仅暂停该范围未完成任务。停止并等待循环后，业务向用户展示当前可见范围，再调用 `await automatic.ConfirmScopeAsync(token)`。此调用允许后续扫描，不恢复既有暂停任务；恢复任务仍由业务显式决定。不会自动弹权限窗口。
- 每次开始/完成图库完整核对，都分批清理最多200条废弃或非当前完成扫描。当前完成代次和进行中的扫描始终保留；查询有 scope/phase 与 phase/id 索引。

`ImageThumbnailCache.Statistics` 的 IdleBytes / InUseBytes 属于该缓存。PendingDestroyBytes 覆盖进程内本图片管线经 ImageTexture.Destroy 移交 Unity 的纹理，包括取消、失效和变换临时纹理；预计下一帧释放后减记。EstimatedInFlightDecodeBytes 是当前串行解码的保守工作量估算，不是原生堆实测值，排队请求不重复计入。平台解码器内部、驱动及 RenderTexture 池仍需 Unity Profiler / 系统工具测量；这些数字不能相加得出进程硬上限。Shutdown 只等待本缓存所属请求及销毁移交，不等待无关缓存的生命周期。

`Samples~/GalleryDemo/GalleryVirtualListDemo.cs` 使用现有 LoopVerticalScrollRect，离屏释放租约，重绑隔离旧请求。示例每页最多200条元数据，支持下一页；业务可按产品需要组合分页导航，不能把这个示例视为已完成5000张连续滚动的设备验收。
