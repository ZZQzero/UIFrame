# SQLite 与照片长期备份再次完整审查

日期：2026-10-01；审查基线：`4fc7328`。本记录保留两轮审查时的触发条件、原代码位置和证据，并在末尾记录随后按用户要求完成的修复。R1–R12 正文描述修复前行为，不代表当前缺陷仍然存在。

对照文件：`Runtime/Sqlite/ImplementationPlan.md`、`Docs/GalleryLongTermBackupPlan.md`；审查范围包括通用存储、两套业务 schema、图片索引与缓存、自动备份、批量操作与清理、Android / iOS 适配、本机服务器、样例、构建与验证记录。

**当前结论：R1–R12 均已完成代码修复，桌面回归及移动发布构建通过；两份计划的商业发布验收仍未全部完成。** Android 系统调度和 iOS PhotoKit 的真机矩阵、Windows 实际运行、最低设备性能及长时验收仍需补齐。修复和证据边界见末尾“修复结果”。

前两轮只增加审查记录；本次按后续修复指令修改产品代码、回归用例、接口文档及四平台仓库产物，没有将未执行的平台 / 长时验收标为完成。

## 第一轮发现：R1–R6

### R1 / P1：自动循环先传输，后核对授权范围

位置：`Runtime/MediaBackup/AutomaticImageBackup.cs:247`。

`RunAsync` 先执行 `service.ProcessAsync`，随后才进入 `ScanOnceAsync` 更新图库并比较 `permission_generation`。因此，停机期间授权范围发生变化，或已观察到范围变化但本轮尚未消费时，已经准备好的任务可以先上传，之后才抛出 `ScopeConfirmationRequired`。观察者只负责记录变化，不保证在传输之前完成暂停。

本轮隔离 Unity 用例：先扫描生成一个 Queued 任务，推进 library 的授权代次，再调用真正的 `RunAsync`。最终收到 `ScopeConfirmationRequired`，但任务已经是 Completed；预期的 Paused 断言失败。本机服务器实际接收了上传。原有用例直接调用 `ScanOnceAsync`，没有覆盖这个启动顺序。

修改方向：将范围核对及相关暂停作为自动传输的前置步骤，明确发现、准备和传输的顺序；恢复前台和重新启动也遵守同一入口。范围确认不自动恢复旧暂停任务。处理边界按范围及任务归属确定，不因为一个范围变化就无条件阻止其他独立工作。

对应计划：L3、C07；授权变化时暂停相关自动发现与未完成传输。

### R2 / P1：Android 交接中断后不能完成系统绑定

位置：`Runtime/Plugins/Android/UIFrameGallery.androidlib/src/main/java/com/zzq/uiframe/media/BackupBridge.java:123`、`:132`、`:166`；共享仓库 `repository.cpp:183`。

进程在 Handoff 已提交、Submitted 未提交之间结束，会留下 `submission_state=1`。重开后 Schedulable 返回该任务，但 Android 的 wake / sync / recover 只做 settleIdle 和 schedule；worker 直接调用 Start，未补齐系统任务绑定。Start 正确要求原生任务达到状态 2，因此报条件失败，外层将该仓库加入故障集合。显式 recover 再次沿用同一路径，不能解决这个状态。

本轮通过发布 ABI 复现：Claim(executor=1) → Handoff → 关闭 / 重开 → Schedulable 返回 1 行 → Start 返回错误 10，`Affected-row condition failed`。对照实验仅补 Submitted，Start 即成功。此证据验证仓库状态门槛，并结合实际 Java 恢复路径定位缺口；不是 Android JobScheduler 真机运行证据。

现有 13 个进程中断窗口测试在状态 1 恢复时由 Python 主动调用 Submitted，所以能通过，但不能证明 Android 适配器已经完成恢复。

修改方向：提交与恢复共用系统任务关联流程，按仓库 / 任务 / 代次核对并持久化关联，再允许 Start。复用已有凭据引用及系统任务身份，不重新 Claim，不把故障任务自动当作新上传重试。补覆盖真实适配路径的中断测试。

对应计划：L1、C02、C15、C16；已登记意图但系统关联未完成的恢复窗口。

### R3 / P1：日志过期后的 catalog 完整核对没有处理缺失成员

位置：`Runtime/MediaBackup/Native~/src/repository.cpp:300`。

普通删除 / 移出日志经 Discover 消费时，会撤下待准备候选并暂停相关任务。但日志已截断时，完整扫描只登记当前存在的成员；ActivateScan 仅更新扫描和范围状态，没有将旧 discoveries / 未完成任务与本轮集合核对。丢失的移出事件不会自动重新产生。

本轮复现：范围中先有一个待准备来源和一个已接收 Queued 任务；模拟移出事件已经过期，按生产命令协议完成当前集合为空的完整核对。范围被重新启用，旧候选仍为 disposition=0，旧任务仍为 Queued。对照实验消费正常移出日志后，候选清零、任务变为 Paused。

如果图片只是移出所选相册、文件仍可读取，旧候选仍可能继续备份；如果文件已不可读取，则会表现为准备失败。不能把这两种后果混为一谈。

修改方向：完整核对增加持久化的缺失成员处理阶段，按稳定键分批对照扫描集合，并结合扫描期间的变更处理移出；完成后才启用范围。与正常增量消费共用撤下 / 暂停语义，保留回执及明确失败、取消事实，不能在激活事务中无界处理全库。

对应计划：L3、C06、C07、C17；完整核对须弥补日志连续性丢失。

### R4 / P2：文件归属清理存在平方级扫描

位置：`Runtime/MediaBackup/Native~/src/repository.cpp:493`；`schema/catalog.sql` 的 tasks 索引定义。

PruneScans 中清理已删除文件记录时，对每个 `file_records.state=3` 候选执行 `NOT EXISTS(SELECT 1 FROM tasks WHERE file_id=file_records.id)`，但没有 `tasks(file_id)` 索引。执行计划显示相关子查询为 `SCAN tasks`。大量文件记录仍由保留历史引用、最终没有任何可删除项时，LIMIT 200 不能限制检查量。

本轮真实仓库命令 68 的结果：

| 保留任务 / 对应文件记录 | 命令耗时 | 结果 |
| --- | --- | --- |
| 1,000 | 16.6 ms | 删除 0 项 |
| 10,000 | 1,167.1 ms | 删除 0 项 |
| 100,000 | 10,000.5 ms | Operation deadline exceeded |

仅在十万条临时数据库添加 `tasks(file_id)` 索引后，同一生产命令约 17.4 ms，删除仍为 0 项。没有提高超时或改变清理语义。

命令持有共享仓库串行执行资格，超时前其他仓库调用会等待。托管 TimeSlice 只在命令之间检查，不能中断这一条 SQL，因此默认 100 ms 时间片不能防止该问题。

修改方向：补充与文件引用关系匹配的索引，并检查相关外键 / 引用查询的执行计划；维护扫描仍按页、按稳定位置推进。不要通过提高截止时间解决。

对应计划：L6、Q05、Q15，十万项维护及前台查询延迟门槛。

### R5 / P2：逐张入队和完成都扫描全部来源历史

位置：`Runtime/MediaBackup/Native~/src/repository.cpp:154`、`:207`。

Accept 更新 discoveries 时由全部 disposition 候选检查对应任务；Finish 按 `(source_id,content_version)` 更新，而 discoveries 的主键以 scope_id 开头，现有索引也未覆盖该跨范围查找。执行计划分别为 `SCAN discoveries USING INDEX discoveries_pending` 与 `SCAN discoveries USING INDEX discoveries_task`。这里的 USING INDEX 仍是完整扫描，不是按来源版本定位。

本轮使用同一发布 SQLite 引擎、当前 schema 和原 SQL，历史 discoveries 均为已处理状态，仅一个待处理匹配项。每组预热 2 次、测量 10 次；表中为单条更新及事务交付的 p50，不包含图片读取、网络和整个 Accept / Finish 的其他 SQL。

| 历史来源版本数 | Accept 的来源更新 | Finish 的来源更新 |
| --- | --- | --- |
| 10,000 | 7.0 ms | 7.1 ms |
| 100,000 | 25.1 ms | 25.5 ms |
| 1,000,000 | 250.5 ms | 258.4 ms |

自动发现目前逐张接受，即使只新增少量照片，成本仍随全部历史增长。此前只测任务分页和汇总，无法发现这个写入瓶颈。

修改方向：以当前批次的有界来源版本集合驱动更新，增加匹配来源版本的索引，保持所有相关范围的处置同步。不能只给 disposition 加索引后继续遍历全部待准备集合。将 Accept / Finish 纳入规模与排队延迟基准。

对应计划：L2、L3，批量判重、热命令索引路径和十万 / 百万历史性能。

### R6 / P2：PhotoKit 的无相关变化被视为全量核对和权限变化

位置：`Runtime/Plugins/iOS/UIFrameGallery.mm:297`。

对于有效的 fetch result，`changeDetailsForFetchResult` 返回 nil 表示此次通知没有影响该结果。当前代码仍先增加 revision，再将 `!details` 视为需要完整核对；Limited 状态还会设置 accessChanged。其他相册或不属于当前图片结果的变化，因而可能触发本范围全量扫描、扫描边界失效和不必要的重新确认。

这是按接口语义和代码路径确认的静态问题，本轮没有 iOS 真机重现，不应将其写成已完成的设备验证。

修改方向：区分“有效 fetch 无相关变化”“有变化但无增量详情”和“观察结果失效”；只有相关变化推进观察修订。真实权限变化继续走明确的授权检查。不能把所有 nil 都忽略，以免同时掩盖失效的观察者。

对应计划：L3、C06、C07，以及只处理相关增量的性能要求。

## 进一步优化候选

下列项目已有额外开销的代码依据，但本轮没有完整测量收益，不与上述已复现缺陷等同：

- 目录索引生成带 SHA-256 的版本后，文件准备会在复制前、复制后各完整哈希一次，复制本身再哈希一次；首次索引还另读一次。可研究让流式复制产出的内容证明直接参与版本确认，减少重复磁盘读取，但必须保留“实际接受字节属于所声明版本”的保证。
- `FileImageVersion.Read` 和文件复制每次新建 128 KiB 缓冲；长时间扫描会产生反复大数组分配。适合在单次扫描 / 复制所有者内复用缓冲或使用明确归还的池，不增加全局图片内容缓存。
- Android 每 100 ms 的观察驱动会逐范围查询权限、卷版本和 generation；boundary 内又查询 version。可合并同一轮系统状态采样，结合通知合并窗口减少空闲调用，完整核对条件仍需保留。
- library 完整枚举即使内容未变，也逐条执行 assets 更新与 scope_assets 的已见标记。标记扫描归属有必要，但不变元数据更新可以按查询计划和 WAL 实测再收敛。
- catalog 的扫描历史清理先遍历 scan_items 排除活跃基线，历史保留门槛还使用 OFFSET。第二轮已实测前者，见 R12；大量受保留规则保护的历史及 OFFSET 路径仍待单独测量。当前 LIMIT 只限制返回 / 删除行数，不能证明扫描成本有界。

本轮没有新增证据证明缩略图租约泄漏或持久常驻内存必然无界增长。已有缓存配额、单解码槽位、取消和释放测试通过，但这不能代替 5,000 张照片滚动十轮、移动解码峰值及 24 小时混合负载验收。32 MiB 是空闲缓存预算，不是整个图片模块或进程的内存上限。

## 两份计划逐阶段结论

| 阶段 | 已实现及可核对部分 | 当前缺口 / 未完成验收 |
| --- | --- | --- |
| S0 | 同源锁版、唯一核心、ABI、独立程序集、四目标原生库与导入设置 | 真机运行、插件共存和完整域生命周期矩阵未完成 |
| S1 | 固定工作池、文件所有权、读写连接、事务、缓存、准入与缓冲预算 | 本轮未发现新的通用核心缺陷；业务 SQL 仍存在 R4 / R5，不满足完整性能契约 |
| S2 | 取消 / 提交确定点、关闭与故障收尾、宿主故障及进程中断测试 | 移动系统故障矩阵仍缺；平台交接存在 R2 |
| S3 | checkpoint、水位、专用快照槽位、临时发布与诊断 | 最低设备并行负载、WAL 增长、磁盘故障完整验收未完成 |
| S4 | 独立样例、来源清单、构建脚本、符号及原生 CI 配置 | CI 文件目前未纳入 Git；远端 CI、Windows 实际运行、项目遥测及 24 小时门槛未通过 |
| L0 | library / catalog 分工、共享仓库、私有根定位、统一引擎 | Unity 未运行时的系统回调及锁屏 / 存储设备验证不完整 |
| L1 | 准备记录、刷盘、接收、执行代次、文件 / 凭据归属 | Android Handoff 中断后的适配恢复缺失，R2 |
| L2 | 键分页、汇总、持久发现、独立回执与下载 | 逐张写入扫描全部历史，R5；最低手机内存 / 冷启动未验收 |
| L3 | 观察者、持久日志、完整核对、授权代次、基线与范围重置 | R1 / R3 / R6；部分授权与系统通知设备矩阵未完成 |
| L4 | 租约、同键合并、取消、LRU、配额、虚拟列表样例 | 产品列表 5,000 张照片十轮滚动、纹理与进程峰值未验收 |
| L5 | 持久批量选择 / 执行、逐项结果、单项与全局暂停 | 十万目标混合负载、迟到系统回调与主线程交付预算未验收 |
| L6 | 文件 / 历史 / 操作明细清理、空间统计、显式重试、服务器 TTL | R4；大基线清理成本、低存储空间和长期 WAL 竞争待测 |
| L7 | 接口文档、示例、构建检查、Android IL2CPP 构建与 iOS 设备目标链接历史证据 | Windows 本机运行、签名安装、Android / iOS 真机与 24 小时稳定性未完成 |

以上“已实现”不等于整阶段完成。首版不需要旧 JSON、旧 schema 或旧 ABI 的检测 / 迁移；本轮在相关自有运行时代码中未发现此类兼容分支，后续修复也不应引入。

## 第一轮验证与证据边界

- 当前八份已安装核心 / 业务库的二进制与其来源校验通过；实验所用 macOS 构建清单与当前源码匹配。SQLite build ID 为 `cc2af4a34676c4a76a07358b30d6c4351d4b22a8bdba0f2c9c2e0954fdbbc305`；BackupRepository build ID 为 `81c55ded4d62ce11904bd331f95df3777625d4a9e7e9bbbb9757f9c08e97274a`。
- 隔离 Unity EditMode：原有 37 项通过；新增的权限启动顺序用例失败，共 38 项中 37 项通过。失败是本轮审查发现，不是未执行。
- 共享 SQLite 原生 4 组、BackupRepository 原生 1 组重新运行通过；本机备份服务器 18 项重新运行通过。
- R2 / R3 使用当前发布 ABI 做状态探针，并以补齐绑定 / 正常移出日志作相邻正常路径对照。
- R4 测量真实仓库命令；R5 测量当前原 SQL 和真实引擎。均在 macOS 宿主运行，不作为手机性能数字。
- 本轮没有重跑移动发布构建、ASAN / UBSAN、Windows 程序或真机测试；前一轮已有结果仍只代表其原验证范围。

临时证据目录：`/tmp/uiframe-audit-20261001`。包含 `media-results.xml`、`media-tests.log`、`state_probes.py`、`state-results.jsonl`、`write_benchmark.py`、`write-results.json`、`cleanup_benchmark.py`、`cleanup-results.jsonl`、`controls.py` 和 `control-results.jsonl`。新 Unity 用例仅位于 `/tmp/uiframe-media-plan-android/Assets/UIFrame/Tests/Editor/MediaTests.cs`。临时数据库及工具脚本没有加入产品包。

建议修复顺序：先 R1–R3 的行为边界，再 R4–R5 的索引和查询，再 R6 的平台通知语义；补足对应回归后重新构建业务原生库并校验八份产物。之后按计划完成平台和长期验收，不用修改计划目标来消除缺口。

## 第二轮发现：R7–R12

以下为用户再次要求深度审查后的新增结果。沿用相同源码与原生库，重点验证异步准入、失败释放、来源重新关联及服务端中断窗口。

### R7 / P1：上传处理在首次 await 之后才取得运行所有权

位置：`Runtime/MediaBackup/ImageBackupService.cs:233`、`:236`、`:325`。

`ProcessTasksAsync` 检查 running 后先等待 GetSummaryAsync，再设置 running 和 processing。同一帧连续调用两次 ProcessAsync 会同时通过检查，并覆盖共享的 CancellationTokenSource；其中一轮收尾会释放、清空另一轮仍依赖的字段。

隔离 Unity 复现覆盖两种情况：

- 空队列：第二轮最终在第 325 行出现 NullReferenceException，未按并发入口契约拒绝。
- 有排队任务、创建上传会话的 HTTP 请求尚未完成：其中一轮 Claim 条件失败并收尾，此时另一轮请求仍在等待，但 IsRunning 已为 false。释放受控请求后，另一轮也出现空引用；本次网络故障还被 finally 的异常遮蔽。

因此问题不止是错误调用抛错类型不对，还破坏了暂停 / 关闭所依据的运行状态。测试实际确认了“在途请求 + IsRunning=false”；没有进一步把关闭过程中可能发生的后果写成已复现结论。

修改方向：首次异步等待前取得唯一执行所有权，让队列暂停的早返回、查询失败和上传收尾都由同一 try/finally 管理。本次操作持有并释放自己的取消资源；其他被拒绝调用不能改动既有操作状态。补空队列和有在途请求的重复启动回归。

对应计划：L1、L5、C09、C14；执行所有权、取消和收尾契约。

### R8 / P2：监听器创建失败后，自动循环永久停留在运行状态

位置：`Runtime/MediaBackup/AutomaticImageBackup.cs:237`、`:238`。

RunAsync 先设置 looping=true，再在 try 之外调用 library.Watch。Watch 创建失败时，负责将 looping 还原的 finally 没有进入。调用已经失败结束，后续 ConfigureAsync、ConfirmScopeAsync、ResetScopeAsync 和 RunAsync 仍认为循环没有结束。

隔离 Unity 复现：配置有效目录，删除目录后启动自动循环，FileSystemWatcher 抛出目录不存在异常；重建目录，再显式关闭自动备份配置，仍收到“Cancel and await the automatic loop before changing its policy”。实例没有仍可等待的活动循环，却无法再次配置。

修改方向：循环所有权从取得到释放统一覆盖 Watch 的创建和使用；创建失败也释放已经取得的状态与资源，保留原异常。无需额外增加“清除卡死标志”的恢复 API。

对应计划：L3、C14；失败后的资源与生命周期状态一致。

### R9 / P1：来源重新加入范围后，处置状态与任务关联不一致

位置：`Runtime/MediaBackup/Native~/src/repository.cpp:554`；受影响的范围暂停查询在 `:503`。

append_discovery 的 INSERT 会查找既有任务并设置 task_id，但冲突更新只恢复 disposition，不同步 task_id。旧记录从 RemovedFromScope（6）恢复为 Accepted（2）时，可能仍保留 NULL 的任务关联。

发布 ABI 复现：待准备来源移出范围 → 同版本照片从另一入口被接受为任务 → 再加入原范围。最终 discovery 的 disposition=2、task_id=NULL；SuspendScope 返回 count=0，任务仍为 Queued。正常对照中，先接受任务再首次加入范围，task_id 正确指向该任务，SuspendScope 返回 count=1，任务变为 Paused。

范围暂停、授权收缩及移出处理都依赖来源与任务的关联。此问题使相关任务漏过暂停，与 R3“完整核对没有发现缺失成员”是不同路径。

修改方向：在恢复来源处置状态的同一原子更新中同步任务关联，复用首次登记的来源关联规则，并保留既有失败 / 取消处置语义。补“移出 → 其他入口接受 → 再加入 → 暂停范围”的行为回归。

对应计划：L3、C07、C09；来源范围和任务意图的一致性。

### R10 / P2：旧分片请求能写入删除后重新创建的上传会话

位置：`Tools~/BackupServer/server.py:315`、`:361`、`:371`。

前台分片请求登记活动后，在锁外读取请求体；读完后只获取当时的会话并核对偏移，没有比较请求开始时的 epoch。后台整文件上传已有传输前后 epoch 核对，前台分片遗漏了同样的边界。

真实 HTTP 复现：创建会话，发送 offset=0 的慢 PUT 并让服务器等待剩余正文；DELETE 旧会话后按相同身份 POST 重建，新 offset=0；再发完旧 PUT。旧请求得到 200，新会话 offset 变为 6，取消前开始的请求被接收到新会话中。

这里没有复现哈希错误或错误照片入库。已确认的影响是旧请求跨越取消 / 重建边界，可能导致新上传的偏移冲突，破坏会话代次隔离。

修改方向：读取分片正文前固定会话身份和 epoch，写入前在上传锁内重验。前台分片与后台整文件共用同一会话代次规则，过期请求得到明确失败。

对应计划：C02、C13；服务端取消、提交与重建的竞争边界。

### R11 / P2：取消后台请求后进程中断，临时文件失去可恢复清理归属

位置：`Tools~/BackupServer/server.py:118`、`:233`、`:419`。

后台上传先持久登记 incoming，再写入临时文件。DELETE 删除 uploads 行，仍在接收正文的临时文件交给请求 finally 清理；如果随后进程被终止，finally 不会执行。重启后的 TTL 只从 uploads 枚举候选，因此无法发现已失去所属会话的 incoming 记录与文件。

真实服务器子进程复现：启动 1 MiB 后台上传，只发送 256 KiB；确认临时文件已写入，DELETE 得到 200 后终止进程。重开 Store 并清理，uploads=0、incoming=1，expired=0、freedBytes=0，仍留下一个 262,144 字节临时文件。该路径属于磁盘泄漏，不是托管堆或纹理泄漏。

修改方向：取消保留可恢复的清理归属，或让 incoming 自身拥有独立、持久的清理生命周期。文件清理完成前不能删除唯一的清理所有者；继续保持分页、活动请求互斥和清理失败显式重试。补取消后杀进程、重开清理的集成测试。

对应计划：L6、C02、C13；中断后的服务端临时空间回收。

### R12 / P2：无垃圾可删时，扫描历史清理仍遍历全部有效基线

位置：`Runtime/MediaBackup/Native~/src/repository.cpp:491`。

PruneScans 从 scan_items 驱动查询，逐项排除 active_baseline_id / pending_scan_id。LIMIT=200 只限制选中行；当全部记录属于必须保留的有效基线时，查询仍遍历全部记录，返回零项。

使用当前发布 ABI 和真实 schema，数据库只含有效 active_baseline 的 scan_items，没有 tasks 或 file_records，排除了 R4 的缺索引影响。每组预热 2 次、测量 5 次，表中为完整 PruneScans 命令耗时。

| 有效基线记录数 | p50 | 最大值 | 删除行数 |
| --- | --- | --- | --- |
| 10,000 | 0.71 ms | 0.73 ms | 0 |
| 100,000 | 5.78 ms | 6.26 ms | 0 |
| 1,000,000 | 56.44 ms | 56.60 ms | 0 |

执行计划明确包含 `SCAN i`，耗时随受保护记录增长。该测量没有达到单命令截止时间，也不是手机帧耗时；问题是无效维护工作与有效历史规模成正比，并在共享仓库的串行命令路径占用执行时间。给 R4 的 file_id 加索引不能解决此处。

修改方向：先选择有界的废弃 scan run，再按 run_id 和稳定键分批删除所属 scan_items；没有废弃 run 时不遍历有效基线。以“大有效基线 + 零 / 少量废弃记录”验证执行计划、完整维护耗时及并发查询等待。

对应计划：L6、S1，以及十万 / 百万历史的分页维护和短查询目标。

## 第二轮验证与后续顺序

- 本轮新增 3 个隔离 Unity 行为测试，3 个断言均失败，分别暴露并发准入、在途运行状态及监听创建失败的缺陷。测试项目的生产代码未改；失败不是编译失败。
- 来源关联使用当前发布 ABI 复现，并完成正常首次关联的对照；服务器使用真实 HTTP 慢请求和实际子进程终止验证。
- 基线清理测量在 macOS 宿主进行，只说明桌面规模成本，不替代最低手机验收。
- 未重跑第一轮已通过的 37 个 Unity 用例、原生组和服务器 18 项；同一代码下这些结果仍保留原有证据范围。本轮也未进行移动真机、Windows 运行或 24 小时压力验证。
- 没有发现新的、经实验确认的图片纹理 / 托管内存泄漏；R11 是服务器磁盘空间无法回收。第一轮列出的缓存、峰值和长时验收仍未完成。

第二轮临时证据目录：`/tmp/uiframe-audit2-20261001`。`media-results.xml` 覆盖 R7 空队列和 R8；`active-results.xml` 覆盖 R7 在途请求；`discovery_probe.py` / `discovery-results.jsonl` 与 `discovery_control.py` / `discovery-control-results.jsonl` 覆盖 R9；`server_probes.py` / `server-results.jsonl` 覆盖 R10 / R11；`baseline_benchmark.py` / `baseline-results.jsonl` 覆盖 R12。新增 Unity 测试仍只在隔离工程，不加入产品包。

结合两轮结果，先修 R1 / R2 / R3 / R7 / R9 的授权、交接和执行所有权，再修 R8 / R10 / R11 的失败退出与服务器生命周期，随后统一处理 R4 / R5 / R12 查询路径及 R6 平台通知语义。各项增加能区分修复前后的回归，原生变更重建并核对产物，然后继续两份计划尚未通过的平台与长期验收。沿用统一所有权、状态转换及索引设计，不增加旧 JSON / schema / ABI 兼容或针对某个调用者的特殊分支。

## 修复结果（2026-10-01）

| 问题 | 最终实现 | 已执行验证 |
| --- | --- | --- |
| R1 | 自动循环每轮先完成图库与授权范围核对，再驱动上传 | 新增真实自动循环用例；授权代次改变时旧排队任务为 Paused，回执为空 |
| R2 | Android 提交、重新调度和 worker 入场共用系统任务绑定；保持原代次和受保护凭据 | 真实 Java 调度 / 绑定代码 + JNI + 发布仓库在宿主运行；模拟 JobScheduler 验证重开、暂停及入场。另通过 Android Release 构建；不等于真机调度验证 |
| R3 | ActivateScan 按32条持久来源游标核对缺失成员，暂停与游标同事务提交，最后一页才启用范围；扫描期间增量同步成员集合 | 多页核对中关闭 / 重开继续、遗失移出日志、枚举期间新增 / 移出及独立任务保留均通过 |
| R4 | 增加任务文件引用索引；无引用元数据随物理清理完成原子删除，历史保留记录随任务删除，移除全历史周期扫描 | 弃置准备文件的元数据释放断言、原生资源收尾、进程中断、十万 / 百万历史维护基准 |
| R5 | 来源版本索引 + 当前接受批次驱动更新；确认按同一来源版本索引定位 | 跨范围接受 / 确认与显式取消处置回归；执行计划为 SEARCH discoveries_source；百万来源更新规模基准 |
| R6 | 有效 PhotoKit fetch 的 nil changeDetails 直接视为无相关变化；fetch 失效仍需核对 | Objective-C++ 随 iOS Release 设备目标编译 / 链接通过；真实系统通知语义仍待真机矩阵 |
| R7 | 首次 await 前取得唯一上传执行权；本轮持有并释放自己的取消源 | 空队列连续启动、在途 HTTP 请求的运行状态及正常后续调用回归通过 |
| R8 | 监听创建与循环使用共用完整收尾，清理保留主异常 | 删除监听目录导致启动失败后，重建目录并显式重新配置成功 |
| R9 | 来源从移出处置恢复时，任务关联与状态在同一 upsert 更新 | 移出 → 其他入口接受 → 重新加入 → 暂停范围，及首次关联正常对照均通过 |
| R10 | 前台分片与后台整文件固定请求入场 epoch，并在写入锁内重验 | 慢 PUT → 删除 → 重建 → 旧请求409；新会话偏移仍为0，正常新请求可完成 |
| R11 | incoming 独立保存创建时间、清理意图及错误；取消原子标记临时文件清理，文件删除刷盘后才移除归属 | 真正终止服务器进程后重开回收；注入删除失败后不自动重试，显式重试成功；共用维护数量预算 |
| R12 | 先选废弃 scan run，再按主键删除有界明细页；没有废弃 run 时不遍历有效基线 | 有效基线1万 / 10万 / 100万行的零删除维护基准，以及维护页预算断言 |

R3 增加的是完成核对阶段及明确的成员 / 排除标记，不另建第二套图库或任务状态机。R4 由资源释放点承担元数据回收，没有增加全局维护游标、启动猜测清理或旧库修复逻辑。当前 schema 直接用于首版，没有迁移、双写或旧格式分支。

最终仓库四平台 build ID：`594a77f179cb7e497509ddb1fb5458b202cf65c322933fd58de25bd907372fd3`。公共 SQLite 核心未改；八份安装产物的源码与二进制哈希均核对通过。

本次通过：Unity 41/41、服务器22/22、仓库原生契约及 ASAN / UBSAN、13个进程中断窗口、两库 schema、新增范围核对 / 跨范围关联测试、Android Java / JNI 绑定测试、Android IL2CPP / High裁剪 / Release混淆 APK，以及 iOS IL2CPP 导出 / Xcode Release 无签名设备目标构建。服务器最终复核额外覆盖“候选选出后才记录失败”，执行前重查，避免自动重试失败清理。iOS最终框架保留全部19个存储ABI入口，未导出原始sqlite3符号。最初以无图形设备模式运行时，既有GPU颜色测试失败；启用图形设备后全套通过，未改动该图片处理逻辑。

最终规模复测使用同一核心、当前 schema 和最终仓库，预热2次 / 测量10次。首次计时中十万保留文件的空维护约0.08ms、百万有效基线约0.07ms；正式新增脚本从零造数后的相应结果为0.16ms、0.11ms，百万保留文件也约0.15ms。百万来源历史的 Accept / Finish 来源更新均约4.02ms（含单条 SQL 的事务交付，不含整个命令、图片处理或网络）。执行计划和规模曲线确认维护不再遍历受保留历史，来源更新不再全扫 discoveries。这些是桌面实验，不作为手机帧耗时或上线门槛已通过的证明。

正式新增复现入口：`Runtime/MediaBackup/Native~/tests/scope_reconciliation.py`、`maintenance_benchmark.py`、`Tests/Native~/android_backup_binding.py`，以及原有 Unity / 服务器测试文件内新增用例。完整参数见 [GalleryValidation.md](GalleryValidation.md)。本次临时日志、XML、规模数据及产物核对在 `/tmp/uiframe-followup-fix-20261001`；发布库已安装到包内各平台 Plugins 目录，未提交 Git。
