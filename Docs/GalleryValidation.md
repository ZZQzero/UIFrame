# 图片与长期备份验证记录

更新：2026-10-01（执行跨2026-09-30 / 10-01）。本页记录当前 SQLite 实现的证据；此前 JSON 实现的测试数量与性能不作为当前版本验收结果。

## 当前结论

共享 BackupRepository、C# 图库索引、增量变化、共享缩略图、持久批量操作、分批清理及本机服务器 TTL 已接入。任务 / 策略 / 基线 / 回执没有旧 JSON 读取、格式探测、迁移、双写或全量 GetTasks 兼容接口。HTTP、平台桥接和构建清单仍使用 JSON。

代码实现和桌面 / 发布构建验证已完成，**两份实施计划的商业发布验收仍未全部完成**。没有真机、Windows运行、24小时稳定性及最低设备帧预算的证据；不能从构建或桌面数字推定。

## 环境与版本

- Unity 6000.3.19f1、macOS Apple Silicon；独立隔离工程验证，未重启用户主工程。
- SQLite 3.53.4 / ABI 3；核心 build ID：`cc2af4a34676c4a76a07358b30d6c4351d4b22a8bdba0f2c9c2e0954fdbbc305`。
- 共享仓库 ABI 1；四平台同源 build ID：`2905c1f20acfb01edc49f20dd49d56a49bbf8a1b38d8ff25a25f75ff2e161bf5`。
- Android ARM64，宿主最低API25，NDK27.2.12479018、CMake3.22.1、JDK17。NDK把native API25规范化为24，不改变宿主最低版本。
- iOS arm64设备目标、最低15.0、Xcode / SDK27.0；macOS库为arm64+x86_64、最低11.0。
- Windows x64用LLVM-MinGW 20260922 UCRT交叉构建；没有Windows实际运行结果。

## 已执行验证

| 检查 | 当前结果 |
| --- | --- |
| 共享仓库原生契约 | Release通过；同源ASAN / UBSAN通过。覆盖准备原子性、预算、认领与开始竞态、代次、确认胜过取消、实际释放前禁止清理、独立回执、基线连续性、批量操作幂等与上界、暂停 / 范围重置、保留数量及过期明细 |
| 实际进程中断 | 7/7通过：准备完成、接受、认领、开始、确认、释放、文件已删除。强制结束子进程后经生产仓库ABI重开，检查准备ID、回执、未知结果和清理归属；没有用测试SQLite替代 |
| 两库schema | 通过同一核心建表、标识 / 约束及删除历史不丢操作结果检查；trigger按完整SQL语句解析 |
| Unity图片回归 | 34项：33通过、1项因要求Android活动目标跳过，耗时约13.5秒。该项随后在Android目标单独通过；对应34个不同用例均有通过结果 |
| HTTP与错误身份 | 真实本机上传、幂等重提、清历史后下载校验；能力缓存与无效能力终止批次；凭据 / 策略回调保留异常身份；响应 / 下载大小边界、超时、取消、已有目标不覆盖 |
| 图库与缓存 | 分页、跨范围内容版本、首次基线、关闭排空；100等待者只处理一次、独立取消、失效不破坏已交付租约、来源释放、旧完成隔离、交付失败所有等待者结束与准入归还 |
| 批量操作与维护 | 持久ID、重复参数、全局 / 单项暂停关系；35个连续提交及过期清理；修复驱动退出时新提交错过唤醒，修复已过期头阻塞后续明细 |
| 本机服务器 | 18项真实HTTP回归通过，含TTL、活动上传占用、显式重建epoch、中断清理与完成副本保留；服务仅面向单进程本机使用 |
| 全Runtime及UGUI样例 | Unity引用下Roslyn编译通过；隔离Unity工程同时验证运行时、Editor和测试程序集 |
| Android发布 | IL2CPP、High裁剪、Release混淆APK成功；包内一份SQLite核心和一份仓库；JNI内部错误类型保留；权限配置可选且幂等测试通过 |
| iOS发布 | IL2CPP、High裁剪导出及Xcode Release无签名设备构建成功；19个ABI导出（15核心+4仓库）、当前核心build ID及原始sqlite3符号隔离通过 |
| 来源和导入 | 四目标产物校验值与锁定源码一致；Unity可见资源meta齐全；主Editor仍可能映射旧动态库，重新打开主工程后才使用新原生实现 |

Android最终APK SHA-256：`3188cae8d7bd7ffecaec6d4cf8b4ed7e6ea7fe715fb3a6f1757aa5d9be89598d`。iOS最终UnityFramework SHA-256：`1c0250685fbc51dc718d7a27ddb67e32c87cb5a6f8afd2b3cb12ca0ea62729e9`。

iOS验证期间发现并修复导出工程关闭Objective-C异常的问题，构建处理器现在同时配置异常及ARC异常收尾。Android构建处理器保留JNI使用的内部Failure类型。平台适配不再自动重新上传已消失执行者的未知结果；该任务需要按旧幂等身份核对。iOS回调使用attempt_generation，容量释放后按等待仓库顺序继续调度。

原生内存工具仅覆盖共享仓库和核心，未覆盖手机PhotoKit / Bitmap / URLSession内部，也不代表线程竞争或长期泄漏矩阵已完成。旧Foundation持久化替身已移除；当前进程中断测试针对真实共享仓库，仍需系统适配的无Unity运行测试。

## 实际仓库性能

`Runtime/MediaBackup/Native~/tests/benchmark.py` 先用同一核心造数，随后经生产BackupRepository ABI打开、读取100条完整任务页和汇总。每组使用新进程、20次预热和100次采样；以下对应当前仓库build ID。

| 历史数 | 重开ms | 任务页p50 / p95 ms | 汇总p95 ms | 进程峰值RSS MiB | 主库MiB |
| --- | --- | --- | --- | --- | --- |
| 10,000 | 1.071 | 0.114 / 0.122 | 0.014 | 24.95 | 7.46 |
| 100,000 | 0.744 | 0.118 / 0.124 | 0.013 | 24.83 | 74.49 |
| 1,000,000 | 1.414 | 0.175 / 0.201 | 0.017 | 25.22 | 752.58 |

造数约0.31 / 2.93 / 37.33秒；完整任务页约40KiB。RSS包含Python与原生库，不是Unity托管堆或手机峰值。文件系统为桌面热状态，不包含手机I/O、照片解码、渲染和IL2CPP对象映射。百万条记录未引入全历史内存物化。

另经核心schema基准确认状态分页使用 `tasks_state_page` 索引；1万 / 10万 / 100万行的简化双列页p95约0.035 / 0.033 / 0.038ms。该查询比实际仓库返回字段少，不能将它作为完整任务页的性能。

## 复现入口

从包目录运行；工具链及库路径显式填写，不自动下载或替换环境：

```sh
python3 Runtime/Sqlite/Native~/tests/schema_contract.py <core-library>
python3 Tests/Native~/run_backup_persistence.py --cmake <cmake> --build <test-build> --sqlite-library <core-library>
python3 Runtime/MediaBackup/Native~/tests/process_windows.py --core <core-library> --repository <backup-library>
python3 Runtime/MediaBackup/Native~/tests/benchmark.py --core <core-library> --repository <backup-library> --rows 10000 100000 1000000 --output <result.json>
python3 Tests/Native~/prepare_media_validation.py --project <isolated-project> --host-project <host-project>
```

先运行 `Tools~/BackupServer/integration_server.py`，再在隔离Unity工程运行 `UIFrame.Regression.MediaTests`。`MediaBuildSmoke.Android` / `Ios` 创建发布构建，`MediaSmoke`提供设备运行入口。真实设备成功标记需在设备上取得，不能由构建日志代替。

本轮临时证据位于 `/tmp/uiframe-media-plan-validation`、`/tmp/uiframe-media-plan-android`、`/tmp/uiframe-media-plan-ios` 和 `/tmp/uiframe-backup-final-benchmark.json`；不作为包内产物提交。核心独立验证详见 [SQLite验证记录](../Runtime/Sqlite/Validation.md)。

## 仍未验收

1. Android / iOS 真机：系统选图、部分授权变化、云端照片、目录授权、前后台 / 锁屏、系统杀进程和用户强制停止、Keystore / Keychain、无Unity启动时的系统回调。
2. iOS数据库与WAL / SHM的锁屏保护继承、首次解锁前失败、磁盘满时释放OS回调；Android持久调度取消和存储失败。当前代码不能代替这些系统行为的实测。
3. Windows原生测试、Windows Editor / IL2CPP与MSVC构建。只有交叉编译和依赖检查。
4. 最低设备的60 / 120FPS帧预算、十万项批量操作与核对的公平性、5000张照片10轮产品虚拟列表、24小时混合压力、真实设备GC / 纹理 / 原生堆峰值。
5. 完整仓库故障注入与平台适配矩阵。底层已有磁盘满 / fsync / 分配器 / COMMIT中断测试，但不能替代每个业务与系统回调窗口。

后台发现新照片、生产云部署、视频 / Live Photo / RAW套件、双向删除不在本轮范围。当前Editor提供分页任务中心和共享预览；产品虚拟列表及业务UI需按游戏接入并执行上述滚动验收。按需诊断已实现，项目遥测 / CI / 运营告警不由本机测试自动部署。
