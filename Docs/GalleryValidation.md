# 图片与长期备份验证记录

更新：2026-10-06。当前照片备份为唯一v2协议，业务仓库ABI 4 / catalog schema 5、图库 schema 3，SQLite核心ABI3。最新故障修复与验证见 [故障修复记录](GalleryPreparationRedesign.md#故障修复与开发数据检查)；部署前旧代码清理见 [清理记录](GalleryPreparationRedesign.md#部署前旧代码清理)；登记／准备重构及历史验证见 [准备协调器重构记录](GalleryPreparationRedesign.md)。完整改动、命令、构建ID、B01–B24矩阵与设备执行步骤见 [本轮执行记录](GalleryBackgroundProtocolExecution.md)，清单见 [后台协议计划](GalleryBackgroundProtocolPlan.md)，最新F1–F6修复及边界见 [全量复查报告](GalleryBackgroundFullReview.md#6-修复结果与最终验证)。

## 当前v2验证

| 范围 | 本轮结果 |
| --- | --- |
| Unity图库与备份 | 71项：70通过、0失败、1项因构建目标跳过；该项在Android目标另行1/1通过 |
| 32项失败隔离强化 | 一张慢传、一张失败，其余30张确认及慢项取消，专项1/1通过 |
| 原生协议与契约 | 30项协议测试通过；6个关键回归在旧库全部失败；Release和ASan / UBSan契约测试通过 |
| 异常退出 | 48个真实kill窗口、2个核对窗口及范围关联验证通过 |
| 参考服务 | 33项HTTP / 进程 / 清理测试通过；OpenAPI3.1及8组正反例通过，已更正iOS重定向能力说明 |
| Android | 生产Java / JNI调度、追加受理、并发恢复、期限所有权及清理宿主绑定、IL2CPP High / Release混淆APK通过；完整Java HTTP链路未运行 |
| iOS | 生产目标与截止策略函数的Foundation宿主测试、Objective-C++、IL2CPP High导出、无签名Release编译链接通过；系统后台会话未实测 |
| 四平台来源 | macOS / Android / iOS / Windows库同源，哈希及ABI检查通过；Windows只交叉构建 |

业务库build ID为 `cfb8809e8ceca2ffdd03356367f4b4053bdbfb82435c6729b6a5afcd1aeed3b8`；最终修复证据目录为 `/var/folders/gk/fvyb98j97rv0xvb18qrzxppr0000gn/T/uiframe-review-fix-20261002-iqd48h49`，Unity整合结果为unity-final.xml、Android专项为unity-permission.xml。前次复审及首次交付证据分别保留在 `/var/folders/gk/fvyb98j97rv0xvb18qrzxppr0000gn/T/uiframe-v2-audit-20261002-1_i2lw75` 和 `/tmp/uiframe-background-v2-20261001.5cBjcQ`。全包.meta GUID合法且无重复，新增文档和Unity测试已补.meta。

前次业务库 `a21bb7af...` 的1000个64KiB合成对象满批场景产生32次Plan；100%已核验场景为0次PUT、0照片上传字节，本次未重测这项吞吐。最终库已重测稀疏调度：百万历史且1个活动任务的Attempts p50约0.106ms、ProtocolWake p50约0.060ms，使用活动索引。这些合成对象 / 宿主查询数据不代表手机吞吐、真实网络或旧版性能提升，详见执行记录的测量条件。

**仍未验收：** 手机系统后台 / 锁屏 / 退出矩阵、24小时负载、接近512MiB照片的设备峰值、Windows实际执行及生产服务。没有同条件旧版性能基线；不宣称商业发布全部完成。用户主Unity未重启，已加载旧原生库时需正常重开才能使用新版原生库。

## 2026-10-01及之前的历史证据

以下保留SQLite改造与多轮复审当时的记录；旧测试数量、旧业务ABI和哈希均不代表当前v2结果，旧JSON与旧协议的验证不能替代本轮验收。

### 当时结论

共享 BackupRepository、C# 图库索引、增量变化、共享缩略图、持久批量操作、分批清理及本机服务器 TTL 已接入。任务 / 策略 / 基线 / 回执没有旧 JSON 读取、格式探测、迁移、双写或全量 GetTasks 兼容接口。HTTP、平台桥接和构建清单仍使用 JSON。

复审F1–F7、继续审查R1–R12、六项失败边界 / 生命周期简化及后续流程根因问题已完成代码修复，桌面 / 发布构建验证已有证据，**两份实施计划的商业发布验收仍未全部完成**。没有真机、Windows运行、24小时稳定性及最低设备帧预算的证据；不能从构建或桌面数字推定。

## 环境与版本

- Unity 6000.3.19f1、macOS Apple Silicon；独立隔离工程验证，未重启用户主工程。
- SQLite 3.53.4 / ABI 3；核心 build ID：`cc2af4a34676c4a76a07358b30d6c4351d4b22a8bdba0f2c9c2e0954fdbbc305`。
- 共享仓库 ABI 1；四平台同源 build ID：`db07b9720d698d9cc3d7fa60a83cb317b3ef28fe2eef9577e1d68d30b146fe0d`。
- Android ARM64，宿主最低API25，NDK27.2.12479018、CMake3.22.1、JDK17。NDK把native API25规范化为24，不改变宿主最低版本。
- iOS arm64设备目标、最低15.0、Xcode / SDK27.0；macOS库为arm64+x86_64、最低11.0。
- Windows x64用LLVM-MinGW 20260922 UCRT交叉构建；没有Windows实际运行结果。

## 已执行验证

| 检查 | 当前结果 |
| --- | --- |
| 共享仓库原生契约 | Release通过；同源ASAN / UBSAN通过。覆盖准备原子性、预算、认领与开始竞态、代次、确认胜过取消、原生Handoff/系统绑定前拒绝Start、凭据由实际持有者释放、Seal缺失/长度错误与正常刷盘、实际释放前禁止清理、独立回执、基线连续性、批量操作幂等与上界、暂停 / 范围重置、保留数量及过期明细 |
| 实际进程中断 | 13/13通过：准备完成、接受、认领、开始、确认、释放、文件已删除7个窗口，另加Android/iOS执行者各自认领/Handoff/系统绑定后中断的6个窗口。强制结束子进程后经生产仓库ABI重开，检查准备ID、回执、未知结果和清理归属；没有用测试SQLite替代 |
| 两库schema | 通过同一核心建表、标识 / 约束及删除历史不丢操作结果检查；trigger按完整SQL语句解析 |
| Unity图片回归 | 最终60/60通过（约73.3秒），Android活动目标，启用图形设备；50项已有回归与10项新增流程回归全部通过。包括原生注册/取消时序、Limited确认与重开、部分刷新通知、自动操作准入、单项/排队取消、凭据失败与跨尝试未知结果、孤立文件清理故障；详细范围见下表 |
| HTTP与错误身份 | 真实本机上传、幂等重提、清历史后下载校验；能力缓存与无效能力终止批次；凭据 / 策略回调保留异常身份；响应 / 下载大小边界、超时、取消、已有目标不覆盖 |
| 图库与缓存 | 分页、跨范围内容版本、首次基线、关闭排空；100等待者只处理一次、独立取消、失效不破坏已交付租约、来源释放、旧完成隔离、交付失败所有等待者结束与准入归还 |
| 批量操作与维护 | 持久ID、重复参数、全局 / 单项暂停关系；35个连续提交及过期清理；修复驱动退出时新提交错过唤醒，修复已过期头阻塞后续明细 |
| 本机服务器 | 前轮22/22通过，本轮服务器未改、未重复执行该单元套件；本轮Unity仍运行真实本机上传 / 下载集成测试。原有覆盖：慢分片跨删除/重建会话隔离、取消后真实杀进程的临时文件回收、清理失败显式重试及选出候选后的失败状态重查；服务仅面向单进程本机使用 |
| 来源范围与关联 | 通过发布ABI验证缺失成员的32项分页核对、关闭/重开继续、扫描期间新增/移出、独立任务保留、重新加入后的任务关联及跨范围接受/确认；完整基线不会吞掉枚举期间的新版本 |
| Android恢复绑定 | 实际BackupBridge调度/绑定 + BackupRepository Java/JNI + 发布仓库运行通过；宿主仅替代Context/JobScheduler，验证中断Handoff重开、暂停不调度、同代次/凭据绑定及Start；新增失败文件隔离后继续清理后续项、不自动重试及显式目标重试。仍需真机系统调度验证 |
| 图片 / SQLite Runtime及UGUI样例 | 本轮隔离Unity工程同时编译图片 / SQLite运行时、相关Editor和测试程序集；新增虚拟图库示例在独立GalleryDemo.Samples消费程序集编译通过，未使用运行时internal接口 |
| Android发布 | IL2CPP、High裁剪、Release混淆APK成功；包内一份SQLite核心和一份仓库；JNI内部错误类型保留；权限配置可选且幂等测试通过 |
| iOS发布 | IL2CPP、High裁剪导出及Xcode Release无签名设备构建成功；19个ABI导出（15核心+4仓库）、当前核心build ID及原始sqlite3符号隔离通过 |
| 来源和导入 | 四目标产物校验值与锁定源码一致；Unity可见资源meta齐全；主Editor仍可能映射旧动态库，重新打开主工程后才使用新原生实现 |

Android最终APK SHA-256：`d889a0db87186bc6c632c0b67cc685686918f910fa53f727fbdc3158b1396a1a`。iOS最终UnityFramework SHA-256：`555d6c006114da0362d2458b251ab8573ab20486af16a459e0bb60983b8b2df3`。

iOS验证期间发现并修复导出工程关闭Objective-C异常的问题，构建处理器现在同时配置异常及ARC异常收尾。Android构建处理器保留JNI使用的内部Failure类型。平台适配不再自动重新上传已消失执行者的未知结果；该任务需要按旧幂等身份核对。iOS回调使用attempt_generation，容量释放后按等待仓库顺序继续调度。

原生内存工具仅覆盖共享仓库和核心，未覆盖手机PhotoKit / Bitmap / URLSession内部，也不代表线程竞争或长期泄漏矩阵已完成。旧Foundation持久化替身已移除；当前进程中断测试针对真实共享仓库，仍需系统适配的无Unity运行测试。

## 前轮六项修复的验证范围

| 修复 | 实现与验证 |
| --- | --- |
| 单文件清理隔离 | 共享仓库只在故障持久保存后返回1001；C#、Android、iOS依同一错误分类处理。原生Release / ASAN / UBSAN、Unity及Android真实Java/JNI路径通过。显式维护提供部分统计，单项重试不删除其他候选；iOS运行行为仍待设备验证 |
| 局部互斥与关闭 | 下载中恢复队列、刷新能力和清理通过；能力刷新与上传互斥；准备中暂停通过。关闭拒绝新调用并排空已受理查询；在途登记使用值类型，无每次登记的独立对象分配 |
| 索引与通知分阶段 | 直接和自动通知异常保留同一异常；恢复通知不触发全量重扫；等待刷新锁取消不破坏已完成索引 |
| 观察者归属 | 顺序使用24个范围不耗尽16个同时观察名额；关闭一个订阅不影响同范围另一个；最后订阅等待在途刷新，释放后位置明确要求重新核对 |
| 复制与哈希 | 目录复制只读取源文件一次并同时计算哈希，复制前后校验元数据；同size/mtime但内容不同的索引引用被拒绝；原生Seal仍负责最终刷盘。减少了完整读取次数，但未提供最低设备耗时 / 峰值内存基准 |
| 图库字段收敛 | 删除未使用占位字段，同步单一SQL源和生成文件；两库schema检查与Unity索引 / 自动备份回归通过；无旧schema兼容或迁移 |

前轮六项修复证据位于执行环境 `/tmp/uiframe-design-fix-20261001/`：Unity XML结果、四平台仓库构建、ASAN / UBSAN、13个真实进程中断窗口、范围核对、两库schema、Android Java/JNI、Android发布APK、iOS导出及Xcode Release无签名构建日志，以及 `artifacts.json`。四平台仓库产物与源码清单一致；Android打包前原库与安装产物一致、APK与去除调试符号后的库一致；iOS最终框架19个ABI导出、核心build ID及sqlite3符号隔离通过。

下方“实际仓库性能”中的历史数字保留各自版本说明；本轮新增查询测量另列。真机系统行为、Windows实际运行、24小时稳定性及最低设备性能验收仍待执行。

## 本轮流程根因修复的验证范围

| 修复 | 实现与正式回归 |
| --- | --- |
| 原生注册与取消 | Watch 后立即 Refresh 等待 observe 完成；取消未完成 imagesOpen 后等待原生结束，再关闭扫描。测试通过内部传输接口运行生产生命周期，不在生产源码插入临时探针 |
| Limited 范围确认 | 成员摘要在完整核对后持久保存；确认后再扫描、观察重建、内容变化和索引重开不重复确认；真实成员变化继续要求确认 |
| 部分刷新通知 | 第一批已提交、后续枚举失败时，下次刷新仍通知之前尚未交付的变化；已尝试失败回调页不重放 |
| 自动备份准入 | 配置、扫描、循环、确认、重置、准备重试共用准入；回归覆盖双向冲突及操作结束后正常调用 |
| 单任务取消 | 取消当前上传后其他任务继续；已选页面中的后续任务被取消时，认领返回空并跳过，不使整轮失败 |
| 请求边界与未知结果 | 发请求前凭据失败保留原异常、零上传且任务 Failed；重试的新本地失败仍保留前次未知结果，服务器核对后可取消。前次未知结果由共享仓库判断，原生契约覆盖两个平台执行者，不依赖 C# 内存标记 |
| 重试前取消 | 重试入队后立即取消、暂停后取消及批量取消都保留未知结果并转回 NeedsAttention；保存的取消意图在服务器证明未提交后完成，其他批量目标继续 |
| 无任务清理故障 | 准备残留清理失败在重开后仍可独立分页查询；按 FileId 重试只处理该文件，其余故障保留 |
| Android 空闲轮询 | 空闲 drain 复用平台边界，脏事件、显式刷新或10秒周期再查询版本/generation；编译验证不等同真机功耗验收 |

`flow_query_benchmark.py` 从生产源码提取 SQL，使用发布核心和共享仓库检查清理故障页与 Limited 成员页。初次执行暴露旧清理索引被选中并产生临时排序；最终复用 `(state,id)` 索引，移除多余索引，验证故障分页使用 `files_cleanup`、任务关联使用 `tasks_file`、成员分页使用覆盖索引 `assets_page`。三档数据均无临时排序，成员完整遍历不超过每页200项；这些是桌面热查询与分页证据，不包含手机扫描、哈希或图片解码耗时。

本轮宿主测量（每组预热5次、采样25次；不同组同时运行的构建负载可能不同，不作跨组速度提升结论）：

| 记录数 | 清理故障页100项 p50 | 成员页200项 p50 | 完整成员分页遍历 | 最大页 |
| --- | --- | --- | --- | --- |
| 10,000 | 0.399 ms | 0.238 ms | 0.011 s | 200 |
| 100,000 | 0.255 ms | 0.112 ms | 0.066 s | 200 |
| 1,000,000 | 0.224 ms | 0.108 ms | 0.680 s | 200 |

本轮证据目录为 `/tmp/uiframe-flow-fix-20261001/`，包含正式Unity结果、共享仓库Release/ASAN/UBSAN、进程恢复、schema、Android Java/JNI、规模查询计划、移动构建及产物清单；修复前原生跨代次复现保留为 `native-retry-before.log`，取消语义以正式回归的状态断言为准。没有新增旧格式探测、迁移或自动故障重试。

## 权限与收尾遗漏修复验证（2026-10-01）

Unity 正式图库、备份与清理回归共67项通过（81.38秒），其中新增7项：权限撤销后的索引重开与显式确认；observe / drain / imagesOpen / imagesNext 拒绝访问后立即恢复；观察驱动失权后的停止与显式恢复；权限记录失败仍保留原访问错误；Start前暂停且释放失败；正常暂停收尾后继续其他任务；凭据主异常与释放次级错误同时发生。

同一组17项流程测试在修复前有5项失败，修复后全部通过。权限替身现在与实际Android / iOS一致：未授权的观察与枚举直接返回 PermissionDenied。释放故障通过临时数据库触发器模拟，生产代码没有测试专用分支；验证异常、任务状态、资源释放标记与后续独立任务，不只检查日志。测试触发器不进入发布schema。

本轮仅修改托管业务逻辑与测试，原生核心、共享BackupRepository、schema和平台二进制沿用前轮产物；未把前轮移动构建算作本轮新构建。权限事实通过同一刷新串行入口记录，正常空闲轮询没有新增数据库操作。

`flow_query_benchmark.py` 在同一发布引擎上补验新增权限更新：1万 / 10万 / 100万条图片记录均使用 `library_scopes` 的主键索引定位单个范围，没有图片表扫描。Authorized / Limited 的重复拒绝只推进一次代次并清除成员摘要，Restricted保持原值，其他范围不受影响；原有清理与成员分页验证同时通过，每页仍不超过200项。这是SQL行为与查询计划证据，不代表手机权限切换耗时或长期性能验收。

证据目录 `/tmp/uiframe-permission-cleanup-fix-20261001/` 保留修复前后XML、完整回归日志及查询计划。`before-final-results.xml` 是完成故障夹具校正后的修复前基线，`fixed-flow-results.xml` 与 `unity-results.xml` 为最终通过结果。真机权限交互、Windows运行及长期性能门槛仍需独立验收。

## 实际仓库性能

`Runtime/MediaBackup/Native~/tests/benchmark.py` 先用同一核心造数，随后经生产BackupRepository ABI打开、读取100条完整任务页和汇总。每组使用新进程、20次预热和100次采样；以下是复审修复前build ID `2905c1f20acfb01edc49f20dd49d56a49bbf8a1b38d8ff25a25f75ff2e161bf5` 的基准，本轮没有重跑，不作为新版本性能验收。

| 历史数 | 重开ms | 任务页p50 / p95 ms | 汇总p95 ms | 进程峰值RSS MiB | 主库MiB |
| --- | --- | --- | --- | --- | --- |
| 10,000 | 1.071 | 0.114 / 0.122 | 0.014 | 24.95 | 7.46 |
| 100,000 | 0.744 | 0.118 / 0.124 | 0.013 | 24.83 | 74.49 |
| 1,000,000 | 1.414 | 0.175 / 0.201 | 0.017 | 25.22 | 752.58 |

造数约0.31 / 2.93 / 37.33秒；完整任务页约40KiB。RSS包含Python与原生库，不是Unity托管堆或手机峰值。文件系统为桌面热状态，不包含手机I/O、照片解码、渲染和IL2CPP对象映射。百万条记录未引入全历史内存物化。

另经核心schema基准确认状态分页使用 `tasks_state_page` 索引；1万 / 10万 / 100万行的简化双列页p95约0.035 / 0.033 / 0.038ms。该查询比实际仓库返回字段少，不能将它作为完整任务页的性能。

继续审查修复后的维护 / 来源更新另有实际测量。正式新增脚本从零造数，使用最终仓库，预热2次、测量10次；以下为macOS宿主p50，不代替手机验收。来源更新数字包含单条更新的事务交付，不包含完整接受/确认命令及图片I/O；其执行计划已从全历史SCAN变为按 `discoveries_source` 定位。

| 记录数 | 保留文件零删除维护 | 有效基线零删除维护 | Accept来源更新 | Finish来源更新 |
| --- | --- | --- | --- | --- |
| 10,000 | 0.15ms | 0.11ms | 4.01ms | 4.00ms |
| 100,000 | 0.16ms | 0.11ms | 4.00ms | 4.00ms |
| 1,000,000 | 0.15ms | 0.11ms | 4.02ms | 4.02ms |

早一次在已造数库上的相应计时约0.08ms / 0.07ms；两轮都确认零删除维护不会随受保留记录数增长。查询和实际命令的用途不同，以上结果与上一节旧版打开/分页数字分开记录。

## 复现入口

从包目录运行；工具链及库路径显式填写，不自动下载或替换环境：

```sh
python3 Runtime/Sqlite/Native~/tests/schema_contract.py <core-library>
python3 Tests/Native~/run_backup_persistence.py --cmake <cmake> --build <test-build> --sqlite-library <core-library>
python3 Runtime/MediaBackup/Native~/tests/process_windows.py --core <core-library> --repository <backup-library>
python3 Runtime/MediaBackup/Native~/tests/scope_reconciliation.py --core <core-library> --repository <backup-library>
python3 Tests/Native~/android_backup_binding.py --jdk <jdk-home> --android-jar <android-sdk-jar> --core <macOS-core-library> --repository <macOS-backup-library>
python3 Runtime/MediaBackup/Native~/tests/flow_query_benchmark.py --core <core-library> --repository <backup-library> --rows 10000 100000 1000000 --output <flow-queries.json>
python3 Runtime/MediaBackup/Native~/tests/maintenance_benchmark.py --core <core-library> --repository <backup-library> --rows 10000 100000 1000000 --output <maintenance.json>
python3 Runtime/MediaBackup/Native~/tests/benchmark.py --core <core-library> --repository <backup-library> --rows 10000 100000 1000000 --output <result.json>
python3 Tests/Native~/prepare_media_validation.py --project <isolated-project> --host-project <host-project>
```

先运行 `Tools~/BackupServer/integration_server.py`，再在隔离Unity工程运行 `UIFrame.Regression`，覆盖 MediaTests、MediaLifecycleTests 和 MediaFlowTests。`MediaBuildSmoke.Android` / `Ios` 创建发布构建，`MediaSmoke`提供设备运行入口。真实设备成功标记需在设备上取得，不能由构建日志代替。

此前R1–R12证据位于 `/tmp/uiframe-followup-fix-20261001`，包括 `media-final-results.xml`、`server-tests.log`、`scope-reconciliation.log`、`android-binding.log`、`process-recovery.log`、`asan-build.log`、`final-performance.json`、`maintenance-performance.json`、移动发布日志及 `artifacts.json`。隔离工程仍为 `/tmp/uiframe-media-plan-android`、`/tmp/uiframe-media-plan-ios`；临时工程和日志不作为包内产物提交。核心独立验证详见 [SQLite验证记录](../Runtime/Sqlite/Validation.md)。

## 仍未验收

1. Android / iOS 真机：系统选图、部分授权变化、云端照片、目录授权、前后台 / 锁屏、系统杀进程和用户强制停止、Keystore / Keychain、无Unity启动时的系统回调。
2. iOS数据库与WAL / SHM的锁屏保护继承、首次解锁前失败、磁盘满时释放OS回调；Android持久调度取消和存储失败。当前代码不能代替这些系统行为的实测。
3. Windows原生测试、Windows Editor / IL2CPP与MSVC构建。只有交叉编译和依赖检查。
4. 最低设备的60 / 120FPS帧预算、十万项批量操作与核对的公平性、5000张照片10轮产品虚拟列表、24小时混合压力、真实设备GC / 纹理 / 原生堆峰值。
5. 完整仓库故障注入与平台适配矩阵。底层已有磁盘满 / fsync / 分配器 / COMMIT中断测试，但不能替代每个业务与系统回调窗口。

后台发现新照片、生产云部署、视频 / Live Photo / RAW套件、双向删除不在本轮范围。当前Editor提供分页任务中心和共享预览，UGUI示例新增LoopScroll分页虚拟列表；产品仍需执行上述滚动验收。已交付 `.github/workflows/native-storage.yml` 原生构建/契约/符号归档配置，尚未取得远端运行记录。项目Unity授权CI、设备农场、遥测和运营告警未接入。

## 此前修复与持续验收

当前继续审查映射见 [GallerySqliteFollowupAudit.md](GallerySqliteFollowupAudit.md)，此前F1–F7见 [GallerySqlitePlanAudit.md](GallerySqlitePlanAudit.md)。没有保留临时审查钩子或增加旧格式识别。仓库四平台来源/安装二进制校验一致；库和C#门面仍依赖原有ABI3核心，本轮未修改通用核心源文件。PhotoKit的无相关变化分支已修复并编译/链接，系统通知仍需真机验证。

正式用例包括 `Tests/Editor/Media/MediaTests.cs`、`Tests/Editor/Media/MediaLifecycleTests.cs`、`Tests/Editor/Media/MediaFlowTests.cs`、共享仓库 `tests/repository_tests.cpp`、`tests/scope_reconciliation.py`、`Tests/Native~/android_backup_binding.py` 和 `Tools~/BackupServer/test_server.py`。最终Unity及移动构建证据均在上述本次目录。主Unity未重启，若它已经加载旧原生库，需重新打开工程后才加载新二进制。

内存回归覆盖已有合并、取消、租约及清理路径；新增在途解码值为估算，待销毁值覆盖管线的统一Destroy出口。尚未测量真机解码内部峰值或运行5000张/10轮与24小时负载。文件刷盘回归和进程中断不能证明真实设备掉电可靠性。

## 完整范围审查修复（2026-10-01）

准备失权、核对与重试的远端归属、图片交付失败资源回收、服务器清理隔离及活动尝试查询已逐项修复。Unity 74/74、服务器25/25、原生Release与ASAN/UBSAN、原有13加新增2个进程窗口、Android Java/JNI、Android发布与iOS无签名设备构建均通过；四平台产物已更新。最终实现、查询测量及准确验证边界见[本轮修复记录](GalleryCompleteReviewFix.md)。本页前文各轮数量和产物哈希保留对应轮次，不代表重复运行；真机、Windows运行和长时验收仍未完成。
