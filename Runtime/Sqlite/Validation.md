# SQLite 实施与验证记录

日期：2026-09-30。此记录区分实现、host验证、移动端发布验收；不把构建成功当成真机验收。

## 交付范围

已实现 `UIFrame.Sqlite` 独立程序集、共享原生核心、参数批次、事务与行数条件、查询映射、取消隔离、配额、关闭、域生命周期接线、快照、空间查询和checkpoint。附存档 / 背包仓库示例、官方引擎来源锁、ABI说明、可复现构建安装脚本和平台导入设置。

照片业务现已接入：C# 图库仓库使用 UIFrame.Sqlite，C# / Android / iOS 备份共同调用原生 BackupRepository；业务库链接本核心，没有嵌入第二份引擎。图库、缓存、批量操作、清理与当前证据见 [图片验证记录](../../Docs/GalleryValidation.md)。

## 本批次：底层契约修复（ABI 3）

这是两份计划按依赖顺序执行的底层修复批次；后续照片接入已落地，其设备和长时门槛仍未通过。当前四个平台原生产物构建 ID：`cc2af4a34676c4a76a07358b30d6c4351d4b22a8bdba0f2c9c2e0954fdbbc305`。

- 同库写入按 `commit_request` 的正式受理顺序执行。队列节点在预留阶段分配，受理时移动既有节点，不增加提交点分配。关闭不会等待未受理预留；关闭受理后，旧预留不得再提交。
- 关闭专用容量限制为每库一个预留，参数长度与结果预算必须为零；丢弃关闭预留后恢复该额度，正常查询仍可继续，避免清理入口绕过缓冲配额。
- 完成分发故障通过独立的原生客户端停止协议收尾：结束在途工作、关闭所属数据库、释放未交付结果；已交付结果保持有效，其他客户端独立运行。最终关闭保留原异常，显式 Shutdown 仍尝试释放剩余原生所有权。
- Editor 域收尾覆盖弱引用已清除、终结器尚未执行的结果；统一登记表保证延后的终结器不重复释放。是否能开启新的 Editor 寿命取决于原生客户端是否已经释放，不以历史异常代替资源状态。
- 加入错误阶段、排队 / 执行数量、参数 / 结果字节、等待 / 执行 / 提交耗时、缓存命中与错误 / 取消 / 超时计数。统计按需读取，不采集业务参数；聚合耗时不能代替真机 p95 / p99。
- ExecuteAsync 支持单次命令截止时间；新增回归验证超时回滚及后续独立写入。C ABI 升为3，完成记录320字节、诊断记录176字节，四个平台同步替换，没有旧 ABI 解析或回退路径。

已通过：4组原生测试、同4组 ASAN / UBSAN、17项独立 Mono 测试、游戏仓库示例、5个进程中断窗口、两库 schema 契约。新增排序测试用受控占用的写连接确定执行顺序；故障测试验证异常身份、原生资源归零、结果跨关闭存活与独立客户端继续工作。

独立 Unity EditMode 17/17 通过；Android Release / IL2CPP / High 裁剪构建通过，APK SHA-256 为 `00a2ae911107787144dbc0acec08ce302533209a392eacd0ba3d78537b4d0e3f`，包内仅一份 SQLite 核心。iOS 同配置导出及 Xcode 无签名设备构建通过；最终 UnityFramework 已核对当前构建 ID、全部15个 C ABI 入口和原始 SQLite 符号隔离。Windows五个测试程序已交叉编译，实际运行与MSVC构建仍未执行。移动端真机、24小时稳定性、最低设备性能及完整故障矩阵尚未验收。

## 环境与产物

- Unity 6000.3.19f1，macOS / Apple Silicon Editor。
- SQLite 3.53.4，源码ID和下载校验见source-lock.json；当前ABI为3。
- Xcode工具链与SDK27.0；macOS universal arm64+x86_64，最低11.0；iOS设备arm64，最低15.0。
- Android NDK27.2.12479018，CMake3.22.1；arm64-v8a。宿主min API25，NDK将native API25规范化为24（不改变宿主minSdk）。
- Android ELF段对齐16 KiB；动态依赖仅libm / libdl / libc。macOS动态导出只有ufsqlite接口，iOS静态库无原始sqlite3全局符号。
- 每平台Plugins目录有源码哈希、构建ID、产物SHA-256、CMake配置。以当前artifact.json为准，不手写另一套二进制版本记录。

上一轮移动端与Editor完整验证构建ID：`a5e4ac71db64112d77454c33a2aecef7f402df454dba0250338af50a6a872fc2`。当时已核对Editor、Android APK和iOS UnityFramework均包含此ID。Android测试APK SHA-256：`ea3f4a47df2ef360c821a6352c46c228fdf01b5b1d3318ab238c3048f039873c`。测试工程由Native~/tests/prepare_unity_project.py重建，不作为业务包内容提交。该记录不代表下述Windows补齐后的构建已经重跑移动端IL2CPP。

## 此前Windows补齐批次（ABI 2，2026-09-30）

该批次四个平台原生产物的构建ID均为 `3421b9e050554da6236a3213450ea25671402a7fabab8aa88727c0324dce015d`，来源及二进制哈希已逐个核对。

- 新增 `Plugins/Windows/x86_64/uiframe_sqlite.dll`，约2.3 MiB；SQLite 3.53.4与共享核心编入同一DLL，未另外引入官方sqlite3.dll。对应PDB约4.8 MiB，保留在构建目录。
- 使用LLVM-MinGW 20260922 UCRT在macOS上完成x64交叉编译。PE32+机器类型、全部13个C ABI导出及动态依赖检查通过；仅依赖KERNEL32与Windows UCRT系统接口，没有额外C++运行库DLL，也没有导出原始SQLite接口。
- Unity实际导入器检查通过：Editor开启、OS=Windows、CPU=x86_64，Win64开启，Win32与Any关闭。已补Windows构建前来源校验，以及原生构建、安装和独立IL2CPP测试入口。
- 文件系统边界统一使用UTF-8与原生宽字符转换；修正Windows只读打开所需权限、已有空文件的失败语义和所有权锁位置。错误消息按UTF-8解码。工作线程由首个客户端启动、最后一个客户端释放时结束，正确关闭后DLL卸载不再需要等待存活线程。
- macOS公共核心3组原生测试及ASAN/UBSAN全部通过，包括中文/emoji目录、快照、8轮客户端关闭重开、事务及I/O/内存故障；进程中断5/5恢复场景通过。13/13独立Mono接口测试及游戏仓库示例通过。
- 新建独立Unity工程加载当前macOS产物，13/13 EditMode测试通过；本轮C#与Editor脚本编译通过。Android和iOS原生库已同步重建并安装，四个平台源码和构建ID一致。

**该批次未验收部分：** 当前机器没有Windows运行环境，四个Windows测试程序已编译但未运行；MSVC本机构建、Windows文件锁/只读权限/DLL卸载的实际运行、Windows Editor及Windows IL2CPP Player仍需在Windows执行README中的步骤。macOS的同源测试通过不能代替这些Windows验证。本轮没有重跑Android/iOS的IL2CPP打包和真机运行。现有主Editor可能仍映射更新前的库，使用更新后的原生实现前需重启Unity；上述13项Unity测试使用独立的新进程。

## 上一轮已通过的检查

| 检查 | 结果 |
| --- | --- |
| 原生契约测试 | 事务、条件回滚、单语句边界、只读、结果限制、配额、取消、超时、关闭、重开、跨客户端所有权、快照无覆盖通过 |
| 真实VFS故障注入 | WAL写入SQLITE_FULL、WAL fsync IOERR；未报告假成功，重开核对回执通过 |
| 独立进程中断 | 5个窗口；提交前可无回执，已确认提交保留10万行；未出现半批数据 |
| SQLite分配器故障注入 | 160档分配预算：53次可观察失败、107次正常完成；失败无部分回执，正常返回文本完整，后续独立查询可继续 |
| AddressSanitizer / UndefinedBehaviorSanitizer | 同一原生契约、VFS故障与分配器故障测试全部通过 |
| 独立Mono接口测试 | 12/12通过，包含映射异常身份、迟到取消隔离、结果跨关闭存活、schema重准备、水位和关闭等待 |
| 游戏仓库示例 | 存档读取、缺失槽位、重复发奖、余额不足回滚、独立回执通过 |
| 照片SQL schema | 两库建表、标识与版本；删除历史保留操作结果；任务序号不复用通过 |
| Android独立工程 | 不含UIFrame / UniTask / 照片模块的工程，Release / IL2CPP / High裁剪构建APK成功；包内只有一份ARM64存储核心，13个C ABI导出保留。尚未安装到真机 |
| iOS独立工程 | Release / IL2CPP / High裁剪导出及Xcode无签名设备构建链接通过；最终UnityFramework含13个ufsqlite入口，无原始sqlite3入口冲突。尚未安装到真机 |
| Unity EditMode | 当时ABI2 / 构建ID与清单一致，12/12通过；结束后数据库、未释放操作、预留缓冲均为0 |
| Editor生命周期 | 显式脚本域重载释放打开的库，重开读取已提交值；关闭域重载的播放 / 退出同样释放所有权并允许新的编辑器寿命重开，原项目设置已恢复 |

测试脚本和命令见README。ASAN/UBSAN不代表完成线程竞争分析或长时运行验证。

## 数据规模基准

`Native~/tests/benchmark.py` 使用同一固定引擎创建当前catalog表；每次事务最多200行，FULL + fullfsync开启；查询每页100项，稳定键及状态筛选。EXPLAIN QUERY PLAN确认使用tasks_state_page索引。

| 历史数量 | 重开耗时 ms | 分页 p50 / p95 / p99 ms | 进程最大RSS MiB | 主库 MiB |
| --- | --- | --- | --- | --- |
| 1万 | 1.14 | 0.029 / 0.039 / 0.048 | 19.7 | 1.43 |
| 10万 | 1.45 | 0.036 / 0.044 / 0.046 | 23.1 | 13.91 |
| 100万 | 1.36 | 0.033 / 0.043 / 0.046 | 22.8 | 144.58 |

基准对应build_id `f0ad63fe32067f91b652bd50805d9a3e67990f647a37a4fc665b0c897e7ed7f5`，记录的是首轮FULL落盘基线；随后加入低内存空指针保护和分配器故障用例，最终产物版本以Plugins清单为准。本表不作为最终版本性能验收。

这是macOS宿主、热文件系统、原生核心+C ABI+Python领取结果的基准，未包含Unity对象、托管实体映射、IL2CPP和移动设备I/O。RSS含Python及引擎，不是纯SQLite或托管堆指标。不能用这些数字宣称达到手机帧预算。样本200次、预热20次，三组各在新进程运行；构造数据耗时约0.29 / 3.43 / 37.14秒。

## 阶段门槛与后续工作

S0–S2已有实现与桌面证据，**阶段验收仍未完成**：没有连接Android / iOS设备，Android / iOS独立IL2CPP构建和设备目标链接已通过，但仍缺移动端实际运行、原生后台进程生命周期及其他插件共存的设备证据；Editor基本域生命周期已验证，故障和在途回调完整矩阵仍待补齐。S3已实现checkpoint / 水位 / 快照，仍需混合负载与磁盘故障矩阵。S4的24小时稳定性、最低设备性能和运营流程未验收；独立测试项目导入及构建已验证。

2026-10-01补充：照片业务已完成共享仓库、原生定位、图库 / 缓存 / 操作 / 清理接入；当前格式两库建表和约束测试再次通过，共享仓库7个实际进程中断窗口通过。照片阶段的真机、完整故障注入与长时验收仍保持未完成，不能由底层或桌面通过结果推定。

## 2026-10-01 照片复审修复与CI交付

本轮未修改SQLite通用核心；此前核心ABI3、独立托管回归和性能结果保留其原验证时间。照片业务的共享仓库已重新构建四目标，图库日志单SQL快照使用既有只读接口。新业务回归、ASAN/UBSAN、进程中断和移动构建结果见 [GalleryValidation.md](../../Docs/GalleryValidation.md)。

新增 `.github/workflows/native-storage.yml`，执行四目标原生构建、macOS/Windows宿主契约及来源清单/二进制/符号归档。Windows仓库构建入口支持本机MSVC；本地仅完成LLVM-MinGW交叉构建，不能据此标记Windows/MSVC运行通过。工作流尚未在GitHub实际运行；Unity/设备/运营接入仍待项目环境。

2026-10-01完整范围审查后，照片共享仓库已完成核对归属与调度查询修复并重新构建四目标；通用核心未改。8份安装产物及两端发布链接重新核对通过；本轮没有重复运行独立核心17项托管套件。照片回归与未验收项见[本轮修复记录](../../Docs/GalleryCompleteReviewFix.md)。
