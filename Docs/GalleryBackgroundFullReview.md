# 照片备份 v2 全量源码复查

日期：2026-10-02。状态：**F1–F6 已修复并完成本机回归、四平台原生重建及移动发布构建**。iOS 保留系统后台传输，已更正无效的客户端禁跳转承诺；真机行为和生产服务仍待验收。修复映射与最终证据见第6节。

基线审查逐文件阅读照片备份主链路的 27 个生产文件、共 5,479 行，并沿提交、系统交接、传输、确认、暂停、取消、恢复和清理检查跨文件调用顺序。另核对相关测试、构建配置、示例和实施记录。修复前再次核对全部27个文件均未偏离已读基线；修复时复查改动及调用者，补充发现 F6。以下 F1–F5 保留修复前发现和复现，不代表当前仍存在；共处理3项P1、2项P2、1项P3。

## 1. 确认发现

### F1 / P1：Android 恢复判断与执行器启动不互斥

位置：`BackupBridge.java:168–170`、`178–185`、`278–303`。

`call()` 在 LOCK 内读取 active，随后释放锁，根据局部变量决定是否执行 recoverIdle。`start()` 可以在这两步之间登记并启动新 Run。恢复分支不再检查真实执行器，也没有获得与启动互斥的恢复所有权，会把新 Run 的控制请求或上传当成前次进程遗留工作。

合法交错为：wake/sync 读取 active=null → JobService 启动 Run → Run 记录 ControlSubmitted/Start → 原调用继续 recoverIdle。恢复把任务转 NeedsAttention，并调用 ControlRelease；实际 HTTP 所有者仍未结束。之后的清理和迟到回调都将依据错误的释放事实工作。并发的两个 idle 恢复调用也没有统一的生命周期边界。

验证：使用当前生产 Java、JNI 和发布仓库，按上述交错重放恢复分支；得到 `actual_active=true, work_completed=false, task_state=6, control_released=true`。宿主使用未完成的 Future 表示仍在执行的工作，并通过反射进入生产恢复方法；这验证破坏状态的分支，不冒充手机系统自然触发或真实 HTTP 复现。

建议：按仓库统一执行器启动、恢复和停止的生命周期所有权。只有持有独占恢复资格且确认旧 Run 已退出，才能扫描并释放遗留任务；活动 Run 的 sync/wake 只向当前所有者传递意图。采用固定的每仓库串行控制入口或等价的互斥生命周期，不用额外延时、重复查询或自动重试补竞态，也不把网络 IO 放入全局锁。

验收：在读取 idle 后、登记 Run 前后设置确定性同步点；覆盖 Job 启动与 wake/sync/recover 并发，以及两个恢复调用并发。未结束的工作不得出现 released=1；结束后才能清理，正常新增提交必须仍可推进。

### F2 / P1：iOS 后台会话的“禁止重定向”没有生效

位置：`UIFrameBackup.mm:119`、`421`、`432–459`；协议的禁止自动重定向要求见 `GalleryBackgroundProtocolPlan.md:165` 和 `GalleryBackupProtocol.openapi.yaml:15`。

实际使用的是 background NSURLSession，却依赖 willPerformHTTPRedirection 回调传 nil 来拒绝跳转。安装的 Apple SDK `Foundation.framework/Headers/NSURLSession.h:1732` 明确说明：

> For tasks in background sessions, redirections will always be followed and this method will not be called.

因此，该回调不能兑现当前协议承诺。完成处理又只检查最终状态码和正文，没有识别最终目标改变。不能把“已编译这个回调”记为“已阻止重定向”。这不等于已经证明所有 Authorization 头都会跨域泄露；本轮没有做这样的运行测试，但上传字节、签名请求和目标约束不能建立在这个无效拦截上。

建议：保留系统后台传输时，明确其平台限制，使用受信任且直接返回最终结果的业务/存储终点，规定服务及网关不返回重定向，并验证实际目标和响应。最终 URL 检查只能发现违规，不能撤回已经转发的字节。如果产品必须由客户端保证“任何跳转都不发送文件”，需要改用允许拦截的传输方式并接受后台能力变化；不能继续用同一个代理回调包装成已解决。

验收：在 iOS 设备上使用受控的 302/307/308、同域与跨域终点，记录第二个终点是否收到请求、正文和各类头；同时修正 OpenAPI、实施记录及 B14 的能力声明。接入正式存储前须确定这项协议取舍。

### F3 / P2：失败控制文件会使后面的历史清理长期得不到处理

位置：`repository.cpp:402–413`、`BackupMaintenance.cs:156–161`。

HistoryPage 只检查任务、照片文件和执行资源，不排除关联控制文件仍未清理的任务。PruneTask 则正确保留这些任务。维护每次从 sequence=0 开始，并把每次跳过计入 MaximumItems；因此，排在前面的不可删除记录可以反复耗尽整个清理额度，后面的可删除历史和操作明细永远轮不到。

验证：第一条已完成任务的控制文件清理失败，第二条已完成任务完全可删除。设置 MaximumItems=1，按真实仓库执行三轮维护调用，三轮都选择第一条且删除数为 0；直接按游标选择第二条，删除成功。默认额度下，同样的问题发生于前面的阻塞记录达到本轮额度时。无需文件清理再次失败，持久的 cleanup_failed 状态就足以触发。

影响：单项清理故障间接阻塞其他历史和元数据回收，长期导致数据库增长、重复查询和无效事务；不意味着它直接阻塞所有正在上传的照片。

建议：让历史候选查询排除稳定存在的控制清理依赖，与删除前置条件一致；PruneTask 仍在事务内复核竞态。维持有界分页，避免增加无限翻页或把失败文件重新纳入自动重试。

验收：前面的阻塞记录数分别为 1、MaximumItems 和大于 MaximumItems，后面的可删除历史仍应被后续维护选中；失败项保留可查询，修复其文件并显式清理后能正常进入历史回收。

### F4 / P2：未来 Query 可以绕过确认截止检查

位置：`protocol.cpp:218–222`、`287–292`、`375–385`。

Pending/Verifying 的 nextCheckAt 只要求晚于 serverTime，应用时原样持久化；创建未来 Query 时没有检查 confirm_deadline_utc。一旦绑定 control_id，ProtocolActions 又完全跳过该项。系统已受理但尚未执行的 Query 因而可以把照片、凭据和控制请求保留到本地确认期限之外。

验证：真实仓库接受 nextCheckAt=7 天后的合法 Pending 响应，创建并绑定未来 Query；将客户端检查时间推进到第 2 天后执行 ProtocolActions，任务仍为 state=2 / phase=2，control_id 非空。24 小时截止已过去，却没有进入 NeedsAttention。这不是单纯“操作系统没有及时唤醒”，因为本次已经主动执行了截止检查。

建议：在共享仓库统一定义“服务器允许的最早查询时点”和“本地继续自动确认的期限”。不为超出剩余预算的时间创建普通 Query，也不把 nextCheckAt 截短后提前轮询。对于已绑定的逾期系统任务，先通知实际执行器终止并确认释放，再结束自动确认；保留未知服务器结果以便显式核对。所有平台使用同一预算规则。

验收：nextCheckAt 在期限前、等于期限、超过期限；排队与已执行两种控制状态；暂停/恢复后过期；系统延期后重新唤醒。既不能突破自动确认预算，也不能伪造实际释放或取消回执。

### F5 / P3：只被测试调用的整批 Abandon 与逐项接受语义冲突

位置：`repository.cpp:161–171`、`ufbackup.h:21`、`BackupRepository.cs` 命令枚举及使用这些命令的测试。

当前公开 SubmitAsync 使用 AcceptItem/FailItem，仓库仍保留 Accept/Abandon。Abandon 仅删除未接受任务，却把同批所有 state=0/1 的文件都置为待清理，包含已通过 AcceptItem 接受的照片。这与 RepositoryContract 的“只拥有未接受准备项”声明不符。

验证：两项 Prepare → 第一项 Seal/AcceptItem → 整批 Abandon → CleanupRun，结果为 `accepted_task_state=0, accepted_payload_exists=false`：任务仍显示 Queued，但文件已被删除。

当前 C#、Android/iOS 生产调用链没有使用这两个整批命令；命中的是原生 ABI 和测试路径，所以列为 P3，不把它描述为正常 SubmitAsync 正在删照片。

建议：删除没有生产调用者的整批 Accept/Abandon 入口、命令映射和相应旧测试假设，让测试统一使用逐项接受/失败及真实恢复路径。无需保留兼容分支，也无需为两套接受模型增加例外判断。

### F6 / P1：无待唤醒时点返回 NULL，iOS 按数字读取会中断调度

修复过程中沿 `ProtocolWake → UFBCommand → schedule` 核对数据类型时发现：`min(ready)` 在空集合上返回 SQL NULL，Objective-C 桥将其保存为 NSNull，而调度直接调用 longLongValue。空仓库、Plan已绑定、只有上传工作或全部结束时均可能没有下一检查时点；NSNull不支持该方法，会被仓库失败边界记录并停止后续调度。C# / Java的数字读取把NULL映射为0，既有宿主测试掩盖了这个平台差异。

已在共享查询使用 `coalesce(min(ready),0)`，统一声明“没有待调度时点”为整数0；派生的Query截止也返回整数。没有新增iOS特殊兜底。真实ABI回归覆盖空仓库、待Plan、已绑定Plan及已结束队列，旧库返回None失败，新库通过；无需依赖实际手机网络即可验证返回类型。

## 2. 阅读覆盖与边界

| 层 | 本轮逐文件阅读的生产代码 |
| --- | --- |
| C# 门面与契约 | ImageBackupService、BackupContracts、BackupQueries、BackupRepository、NativeBackup、AssemblyInfo |
| C# 执行和维护 | BackupSubmission、BackupExecutionControl、BackupProtocolExecutor、BackupOperations、BackupMaintenance、AutomaticImageBackup |
| 共享仓库 | repository.cpp、protocol.cpp、repository_support.hpp、android.cpp、catalog.sql |
| Android | BackupBridge、BackupRepository、BackupJobService、BackupForegroundService、BackupPauseReceiver |
| iOS | UIFrameBackup.mm |
| 本机参考服务 | server.py、store.py、protocol.py、schema.sql |

上述为 27 个生产文件。另核对 ufbackup.h、RepositoryContract、CMake/构建/安装脚本、Android Manifest/混淆配置、GalleryBuildProcessor、GalleryTestWindow、GalleryDemo，以及协议测试、仓库测试、JNI 宿主夹具和相关既有验证记录。按现有协议计划和执行记录追踪入口、错误出口、所有权和预算，不只检查本轮 diff。

这是照片备份主链路的完整源码复查。未将整个 UIFrame UI 框架、图库/缩略图的所有实现、SQLite 引擎及第三方 SQLite amalgamation 重新逐行审计；也未把所有历史测试脚本和基准程序都算成逐行审查完成。此次范围之外的模块不能据此宣称全量验收。

## 3. 性能、内存与可维护性结论

现有按文件流式传输、有界照片窗口、独立控制文件预算、批量 Plan/Query、两个照片上传槽和共享仓库状态机仍是合理方向。这次没有证据支持为提高性能而重写架构、增加全局缓存或继续提高并发。

优先解决 F3/F4 的长期保留和无效工作，以及 F1 的错误生命周期。照片与控制请求按已读路径保持有界；这只能证明代码显式预算存在，不能代替设备 RSS/GC 峰值、系统缓存和 24 小时负载测量。没有同条件旧版吞吐基线，不给提速比例。

F5 的重复接受模型已删除；NativeBackupRequest/Status 同步删除当前适配器不读写的字段。保持现有共享状态机和平台所有权规则，没有增加通用调度框架、重试层或旧格式兼容。

Android 的真实 HTTP 慢写入、系统停止与恢复，以及 iOS 重定向、锁屏与后台回调仍需设备验证。现有宿主夹具主要验证绑定/调度，不能代替平台 HTTP 栈。

## 4. 修复前审查证据

业务库构建 ID：`4d8791d4befab5ad657861ac317bdc96da1dd83b81704b046695a17274d78393`。SQLite 核心沿用当前发布库。四个平台的业务源文件清单与二进制哈希再次核对一致。

| 验证 | 本轮结果 |
| --- | --- |
| 既有共享协议测试 | 22/22 通过，4.707 秒 |
| 既有参考服务测试 | 33/33 通过，2.585 秒 |
| F1 Android Java/JNI 分支交错 | 复现活动工作未完成却记录 released=1 |
| F2 Apple SDK 能力声明 | 直接核对后台会话不调用重定向代理的文档注释 |
| F3 原生仓库调用 | 三轮重复跳过前项，后项直接删除成功 |
| F4 原生仓库调用 | 第 2 天主动检查仍保留第 7 天的未来 Query |
| F5 原生仓库调用 | Queued 任务仍在，已接受文件被删除 |

本轮证据目录：`/var/folders/gk/fvyb98j97rv0xvb18qrzxppr0000gn/T/uiframe-full-review-20261002-ctnwmpt3`。其中 `reviewed-production-files.json` 保存 27 个生产文件的路径、行数和源码哈希，`repository-reproductions.py/json`、`android-recovery-repro.py` 和 `abandon-mixed-acceptance.json` 保存专项复现。

本节对应修复前审查，当时没有重启主 Unity、重新构建、提交 Git 或部署服务器。修复后的构建和运行结果另列于第6节。

## 5. 与实施清单的关系及建议顺序

前份审查中的“本轮五类问题已处理”只适用于当时列出的 R1–R5，不能解读成现在没有缺陷。特别是 B14 的“三端禁重定向构建”不能作为 iOS 已具备该能力的结论，应按 F2 撤回这一判断。

F1 涉及 P2.4/P4.3 的实际释放与恢复，F2 涉及 P5 平台能力和 B14，F3 涉及历史维护与 B16/B19，F4 涉及 P2.2/P5.2 及 B23/B24，相关条目修复后需重新验收。原有真机、24 小时、Windows 运行、生产服务和旧版同条件基线等未验收项继续保留。

建议先修 F1 并确定 F2 的平台/协议边界，再修 F3/F4，最后删除 F5 的闲置入口。每项补能区分修复前后的行为验证，完成后统一复查跨平台调用顺序和文档。问题根因分别是生命周期互斥、平台能力、候选筛选和期限模型，应在所属层解决，不增加调用方专用分支。

## 6. 修复结果与最终验证

| 项目 | 最终实现与验证 |
| --- | --- |
| F1 | 每仓库生命周期互斥覆盖idle恢复与Run启动初始化；启动可立即登记，但实际执行必须等待恢复结束。锁不覆盖网络IO，不同仓库独立，引用计数包含等待者。生产Java/JNI宿主覆盖恢复等待期间出现活动Run、拒绝活动Run显式恢复、两个idle恢复并发、未结束Future不得释放，以及生命周期登记释放。 |
| F2 | 删除无效的重定向代理，校验最终URL与系统提供的redirectCount。违规不能发布成功；保留后台传输，协议明确可信最终终点及服务/网关不得重定向。Foundation宿主运行生产目标校验函数，iOS Release编译链接通过；未声称在跳转前阻止字节，也未把宿主当真机HTTP。 |
| F3 | HistoryPage与批量删除共用控制依赖条件，PruneTask仍事务复核。原生回归覆盖失败项隔离、后项删除、显式修复后回收；C#公开维护接口以MaximumItems=1验证两个阻塞项不挡独立后项。 |
| F4 | 仓库校验nextCheckAt与24小时预算，不提前轮询；第256次Pending停止。Controls派生Query最早成员截止，Wake包含已绑定请求截止；三端执行器处理超时，等实际所有者结束后才Release。批量共享请求以最早成员截止作为固定失败边界，未确定成员保留未知结果。原生覆盖边界、暂停恢复、迟到权威确认；C#受控异步请求验证取消后未完成仍持有控制文件，实际结束再释放，后续独立任务可完成；Java宿主覆盖未执行和执行中两种状态。 |
| F5 | 删除整批Accept/Abandon命令及映射，测试改为逐项AcceptItem/FailItem与真实重开恢复。准备失败保留可查询任务；部分接受后中断只回收未接受文件。同步删除无使用者的原生桥DTO字段，无兼容分支。 |
| F6 | 共享Wake返回整数0表达无调度时点；空队列、已绑定控制、结束队列均通过真实ABI验证。 |

业务仓库四平台同源构建ID：`cfb8809e8ceca2ffdd03356367f4b4053bdbfb82435c6729b6a5afcd1aeed3b8`。SQLite核心ABI3及构建ID未变。唯一v2、业务ABI/schema2保持不变，没有新协议字段或数据库迁移。

最终证据目录：`/var/folders/gk/fvyb98j97rv0xvb18qrzxppr0000gn/T/uiframe-review-fix-20261002-iqd48h49`。保留修复前27文件副本、旧业务库、最终来源摘要及日志。`before-regressions-final.log` 的6个关键回归在旧库全部失败（包含子场景共7个断言失败），最终库全部通过。F1/F5的旧行为证据见第4节。

| 最终验证 | 结果 |
| --- | --- |
| 原生协议 | 30/30，14.179s |
| 原生契约 | Release及ASan / UBSan通过 |
| Unity Media | 71项：70通过、0失败、1项目标不适用跳过；129.960s |
| Android权限专项 | 1/1，覆盖上述跳过项 |
| 进程中断 / 显式核对 / 范围关联 | 48/48、2/2及范围分页关联通过 |
| Android Java/JNI | 生产调度、并发恢复、期限所有权、失败清理宿主通过；未执行完整Java HTTP |
| iOS Foundation策略 | 实际目标与截止辅助函数通过；未执行系统后台会话 |
| 参考服务 / OpenAPI | 33/33；OpenAPI3.1及8组正反例通过 |
| Android发布 | IL2CPP High、Release混淆APK通过 |
| iOS发布 | IL2CPP High导出及Xcode无签名Release链接通过 |
| Windows | x64交叉构建及来源校验通过，未实际运行 |

重新运行1万 / 10万 / 100万历史且同样1个活动尾部尝试的稀疏调度基准：Attempts p50分别0.201 / 0.226 / 0.106ms，ProtocolWake p50分别0.090 / 0.087 / 0.060ms。查询使用活动索引，没有出现随总历史线性增长；构建及测试存在并行时段，这些是宿主查询成本，不是手机吞吐或与旧版同条件提速结论。

原生库通过新inode替换安装，没有覆盖主Editor已加载的映射，也没有重启用户主Unity。已加载旧业务库的Editor需正常重开后才使用新库。本轮未提交Git或部署服务。仍需真机后台 / 锁屏 / 退出 / 重定向矩阵、24小时负载、Windows运行和生产服务验收；测试通过不能证明没有其他缺陷。
