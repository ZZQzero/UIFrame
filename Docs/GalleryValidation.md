# 图片与长期备份验证记录

更新：2026-10-01（执行跨2026-09-30 / 10-01）。本页记录当前 SQLite 实现与复审修复的证据；此前 JSON 实现的测试数量与性能不作为当前版本验收结果。

## 当前结论

共享 BackupRepository、C# 图库索引、增量变化、共享缩略图、持久批量操作、分批清理及本机服务器 TTL 已接入。任务 / 策略 / 基线 / 回执没有旧 JSON 读取、格式探测、迁移、双写或全量 GetTasks 兼容接口。HTTP、平台桥接和构建清单仍使用 JSON。

复审F1–F7、继续审查R1–R12及本轮六项失败边界 / 生命周期简化已完成代码修复，桌面 / 发布构建验证已有证据，**两份实施计划的商业发布验收仍未全部完成**。没有真机、Windows运行、24小时稳定性及最低设备帧预算的证据；不能从构建或桌面数字推定。

## 环境与版本

- Unity 6000.3.19f1、macOS Apple Silicon；独立隔离工程验证，未重启用户主工程。
- SQLite 3.53.4 / ABI 3；核心 build ID：`cc2af4a34676c4a76a07358b30d6c4351d4b22a8bdba0f2c9c2e0954fdbbc305`。
- 共享仓库 ABI 1；四平台同源 build ID：`20930774efc528f533217a58c29dfedcf0f242310b4b059d82da2335efc93bc3`。
- Android ARM64，宿主最低API25，NDK27.2.12479018、CMake3.22.1、JDK17。NDK把native API25规范化为24，不改变宿主最低版本。
- iOS arm64设备目标、最低15.0、Xcode / SDK27.0；macOS库为arm64+x86_64、最低11.0。
- Windows x64用LLVM-MinGW 20260922 UCRT交叉构建；没有Windows实际运行结果。

## 已执行验证

| 检查 | 当前结果 |
| --- | --- |
| 共享仓库原生契约 | Release通过；同源ASAN / UBSAN通过。覆盖准备原子性、预算、认领与开始竞态、代次、确认胜过取消、原生Handoff/系统绑定前拒绝Start、凭据由实际持有者释放、Seal缺失/长度错误与正常刷盘、实际释放前禁止清理、独立回执、基线连续性、批量操作幂等与上界、暂停 / 范围重置、保留数量及过期明细 |
| 实际进程中断 | 13/13通过：准备完成、接受、认领、开始、确认、释放、文件已删除7个窗口，另加Android/iOS执行者各自认领/Handoff/系统绑定后中断的6个窗口。强制结束子进程后经生产仓库ABI重开，检查准备ID、回执、未知结果和清理归属；没有用测试SQLite替代 |
| 两库schema | 通过同一核心建表、标识 / 约束及删除历史不丢操作结果检查；trigger按完整SQL语句解析 |
| Unity图片回归 | 最终50/50通过（约61.2秒），Android活动目标，启用图形设备；41项原有回归及9项生命周期、清理隔离、版本校验回归全部通过。新增覆盖下载中恢复 / 清理、能力刷新双向互斥、准备中暂停、关闭拒绝新受理并等待已受理查询、单文件清理故障与定向重试、通知异常与等待取消不重扫、连续切换24个范围、共享及在途观察释放、相同文件stat下的内容变化 |
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

Android最终APK SHA-256：`09d8137437c00ce995d1bd0f565b0c91ce03cded1a90dcf31c45e441afa74306`。iOS最终UnityFramework SHA-256：`2dafc7471eda85d88c1ae562115ed49504207675e151c5762552e187f9587f59`。

iOS验证期间发现并修复导出工程关闭Objective-C异常的问题，构建处理器现在同时配置异常及ARC异常收尾。Android构建处理器保留JNI使用的内部Failure类型。平台适配不再自动重新上传已消失执行者的未知结果；该任务需要按旧幂等身份核对。iOS回调使用attempt_generation，容量释放后按等待仓库顺序继续调度。

原生内存工具仅覆盖共享仓库和核心，未覆盖手机PhotoKit / Bitmap / URLSession内部，也不代表线程竞争或长期泄漏矩阵已完成。旧Foundation持久化替身已移除；当前进程中断测试针对真实共享仓库，仍需系统适配的无Unity运行测试。

## 本轮六项修复的验证范围

| 修复 | 实现与验证 |
| --- | --- |
| 单文件清理隔离 | 共享仓库只在故障持久保存后返回1001；C#、Android、iOS依同一错误分类处理。原生Release / ASAN / UBSAN、Unity及Android真实Java/JNI路径通过。显式维护提供部分统计，单项重试不删除其他候选；iOS运行行为仍待设备验证 |
| 局部互斥与关闭 | 下载中恢复队列、刷新能力和清理通过；能力刷新与上传互斥；准备中暂停通过。关闭拒绝新调用并排空已受理查询；在途登记使用值类型，无每次登记的独立对象分配 |
| 索引与通知分阶段 | 直接和自动通知异常保留同一异常；恢复通知不触发全量重扫；等待刷新锁取消不破坏已完成索引 |
| 观察者归属 | 顺序使用24个范围不耗尽16个同时观察名额；关闭一个订阅不影响同范围另一个；最后订阅等待在途刷新，释放后位置明确要求重新核对 |
| 复制与哈希 | 目录复制只读取源文件一次并同时计算哈希，复制前后校验元数据；同size/mtime但内容不同的索引引用被拒绝；原生Seal仍负责最终刷盘。减少了完整读取次数，但未提供最低设备耗时 / 峰值内存基准 |
| 图库字段收敛 | 删除未使用占位字段，同步单一SQL源和生成文件；两库schema检查与Unity索引 / 自动备份回归通过；无旧schema兼容或迁移 |

本轮证据位于执行环境 `/tmp/uiframe-design-fix-20261001/`：Unity XML结果、四平台仓库构建、ASAN / UBSAN、13个真实进程中断窗口、范围核对、两库schema、Android Java/JNI、Android发布APK、iOS导出及Xcode Release无签名构建日志，以及 `artifacts.json`。四平台仓库产物与源码清单一致；Android打包前原库与安装产物一致、APK与去除调试符号后的库一致；iOS最终框架19个ABI导出、核心build ID及sqlite3符号隔离通过。

以下规模性能数字保留各自历史版本说明，不作为本轮重新测量结果。真机系统行为、Windows实际运行、24小时稳定性及最低设备性能验收仍待执行。

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
python3 Runtime/MediaBackup/Native~/tests/maintenance_benchmark.py --core <core-library> --repository <backup-library> --rows 10000 100000 1000000 --output <maintenance.json>
python3 Runtime/MediaBackup/Native~/tests/benchmark.py --core <core-library> --repository <backup-library> --rows 10000 100000 1000000 --output <result.json>
python3 Tests/Native~/prepare_media_validation.py --project <isolated-project> --host-project <host-project>
```

先运行 `Tools~/BackupServer/integration_server.py`，再在隔离Unity工程运行 `UIFrame.Regression.MediaTests`。`MediaBuildSmoke.Android` / `Ios` 创建发布构建，`MediaSmoke`提供设备运行入口。真实设备成功标记需在设备上取得，不能由构建日志代替。

本次最终证据位于 `/tmp/uiframe-followup-fix-20261001`，包括 `media-final-results.xml`、`server-tests.log`、`scope-reconciliation.log`、`android-binding.log`、`process-recovery.log`、`asan-build.log`、`final-performance.json`、`maintenance-performance.json`、移动发布日志及 `artifacts.json`。隔离工程仍为 `/tmp/uiframe-media-plan-android`、`/tmp/uiframe-media-plan-ios`；临时工程和日志不作为包内产物提交。核心独立验证详见 [SQLite验证记录](../Runtime/Sqlite/Validation.md)。

## 仍未验收

1. Android / iOS 真机：系统选图、部分授权变化、云端照片、目录授权、前后台 / 锁屏、系统杀进程和用户强制停止、Keystore / Keychain、无Unity启动时的系统回调。
2. iOS数据库与WAL / SHM的锁屏保护继承、首次解锁前失败、磁盘满时释放OS回调；Android持久调度取消和存储失败。当前代码不能代替这些系统行为的实测。
3. Windows原生测试、Windows Editor / IL2CPP与MSVC构建。只有交叉编译和依赖检查。
4. 最低设备的60 / 120FPS帧预算、十万项批量操作与核对的公平性、5000张照片10轮产品虚拟列表、24小时混合压力、真实设备GC / 纹理 / 原生堆峰值。
5. 完整仓库故障注入与平台适配矩阵。底层已有磁盘满 / fsync / 分配器 / COMMIT中断测试，但不能替代每个业务与系统回调窗口。

后台发现新照片、生产云部署、视频 / Live Photo / RAW套件、双向删除不在本轮范围。当前Editor提供分页任务中心和共享预览，UGUI示例新增LoopScroll分页虚拟列表；产品仍需执行上述滚动验收。已交付 `.github/workflows/native-storage.yml` 原生构建/契约/符号归档配置，尚未取得远端运行记录。项目Unity授权CI、设备农场、遥测和运营告警未接入。

## 此前修复与持续验收

当前继续审查映射见 [GallerySqliteFollowupAudit.md](GallerySqliteFollowupAudit.md)，此前F1–F7见 [GallerySqlitePlanAudit.md](GallerySqlitePlanAudit.md)。没有保留临时审查钩子或增加旧格式识别。仓库四平台来源/安装二进制校验一致；库和C#门面仍依赖原有ABI3核心，本轮未修改通用核心源文件。PhotoKit的无相关变化分支已修复并编译/链接，系统通知仍需真机验证。

正式用例包括 `Tests/Editor/Media/MediaTests.cs`、`Tests/Editor/Media/MediaLifecycleTests.cs`、共享仓库 `tests/repository_tests.cpp`、`tests/scope_reconciliation.py`、`Tests/Native~/android_backup_binding.py` 和 `Tools~/BackupServer/test_server.py`。最终Unity及移动构建证据均在上述本次目录。主Unity未重启，若它已经加载旧原生库，需重新打开工程后才加载新二进制。

内存回归覆盖已有合并、取消、租约及清理路径；新增在途解码值为估算，待销毁值覆盖管线的统一Destroy出口。尚未测量真机解码内部峰值或运行5000张/10轮与24小时负载。文件刷盘回归和进程中断不能证明真实设备掉电可靠性。
