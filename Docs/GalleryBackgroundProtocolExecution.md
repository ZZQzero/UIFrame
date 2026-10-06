# 照片备份 v2 执行记录

更新：2026-10-02。对应 [实施计划](GalleryBackgroundProtocolPlan.md)，当前 API 见 [Gallery.md](Gallery.md)。

客户端、共享仓库、参考服务、四平台库与本机可执行验证已交付。真机后台、24小时负载、Windows实际运行及生产云未验收；P7不能标记全部完成。当前无连接的Android / iOS设备。源码实现、宿主测试和系统实机行为分别记录。

实施后逐项复核及修正见 [审查报告](GalleryBackgroundProtocolAudit.md)：39条清单保持31项完成、8项未验收。后续完整复查及F1–F6修复见 [全量复查报告](GalleryBackgroundFullReview.md#6-修复结果与最终验证)。本文件验证表对应最终修复产物，旧吞吐基准保留原版本标识。

## 1. 实际改动映射

| 阶段 | 主要文件 | 已实现行为 |
| --- | --- | --- |
| P0 | `GalleryBackupProtocol.openapi.yaml`、`Tools~/BackupServer/protocol.py`、fixtures、validate_contract.py | 唯一v2、严格字段与预算、身份、回执、授权、取消仲裁，8组正例与未知字段反例 |
| P1 | `Tools~/BackupServer/{server,store}.py`、schema.sql、README、集成服务与测试 | 双origin、批量Plan / Query / Cancel、独立PUT、服务端持久核验、账号内已核验去重、不可变内容、取消墓碑、清理、列表与下载 |
| P2 | `Native~/schema/catalog.sql`、`src/{repository,protocol}.cpp`、repository_support.hpp、ufbackup.h、RepositoryContract | 唯一业务状态机、逐项接受、控制请求归属、代次与阶段、实际释放、失败隔离、保留策略；业务ABI / schema 2 |
| P3 | BackupSubmission、BackupProtocolExecutor、BackupExecutionControl、ImageBackupService、BackupContracts、BackupMaintenance、AutomaticImageBackup、Editor与示例 | 有界准备、持久Submit、部分失败查询、并发上传、滚动确认、准确受理阶段、生命周期、局部清理；保留权限 / 基线规则 |
| P4 | Android BackupBridge、BackupRepository、BackupJobService、BackupForegroundService、BackupPauseReceiver、Manifest | 四操作、共享仓库JNI、AES/GCM凭据、持久自动Job、用户主动UIDT / 前台服务、通知暂停、停止原因与有界工作线程 |
| P5 | `Runtime/Plugins/iOS/UIFrameBackup.mm`、构建处理器 | 文件式控制与照片请求、系统配额、恢复关联、Keychain属性核对与孤儿凭据清理、有限收尾、未来Query、completion handler错误出口 |
| P6 | 四平台Plugins、来源清单、Gallery文档、前置计划与Tests | 删除旧会话 / 分片 / commit / DTO与命令；无旧格式迁移或运行时降级；同步产物与测试 |
| P7 | protocol_tests、process_windows、protocol_benchmark、scheduler_benchmark、Unity测试、JNI测试、MediaSmoke | 本机故障窗口、资源边界、实际协议与稀疏活动查询验证；设备与长期门槛待执行 |

BackupQueries的分页类型沿用，结果在门面派生新阶段，没有另一套查询状态机。通用Runtime/Sqlite保持ABI3，照片业务状态仍只有一份。

## 2. 固定契约与收尾规则

唯一流程为有界准备 → 持久交接 → 批量Plan → 独立PUT → 服务端独立确认 → 滚动Query。Plan / Query已有回执立即应用；PUT 200 / 201 / 204可空正文，不作为业务成功依据。

- `SubmitAsync(operationId, images, token)` 每次1–32张，登记固定成员后逐项准备和接受；不等待服务器。移动系统调度受理后才返回完整提交成功。`QuerySubmissionAsync` 可重开查询，部分失败由 `BackupSubmissionException` 保留原异常和已受理结果。
- 一仓库一准备者，最多64个已准备未确认项；暂存与单文件默认512MiB、磁盘余量16MiB。控制文件另有8MiB预算；请求256KiB、响应512KiB、描述8KiB，桥接仍为1MiB。
- 每仓库2个照片上传与1个当前控制请求；未来Query另有排队槽。Plan聚合250ms、首次Query聚合1秒，Pending遵守nextCheckAt；第256次Pending或24小时截止停止自动确认。nextCheckAt等于或超过截止时直接结束，不提前轮询。共享Query以成员最早截止为请求边界，执行器取消后等待实际结束才释放；暂停/恢复不重置截止。无待调度时点返回整数0；系统不保证在墙钟截止时及时唤醒。
- Android每仓库3工作线程，最多4活跃仓库。自动Job持久保存；用户前台主动传输在API34以上使用UIDT、25–33使用前台服务，启动失败不降级。iOS所有会话最多16个系统任务，照片最多14个，实际传输机会由系统决定。
- 整批先验证账号、请求、成员、代次和嵌套内容，再逐项原子提交回执与应用标记；结构错误不会先发布前面成员的假成功。
- 暂停未提交Plan保留登记阶段，暂停未来Query保留检查时点。全局暂停不打断Cancel；中断上传后的取消意图可推进一次仲裁，Cancel自身失败不自动重发。
- 未知结果在旧资源实际释放后显式Reconcile，并保留已有取消意图；明确失败且暂存有效才能Retry，准备失败重新提交来源。回执与平台实际释放分别持久记录，不提前删文件。未使用的过期描述释放时，同一事务立即暴露下一次Plan。
- 单照片或控制文件清理失败持久隔离。`GetControlCleanupFailuresAsync` 分页查询，`RetryControlCleanupAsync` 只重试指定文件；删除后清空请求正文，诊断行保留30天再有界删除。
- 历史维护候选排除仍持有控制文件的任务，单项清理失败不能反复耗尽维护名额。Android恢复与启动按仓库互斥，网络IO不持生命周期锁。接受/失败只有逐项入口，准备恢复保留已接受文件。
- 业务、存储及网关必须是可信且不重定向的最终终点。Desktop/Android禁止自动跳转；iOS后台会话由系统跟随，客户端最终URL及可用metrics只做事后校验，不能阻止或撤回已转发字节。有效迟到回执仍可应用，过期Pending/UploadRequired不能继续自动推进。
- 提交不明停止依赖工作并保留可能已交接资源。Shutdown尝试全部自有清理，保留主异常，次级错误单独记录。相册失权按范围暂停，不能因稍后恢复授权而抹掉拒绝访问事实。

## 3. 构建与验证证据

工作区：`/Users/zzq/UIFrameTest/Packages/UIFrame`。原始证据：`/tmp/uiframe-background-v2-20261001.5cBjcQ`。临时日志不作为运行资源发布；本文件保留结论，测试和来源清单随包保存。

最终修复证据：`/var/folders/gk/fvyb98j97rv0xvb18qrzxppr0000gn/T/uiframe-review-fix-20261002-iqd48h49`；下表结果来自该目录。前次复审证据保留在 `/var/folders/gk/fvyb98j97rv0xvb18qrzxppr0000gn/T/uiframe-v2-audit-20261002-1_i2lw75`。

环境：Unity6000.3.19f1，Android SDK36、NDK27.2.12479018、JDK17、CMake3.22.1，Xcode iPhoneOS27.0 SDK、部署目标iOS15，macOS universal与LLVM-MinGW x64交叉构建。用户主Unity未重启，验证使用隔离项目。

业务仓库四平台构建ID：`cfb8809e8ceca2ffdd03356367f4b4053bdbfb82435c6729b6a5afcd1aeed3b8`。

SQLite构建ID：`cc2af4a34676c4a76a07358b30d6c4351d4b22a8bdba0f2c9c2e0954fdbbc305`（ABI3）。四平台来源哈希、二进制哈希与manifest一致。

| 目标与包内Plugins目录 | 本轮结果 |
| --- | --- |
| macOS arm64 / x64：`macOS` | universal构建、原生契约与Unity Editor运行通过 |
| Android arm64：`Android/arm64-v8a` | 原生库、JNI、IL2CPP High、Release混淆APK通过；真机未运行 |
| iOS arm64：`iOS` | 静态库、Objective-C++、IL2CPP High导出及Xcode无签名Release链接通过；真机未运行 |
| Windows x64：`Windows/x86_64` | DLL交叉构建及来源检查通过；Windows Editor / Player实际运行未验收 |

| 验证 | 结果 | 日志 / 数据 |
| --- | --- | --- |
| Unity Media回归 | 71项：70通过、0失败、1项目标不适用跳过；129.960s | unity-final.xml / log |
| Android专项权限测试 | 上述跳过项在Android目标单独1/1通过 | unity-permission.xml |
| B21强化 | 32项一慢一失败、其余30项完成，本次整合回归通过 | unity-final.xml |
| 共享协议ABI | 30/30通过，14.179s；6个关键回归在旧库全部失败 | protocol-final.log、before-regressions-final.log |
| 原生仓库契约 | Release通过；ASan / UBSan构建及契约通过 | build-macos.log、asan-final.log |
| 真实进程中断 | 3个executor各16窗口，48/48通过 | process-final.log |
| 显式核对跨进程 | 2/2通过 | reconciliation-final.log |
| 范围 / 分页 / 关联 | 通过 | scope-final.log |
| Android生产Java / JNI / 仓库 | 调度、追加受理、并发恢复、截止所有权与清理宿主测试通过，Context / Job / Handler为替身；未执行完整Java HTTP链路 | android-binding-final.log |
| iOS策略函数 | 生产目标与截止校验函数通过Foundation宿主测试，未执行后台会话 | ios-policy.log |
| 参考服务 | 33/33通过，2.620s | server.log，含慢速重复PUT回归 |
| 机器契约 | OpenAPI3.1及8组正反例通过；已修正iOS重定向能力说明，未改协议字段 | openapi.log；本机验证工具有LibreSSL提示，服务运行不依赖该工具 |
| Android发布 | UIFRAME_MEDIA_ANDROID_BUILD_PASSED | unity-android.log；APK：`/tmp/uiframe-media-plan-android/Build/MediaSmoke.apk` |
| iOS发布 | UIFRAME_MEDIA_IOS_EXPORT_PASSED、BUILD SUCCEEDED | unity-ios.log、xcode.log；工程：`/tmp/uiframe-media-plan-ios/Build/iOS` |

首次交付目录的 `unity-final.xml`、`unity-r5.xml` 和前次复审目录的 `unity.xml` 均为历史证据；当前整合依据为最终修复目录的 `unity-final.xml`。本次修改了生产仓库与平台执行代码，已重新构建四平台库和移动发布产物。ASan只覆盖原生契约，不代表Unity与手机全部内存路径运行过ASan。

## 4. B01–B24对应结果

“宿主通过”只说明实际共享仓库、C# / JNI和本机HTTP的已测行为，不能替代手机系统测试。

| ID | 证据与边界 |
| --- | --- |
| B01 | 原生test_batch_32_reordered_response_and_shared_receipts，32项乱序关联与回执通过 |
| B02 | Unity SourceFailureIsIsolatedAndPartialResultSurvivesQuery及服务逐项拒绝，失败与受理成员均可查询 |
| B03 | 原生缺项 / 重复 / 账号 / 嵌套哈希及服务结构 / 上限测试；全批先校验，无假成功 |
| B04 | 服务requestId参数与操作绑定、Unity持久操作身份；参数改变明确冲突 |
| B05 | 服务去重 / 账号隔离及1000对象基准；已核验副本零照片字节、来源独立 |
| B06 | Unity离线受理、容量等待与暂停退出、原生聚合尾批通过；手机断网调度未测 |
| B07 | 真实进程Prepare / Seal / Accept / Claim / Handoff中断，保留逐项归属 |
| B08 | 48窗口覆盖控制创建 / 落盘 / 系统绑定 / 开始 / 应用 / 释放与照片阶段；不是OS真机终止实验 |
| B09 | HTTP空204、C#独立origin、PUT后仅待确认通过；200 / 201 / 204接受集合统一，未分别跑手机HTTP栈 |
| B10 | 服务独立确认、字节到达后kill / 重启确认，无客户端commit |
| B11 | 损坏内容、长度不符、重复PUT、发布后写库前中断与持久核验恢复；云通知未接入 |
| B12 | 取消先赢 / 确认先赢 / 取消先于Plan / 重复签名PUT、原生代次与Cancel失败不重发 |
| B13 | 丢失Plan / PUT响应显式核对、Absent不等于取消、取消墓碑与实际释放分离 |
| B14 | 授权到期、凭据隔离、开发HTTP地址校验；Desktop/Android禁跳转，iOS最终URL/可用metrics检查已编译并验证纯策略函数。iOS不能前置拦截后台跳转，服务/网关须提供可信最终终点。真实TLS / 重定向 / 锁屏凭据仍需环境验证 |
| B15 | Unity权限缩小、版本、账号隔离、暂停与Shutdown原异常 / 释放通过；系统授权与锁屏另需真机 |
| B16 | Unity照片 / 控制文件清理故障、预算预留、原生持久归属及未知提交规则；实际磁盘满 / 掉电未测 |
| B17 | 生产Java/JNI的调度、运行中追加受理、停止拒绝与清理宿主测试通过；完整Java HTTP链路及手机无C#、锁屏、系统回收、force-stop未验收 |
| B18 | iOS恢复 / completion handler / 文件保护实现并链接；系统唤醒、首次解锁前、用户划掉未验收 |
| B19 | 万 / 十万 / 百万历史、同样1个活动尝试，有界活动索引查询通过 |
| B20 | 24小时混合负载未执行，不从短测试推断长期无泄漏 |
| B21 | ThirtyTwoPhotosKeepProgressWithOneSlowAndOneFailedUpload：一慢一失败，其余30项继续确认；慢项独立取消，失败不自动重传。若两项都占满2槽，仍需等待完成 / 截止，不突破并发预算 |
| B22 | test_continuous_registration_does_not_starve_due_confirmation：持续新Plan压力下到期Query逐轮推进 |
| B23 | 原生Pending时点、24小时/256次边界与Unity容量等待通过；超预算不提前查询，取消后实际释放前不删文件；实机空间压力与系统延期组合未测 |
| B24 | 首次Query聚合、未来Query不挡Plan、暂停保留时点/截止；已绑定Query截止纳入唤醒，Java/C#所有权回归通过。iOS earliestBeginDate已链接，系统真实延后未测 |

## 5. 性能与资源

首次交付库 `a21bb7af979d1f309d657aa09927791765a07d6d78c6991985b6b22fd662482a` 运行protocol_benchmark.py：1000个64KiB合成对象，真实仓库ABI与真实服务Store，进程内顺序I/O。这项吞吐基准本次未重测，保留为该版本证据。它验证请求数量、去重字节和窗口，**不是1000张实际手机照片或真实网络吞吐测试**。

| 已核验比例 | Plan | Query | PUT | 照片上传字节 | 最大暂存项 | 耗时 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 0% | 32 | 32 | 1000 | 65,536,000 | 32 | 72.76s |
| 50% | 32 | 17 | 500 | 32,768,000 | 32 | 59.40s |
| 100% | 32 | 0 | 0 | 0 | 32 | 39.45s |

Plan平均31.25项；夹具每批上传后推进服务核验，不模拟系统并发。RSS高水位约34.44 / 35.5 / 36.45MiB，包含Python、服务和原生库，后续场景继承同进程高水位，不能当Unity堆或各阶段独立峰值。64KiB照片不证明接近512MiB上限的设备峰值。

最终修复库 `cfb8809e...` 重新运行scheduler_benchmark.py，保持1个活动尾部尝试：

| 历史条数 | Attempts p50 | ProtocolWake p50 |
| --- | ---: | ---: |
| 10,000 | 0.201ms | 0.090ms |
| 100,000 | 0.226ms | 0.087ms |
| 1,000,000 | 0.106ms | 0.060ms |

查询从attempts_unreleased活动索引开始，再按主键关联；Wake包含controls_active中已绑定Query的截止，排序覆盖活动集合，不物化百万历史。调度证据为最终修复目录的scheduler.json，吞吐证据仍为首次交付目录的protocol-benchmark-final.json。测试和构建有并行执行时段，耗时不是无干扰吞吐基线。只证明本夹具活动成本没有随总历史线性增长，未测手机CPU、GC、WAL峰值、耗电或后台完成率。

P0旧服务25项测试耗时13.063s，与新服务32项内容不同，不能比较性能。缺少同照片集 / 网络 / 设备的旧版基线；250ms / 1s / 64项 / 并发2保持有限默认值，不补造提升倍数，留待设备实测调参。

## 6. 可复现验证

在包根执行，替换尖括号中的绝对路径，均使用隔离数据目录：

```sh
python3 -m unittest discover -s Tools~/BackupServer -p test_server.py -v
python3 Tools~/BackupServer/validate_contract.py
python3 Runtime/MediaBackup/Native~/tests/protocol_tests.py --core <core> --repository <backup> -v
python3 Runtime/MediaBackup/Native~/tests/process_windows.py --core <core> --repository <backup>
python3 Runtime/MediaBackup/Native~/tests/reconciliation_lifecycle.py --core <core> --repository <backup>
python3 Runtime/MediaBackup/Native~/tests/scope_reconciliation.py --core <core> --repository <backup>
python3 Tests/Native~/android_backup_binding.py --jdk <jdk-home> --android-jar <android.jar> --core <macOS-core> --repository <macOS-backup>
python3 Tests/Native~/ios_backup_policy.py
python3 Runtime/MediaBackup/Native~/tests/protocol_benchmark.py --core <core> --repository <backup> --photos 1000 --output <protocol.json>
python3 Runtime/MediaBackup/Native~/tests/scheduler_benchmark.py --core <core> --repository <backup> --rows 10000 100000 1000000 --output <scheduler.json>
```

OpenAPI依赖只安装到独立venv：`pip install -r Tools~/BackupServer/requirements-validation.txt`，参考服务运行只用Python标准库。

```sh
python3 Tests/Native~/prepare_media_validation.py --project <isolated-project> --host-project <Unity-project>
# 另一个终端保持服务运行
python3 Tools~/BackupServer/integration_server.py
# macOS保留图形设备以运行纹理 / 方向回归
<Unity> -batchmode -projectPath <isolated-project> -runTests -testPlatform EditMode -testFilter UIFrame.Regression -testResults <results.xml> -logFile <tests.log>
```

Android目标另运行MediaTests.AndroidBuildPermissionConfigurationIsOptInAndIdempotent。Windows控制清理夹具依赖POSIX打开文件unlink，明确跳过；不把跳过算通过。

四平台先取得对应ABI3 SQLite核心，再运行Native~/build/build.py，指定 `--target macos|android|ios|windows-x64`、`--core-build`、`--cmake`及NDK / LLVM-MinGW；然后 `install.py <build-directory>` 校验安装。ASan用独立输出和 `--sanitize`，不安装调试库。

```sh
<Unity> -batchmode -nographics -quit -projectPath <android-project> -buildTarget Android -executeMethod MediaBuildSmoke.Android -sqliteSdk <sdk> -sqliteNdk <ndk> -sqliteJdk <jdk> -logFile <android.log>
<Unity> -batchmode -nographics -quit -projectPath <ios-project> -buildTarget iOS -executeMethod MediaBuildSmoke.Ios -logFile <ios.log>
# 在导出Build/iOS目录执行
xcodebuild -project Unity-iPhone.xcodeproj -scheme Unity-iPhone -configuration Release -sdk iphoneos -destination 'generic/platform=iOS' CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO build
```

## 7. 设备与生产剩余验收步骤

下列操作尚未执行，不能当已取得的结果：

1. 按[参考服务README](../Tools~/BackupServer/README.md)初始化独立v2目录，业务及上传origin都用设备可达地址。测试令牌放工作区外。开发HTTP需客户端显式启用，并给测试App配置Android cleartext或iOS ATS及本地网络用途说明；正式App用HTTPS，不默认扩大网络许可。
2. 私有BackupSmokeConfig.json包含serverUrl、account、token、allowDevelopmentHttp、wifiOnly。用 `prepare_media_validation.py --backup-smoke-config /absolute/private/BackupSmokeConfig.json` 拷贝到隔离测试工程Resources再构建。文件会嵌入测试包，只用可撤销测试账号，不提交Git或用于生产。不传该参数会移除隔离工程旧配置。
3. Android安装MediaSmoke.apk，取得UIFRAME_MEDIA_DEVICE_ACCEPTED任务ID及UIFRAME_MEDIA_DEVICE_CONFIRMED_SMOKE_PASSED。未配服务只输出ADMISSION_SMOKE_PASSED，不代表网络备份。iOS需在Xcode开发签名后运行；无签名产物不能安装手机。
4. MediaSmoke仅验证1个小文件。后台矩阵使用实际32 / 1000张照片与SubmitAsync，保存operationId，记录NativeAccepted / SystemScheduled、切后台和服务确认时间；仅已交接照片计入后台完成率，同时报告未准备数。
5. Android分别运行Automatic及可见前台用户主动UserInitiated：Home切出、锁屏、网络切换、系统回收、Job停止、用户force-stop和重开。`adb shell am force-stop com.zzq.mediavalidation` 只代表用户强制停止，不能冒充普通回收；记录stopReason、通知暂停、重开结果与文件归属。
6. iOS分别测系统唤醒、锁屏、重启首次解锁前、系统终止、用户划掉与重开；调试器停止不能替代系统终止。核对session回调、completion handler、Keychain、earliestBeginDate和独立服务确认；划掉后不承诺继续。
7. 24小时混合提交、取消、显式核对、清理及网络切换，采集RSS、GC、原生堆、纹理、暂存 / 控制 / WAL / 凭据数及耗电。包含0 / 50 / 100%已核验、接近单文件上限和双慢传；保留最小身份历史不等于资源泄漏。
8. Windows Editor运行原生 / Media回归及MediaBuildSmoke.Windows，取得DLL加载与IL2CPP实际运行结果。生产另验真实认证、HTTPS、对象存储、通知幂等及遗漏核对、配额、监控、灾备与容量，本机Store不替代这些门槛。

未执行生产部署、Git提交或旧数据自动清除。主Editor若已加载旧业务库，需要正常关闭再打开工程才会加载新版原生库；本轮未重启用户工程。
