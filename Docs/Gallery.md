# 图片、相册与备份使用说明

实现入口位于 `Game.Media` 与 `Game.Media.Backup`。系统选图不依赖 `UI.Init()`；创建 Texture / Sprite、调用原生桥接及备份服务入口要求 Unity 主线程。

## 当前支持情况

| 功能 | 当前实现 |
| --- | --- |
| Android 系统选图 | API 33+ Photo Picker；25–32 系统文档选择器；单选 / 多选 |
| iOS 系统选图 | PHPicker；最低功能版本 iOS 14，当前宿主为 iOS 15 |
| Unity Editor 选图 | 单张文件窗口；显式多文件导入可用 |
| 相册访问与查询 | Android MediaStore / iOS PhotoKit；权限查询、请求、相册及图片元数据快照 |
| 普通目录 | 文件枚举、可选递归、快照分页；默认跳过符号链接 |
| 外部目录 | Android SAF 持久 URI / iOS 目录书签；显式选择、恢复与枚举 |
| 预览 / 文件导出 | 移动端原生缩采样；PNG / JPEG 转码；保留提供者字节 |
| 手动备份 | 持久文件与任务、分片上传、服务端校验、去重、下载验证 |
| 自动备份 | 应用运行期间的目录 / 授权相册扫描、历史基线、持久策略、条件上传 |
| 原生后台传输 | Android JobScheduler / iOS 后台 URLSession；显式启用后传输已入队文件，系统决定执行时机 |
| 后台发现新照片 | 尚未实现；`SupportsBackgroundDiscovery` 返回 false |

相册目前一次获取元数据快照，再对快照分页；尚未提供系统变化通知和跨页面共享的缩略图缓存。图库变化后请重新查询；旧引用读取失败会传播。外部目录需要实际设备验证授权的持久性，不保证所有文件提供者都支持相同能力。

实施计划中的后台发现、变化通知、有界共享缓存及完整真机矩阵仍是后续工作，不能把已有编译或本机测试当作这些功能已完成。

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

移动端按目标尺寸原生解码。Editor / 桌面使用有界的托管预览，只接受 JPEG / PNG，源像素上限为 16 MP；大于上限明确报 `ImageTooLarge`。Unity 预览阶段读入的编码文件上限为 128 MiB，限制的是预览缓冲，不限制独立原文件导出。托管预览会完整解码上限内的源图，然后缩小，不承诺与移动端相同的峰值内存。预览 `MaxEdge` 范围为 1–8192，默认 2048。

“保留原图”指保留系统提供者交付的字节，不承诺是最初相机文件。JPEG 转码默认质量 90、透明区域白底；可通过 `ImageExportOptions` 显式修改。

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

授权失效、书签过期或文件被删除会明确失败，业务可由用户主动重新选择目录。目录扫描与照片库查询都不修改或删除用户原图。

## 本机备份服务

需要 Python 3.9 或更高版本，无额外包。以下命令在包目录运行，数据目录可替换为自己的绝对路径：

```sh
python3 Tools~/BackupServer/server.py init --root /tmp/uiframe-backup-local
python3 Tools~/BackupServer/server.py serve --root /tmp/uiframe-backup-local
```

默认仅监听 `127.0.0.1:8787`。初始化生成 `credentials.json`，包含本机账号与随机访问令牌；把它填入编辑器调试窗口，不要提交该文件。服务使用 SQLite、磁盘内容文件和 Bearer 认证；客户端显式允许开发 HTTP 后才能连接本地 HTTP 地址。

手机上的 `127.0.0.1` 指手机本身。真机联调需显式让服务监听可访问的本机网卡地址，配置客户端为电脑局域网地址，并配置开发构建的 HTTP 访问规则；本包不替项目放开所有明文网络。正式接入使用 HTTPS 和项目认证服务。

这是单进程本机参考服务：串行保护存储、支持内容去重和账号隔离，不包含生产配额管理、分布式存储、令牌生命周期、运维监控或多副本容灾。服务数据库及对象文件应由部署方一起备份。它提供真实存储和下载验证，但不是已部署的线上服务。

协议 v1：

| 方法与路径 | 职责 |
| --- | --- |
| `GET /v1/capabilities` | 协议版本、认证账号、分片与文件上限 |
| `POST /v1/uploads` | 以 key / sha256 / size / source 建立或恢复上传 |
| `GET /v1/uploads/{key}` | 查询已确认偏移与提交结果 |
| `PUT /v1/uploads/{key}?offset=N` | 顺序写入最多 4 MiB 分片 |
| `PUT /v1/uploads/{key}/background` | 系统后台完整文件上传，SHA-256 校验后原子提交；需账号摘要头 |
| `POST /v1/uploads/{key}/commit` | 完整校验并提交备份记录 |
| `GET /v1/backups` | 列出当前账号的已提交记录 |
| `GET /v1/backups/{key}/content` | 下载校验 |
| `DELETE /v1/uploads/{key}` | 放弃未完成会话；不删除已完成备份 |

目前单文件服务上限 512 MiB。不完整上传不会完成；客户端会保留错误及本地文件。未完成的服务端会话尚无自动过期回收，部署方应管理其存储。客户端取消保留会话身份，不自动声明远端没有副本。

## 客户端持久队列

```csharp
using Game.Media.Backup;

var backup = new ImageBackupService(new BackupConfiguration
{
    ServerUrl = "http://127.0.0.1:8787",
    Account = "local-user",
    StorageDirectory = System.IO.Path.Combine(Application.persistentDataPath, "MyBackup"),
    AccessToken = () => tokenFromYourCredentialProvider,
    AllowDevelopmentHttp = true,
    EnableTransientRetries = true
});

var taskIds = await backup.EnqueueAsync(selection.Items, token);
// 此后 selection 可释放，面板关闭不影响已接受任务的持久文件。
await backup.ProcessAsync(token);
var tasks = backup.GetTasks();
await backup.DownloadAndVerifyAsync(taskIds[0], absoluteNewFilePath, token);
// 应用服务结束时：取消在途操作，等待结束并释放仓库。
await backup.ShutdownAsync();
```

一个存储目录只允许一个服务拥有，并固定绑定服务器与账号；不能换账号后接着使用旧队列。C# 队列 JSON 不保存访问令牌，由调用方提供认证服务 / 安全存储。启用原生传输时按下节规则保存用于已提交任务的凭据。

`EnqueueAsync` 对一个批次先准备全部文件，成功提交后才返回 ID；失败不交付半批任务。队列采用批次目录原子移交、任务 JSON 原子替换和单写入者规则。文件预算默认 512 MiB；前台与 Android 上传并发为 1，iOS 每个网络会话限制同主机连接为 2；仅数据目录中的模块自有文件会被清理。

`ProcessAsync` 处理当前符合条件的任务一轮，任务级失败保存在对应记录中，其他独立任务可以继续。服务不可用、认证账号与配置不匹配、队列持久化失败等公共前置 / 仓库故障仍向调用方抛出。调用方必须检查每项状态，不能把 `ProcessAsync` 正常返回视为所有图片成功。

支持 `PauseAsync`、`Resume`、`Retry(taskId)`、`Cancel(taskId)`。取消单任务与手动重试要求当前没有处理 / 接收工作，先暂停并等待；这是当前单写入调度方式的显式限制。上传完成后先持久记为 Completed 再清理本地文件；清理失败不反向改写已提交结果。

可选有限重试只处理临时传输错误，最多 5 次，基础等待为 5 秒、30 秒、2 分钟、10 分钟、30 分钟，并考虑服务端 Retry-After。后续 Process / 自动循环在到期后再执行。认证拒绝、校验错误和格式 / 文件限制不会无限重试。未开启该策略时，失败需用户显式重试。

## 移动端后台上传

原生代码随 Unity 构建：Android 使用 `.androidlib` 源码和系统 JobScheduler，不需额外导出 AAR 或引入 AndroidX；iOS 自动编译 `.mm`，链接 Security 框架，通过 Unity `AppDelegateListener` 接收后台 URLSession 回调，无需手改导出的 AppDelegate。

```csharp
var backup = new ImageBackupService(new BackupConfiguration
{
    ServerUrl = yourServerUrl,
    Account = yourAccount,
    StorageDirectory = Path.Combine(Application.persistentDataPath, "PhotoBackup"),
    AccessToken = () => yourRuntimeToken,
    EnableNativeBackgroundTransfer = true, // Android / iOS Player；Editor 会明确拒绝
    NativeWifiOnly = true,
    EnableTransientRetries = false
});
await backup.EnqueueAsync(selection.Items, applicationToken);
await backup.ProcessAsync(applicationToken); // 完成文件交接，不代表已备份
// 可切去其他应用；回来后读取原生层持久保存的结果。
var tasks = backup.GetTasks();
await backup.PauseAsync(); // 原生取消回调完成后才返回；保留待恢复文件
backup.Resume();
await backup.ProcessAsync(applicationToken);
```

- `SupportsNativeBackgroundTransfer` 报告平台能力；`UsesNativeBackgroundTransfer` 表示本服务实际启用了该模式，默认关闭。先检查服务端 `backgroundUpload: true`，不支持时明确失败，不自动改用前台传输。
- 入队时仍需前台准备文件；`ProcessAsync` 在前台认证并建立上传会话，然后先持久保存交接意图，再提交原生层。每个任务只有一个上传执行者，Unity 不在暂停时启动第二套上传。
- Android JobScheduler 持久调度，受系统后台配额、省电模式和厂商策略限制；系统中断后可重调度。iOS 后台 URLSession 使用文件上传，系统唤醒时由原生代码保存结果并完成系统回调。用户强制停止 / 划掉应用后不保证继续，重新打开后调用 `ProcessAsync` 恢复调度；系统也不承诺即时开始。
- `NativeWifiOnly=true`：Android 检查可用的非计费 Wi-Fi；iOS 系统会话禁止蜂窝、昂贵与低数据模式网络（非计费有线网络也可能可用）。这不是跨平台精确的零蜂窝字节保证。自动扫描策略 `wifiOnly` 必须与原生配置一致，改变网络策略前先暂停旧任务，再创建相同目录的新配置服务并 `Resume/ProcessAsync`。C# `canTransfer` 回调无法在 Unity 暂停后执行，因此原生模式拒绝此参数。
- 原生队列只传输已准备文件，不负责后台扫描新照片。Unity 服务对象销毁、`ShutdownAsync`、`ProcessAsync` 的 token 取消都不取消已经交给系统的任务；它们只停止 C# 工作。退出账号前必须显式 `PauseAsync`，等待原生释放，再关闭服务。要放弃任务用 `Cancel(id)`；取消会先记录状态，`GetTasks` 对账确认原生释放后才删除本地副本。取消不会删除已提交的服务器备份，也无法撤回可能刚完成的服务器提交。
- 暂停时暂不接受新批次或新上传；已在准备文件的批次须先取消并等待。任务 JSON 与原生任务记录分别由各自唯一写入者维护；`nativeOwned` 是持久交接标记。尚无原生记录的交接意图可再次提交；已有记录只唤醒原任务。暂停 / 失败后的重试必须等旧任务释放，禁止旧回调与新任务共用文件。重新创建服务不会把原生上传改成前台上传，也不会清理原生仍占用的文件。
- Android 凭据由 AndroidKeyStore 的 AES-GCM 密钥加密，保存在不参与系统备份的私有目录；iOS 使用设备专属 Keychain，URLSession 另持有请求头。暂停、取消、完成时移除插件保存的凭据。任务只能使用提交时的令牌，不能在后台调用 C# 刷新认证；401/403 进入 `NeedsAttention`，业务更新令牌后显式 `Retry(id)`、`ProcessAsync`。
- 原生传输失败不会自动进行业务重试；`EnableTransientRetries` 与原生模式组合会被拒绝。系统等待网络或中断恢复属于系统调度。断线后的完整文件可能从头重传，服务端按固定 key 幂等提交；当前原生路径没有分片断点续传承诺。只有服务端返回匹配的 key / SHA-256 / 大小及提交结果才进入 `Completed`；发送字节不算已确认进度。
- 文件预算约束插件暂存文件，不包含 iOS 系统可能建立的上传副本。请保持服务目录与原生存储，任务释放前不要手动清理。原生全局队列损坏会报错并停止相应恢复工作，不自动清空数据。

本机验证需要让手机访问电脑局域网地址，不能使用手机自己的 `127.0.0.1`。本机 HTTP 同时需要 `AllowDevelopmentHttp=true` 和宿主的开发网络配置；iOS 局域网用途说明与系统授权、Android 明文网络策略由宿主配置。发布服务使用 HTTPS。服务端协议新增 `PUT /v1/uploads/{key}/background`，请求头 `X-Backup-Account-SHA256` 为配置账号 UTF-8 的 SHA-256，正文是原始文件，认证仍是 Bearer；返回值与 commit 相同。服务器必须先校验完整文件再提交，禁止仅收到请求就回复 completed。

## 自动扫描

```csharp
var automatic = new AutomaticImageBackup(backup);
automatic.Configure(new AutomaticBackupPolicy
{
    enabled = true,
    sourceKind = BackupSourceKind.PhotoLibrary,
    source = null,                 // 或具体相册 ID
    includeExisting = true,
    wifiOnly = true,
    scanIntervalSeconds = 300
});
await automatic.RunAsync(applicationLifetimeToken);
```

`Directory` 来源使用绝对目录；`GrantedDirectory` 来源使用保存的目录书签。默认自动策略关闭，移动端 Wi-Fi 条件使用平台确认的可用、非计费 Wi-Fi 网络。Editor 可注入真实网络策略检测器，或在测试中显式关闭 Wi-Fi 限制；不以 Unity 通用网络可达性冒充 Wi-Fi。

网络条件在扫描与提交分片前检查，不承诺网络切换瞬间零流量。更改策略前取消并等待旧循环；关闭自动策略不会删除已备份副本。首次选择不含历史照片时建立元数据基线，后续新增 / 版本变化入队；扫描通知或持久记录不能证明用户未授权照片也被备份。

这是**应用运行期间的自动发现**；移动端启用原生传输后，已提交文件可以由系统继续上传。`SupportsBackgroundDiscovery` 仍为 false。来源权限错误会结束自动循环并保留错误，需要业务提示用户处理；不会后台反复请求权限。当前来源范围缩小尚无原生变更通知，业务应在授权变化时取消扫描、暂停队列并重新确认范围。

## 验证与示例

- 编辑器调试：**Tools → UIFrame → 图片与备份**，可选择图片、浏览目录、上传与下载校验。
- Runtime 示例：从 Package Manager 导入“图片选择与备份示例”，将 `GalleryDemo` 挂在应用服务对象上，配置 RawImage / Text 并绑定 UGUI 按钮；访问令牌运行时通过 `SetAccessToken` 提供。
- Unity 常规测试：`UIFrame.Regression.MediaTests`。
- 真实 HTTP 测试：先运行 `python3 Tools~/BackupServer/integration_server.py`，再显式运行 `RealServerRoundTripAndIdempotentResubmission`。这是独立的临时测试服务和固定测试账号，不使用真实凭据。
- 服务端回归：在 `Tools~/BackupServer` 运行 `python3 -m unittest -v test_server.py`。

移动端原生编译、Unity 构建、真机权限与系统窗口行为是不同验证层次。当前实测状态见 [GalleryValidation.md](GalleryValidation.md)，总体后续任务见 [GalleryImplementationPlan.md](GalleryImplementationPlan.md)。
