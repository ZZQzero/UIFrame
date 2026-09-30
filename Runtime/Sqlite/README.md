# Sqlite 通用本地存储模块

位于 `Runtime/Sqlite`，程序集与命名空间为 `UIFrame.Sqlite`。不依赖 Unity、UniTask、UIFrame.Runtime 或照片模块。使用官方 SQLite 3.53.4、一份共享 C++ 执行核心和 C ABI 3；数据库实际工作在原生线程池中执行。

目前已实现通用核心、托管接口和桌面行为验证。已构建 macOS universal、Android ARM64、iOS ARM64 和 Windows x86_64 原生产物；Windows DLL 已通过交叉编译、架构、接口导出与依赖检查，Windows Editor / Player 的实际运行尚待验证。移动端已有独立 IL2CPP 构建记录，真机后台运行及商业性能门槛尚未验收；iOS Simulator 未提供已验收产物。照片备份尚未切换到本模块，新增照片 schema 目前用于独立验证。完整状态见 [Validation.md](Validation.md) 和 [实施计划](ImplementationPlan.md)。

## 使用

调用方提供绝对路径、创建父目录并明确选择打开模式；打开既有库不会创建空库，创建新库不会覆盖已有文件。

```csharp
using UIFrame.Sqlite;

var db = await SqliteDatabase.OpenAsync(new SqliteOpenOptions(
    absolutePath, SqliteOpenMode.CreateNew));
try
{
    await db.ExecuteAsync(new SqliteCommand(
        "CREATE TABLE saves(slot TEXT PRIMARY KEY, payload BLOB NOT NULL) STRICT"));
    await db.ExecuteAsync(new SqliteCommand(
        "INSERT INTO saves VALUES(?,?)", "slot-1", bytes));
    var page = await db.QueryPageAsync(
        new SqliteCommand("SELECT slot FROM saves ORDER BY slot LIMIT ?", 100),
        new SqliteQueryBudget(100, 128 * 1024), row => row.GetString(0));
}
finally { await db.CloseAsync(); }
```

应用最终退出前释放所有批次结果并调用 `await SqliteRuntime.ShutdownAsync()`。该调用结束当前进程中的托管模块寿命，不是用于每次切换界面的重置接口。关闭与查询映射都不访问 Unity；调用方在使用 Unity 对象之前切回主线程。Editor 自动在域重载、退出播放模式和退出时清理，重新进入编辑模式开启新的编辑器寿命。

业务自己定义 application_id、schema、账号归属、参数校验、索引和操作回执。参考 [游戏存档与背包示例](Samples~/GameRepositories.cs)：示例包含原子变更、余额约束及独立操作回执，重复 OperationId 会导致整个事务失败；调用方可查询此前回执核对丢失的提交结果，不会自动重放。

## 接口与所有权

| 接口 | 结果与释放 |
| --- | --- |
| `SqliteDatabase.OpenAsync` | CreateNew / OpenExistingReadWrite / OpenExistingReadOnly；一个文件同一核心只允许一个上下文所有者 |
| `ExecuteAsync` | 单条不返回行的 SQL；成功返回直接受影响行数 |
| `QueryPageAsync` | 一条只读 SQL；映射完成即释放原生页，返回的集合由调用方持有 |
| `ExecuteTransactionAsync` | 有序 `SqliteBatch`，提交后交付 `SqliteBatchResult`；必须 Dispose，可以在数据库关闭后继续读取 |
| `CreateSnapshotAsync` | 固定来源快照、分步复制、校验并发布到新的目标路径；已有目标不覆盖 |
| `GetStorageInfoAsync` | 主库、WAL 和可用磁盘字节；并发写入后数字可以变化 |
| `CheckpointAsync` | Passive 或 Truncate；只尝试一次，读取者阻止回收时 `CheckpointBlocked=true`，不等待或重试 |
| `GetDiagnostics` / `SqliteRuntime.GetDiagnostics` | **模块全局**连接、排队 / 执行数量、参数 / 结果缓冲、等待 / 执行 / 提交耗时、缓存命中、错误 / 取消 / 超时计数和引擎内存 |
| `CloseAsync` | 停止新请求，等已受理工作及映射结束，释放连接；重复调用等待同一结果 |

`SqliteCommand.ExpectAffectedRows(n)` 作为事务条件；不匹配时回滚整批。参数支持 null、string、byte[]、有符号64位范围的整数、bool、有限浮点数。SQL 一条命令一条语句，参数与 SQL 分开；禁止外部 BEGIN / COMMIT / ATTACH 和核心配置 PRAGMA。业务 application_id / user_version 可读写。

行对象仅在 `Func<SqliteRow,T>` 内有效，映射后不可保留行对象；需要的数据应在回调中复制。映射运行在受限的后台并发槽位，须是短小的纯映射，不能访问 Unity 或同步等待嵌套数据库工作。映射异常保留原异常，不能撤销此前已提交的写入。

## 预算和持久化

- 固定2个原生工作线程，每库一个写连接和一个只读连接；写事务按正式受理顺序串行，资源预留不决定执行次序。
- 每库最多128个未释放请求；参数双份预留合计4 MiB，结果额度4 MiB；全模块原生与托管编码缓冲预留合计16 MiB。慢消费者持有的批次结果也占额度。
- 一批最多200条SQL，结果合计最多200行 / 1 MiB；超限失败，**不截断结果**。单条SQL仍可能修改大量行，业务必须自行分批。
- 请求默认截止时间5秒；ExecuteAsync 可通过 TimeSpan 重载设置本次截止时间，查询 / 批次使用 SqliteQueryBudget。不可中断文件系统 I/O 不保证立即返回。关闭不接受取消。原生关闭入口每库只允许一个未提交预留，且参数与结果预算必须为零，避免借清理通道绕过资源限额。
- 每连接64条语句缓存，单缓存语句内存小于64 KiB；SQLite页缓存目标4 MiB。引擎临时数据、托管物化结果和调用方对象不等于模块缓冲预算。
- 参数在受理期间复制；受理期间不能修改 byte[]。原生额度先预留再编码。业务长期保存的原始数组仍由业务拥有。
- WAL + synchronous=FULL；同时开启 fullfsync / checkpoint_fullfsync，由目标 VFS 执行相应刷新。没有关闭可靠落盘以换取性能。
- `SqliteOpenOptions` 默认写入前保留32 MiB可用磁盘、WAL高水位128 MiB。可显式配置；检查在写事务开始前执行，不预测任意SQL的全部未来磁盘增长，也不能代替实际磁盘满错误。高水位阻止写入，查询、维护、关闭仍可执行。释放空间或checkpoint后可显式提交新工作；不会重放原失败操作。
- 自动checkpoint采用SQLite的1000页阈值。显式Truncate适合空闲窗口，不在写事务中执行VACUUM；本版没有自动压缩。

成功以COMMIT完成为准。提交后迟到取消不改写成功；不确定提交通过 `SqliteException.CommitOutcomeUnknown` 表达，故障库停止后续工作并仍允许关闭。业务通过同事务内的OperationId回执核对。次级清理错误保留在 `CleanupCode` 或 `SqliteRuntime.LastCleanupError`，不替换已有主异常。没有自动重试、损坏重建、JSON兼容、导入或双写。SqliteException.Phase 标识主错误所处阶段。

完成分发故障会停止该托管客户端：由原生核心结束在途工作并关闭所属数据库，未交付等待保留同一主异常，已交付批次结果保持有效。CloseAsync / ShutdownAsync 在资源收尾后仍传播故障，不能把清理完成当成业务成功。其他原生客户端不受影响。Editor 域收尾同时回收弱引用已清除而终结器尚未执行的结果，延后的终结器不会重复释放。只有在旧客户端确已释放后才允许开始新寿命，历史异常仍保留为可观察的错误。

诊断按需读取，不自动轮询或输出逐条成功日志。累计耗时以纳秒计，不能直接当作 p95 / p99；结果配额、已完成结果实际字节和引擎内存分别报告。完整口径见 ABI 文档；WAL 与 checkpoint 进度通过 GetStorageInfoAsync / CheckpointAsync 查询。

快照默认临时文件512 MiB、来源WAL128 MiB、30秒期限，全模块同时一个。使用独立只读来源连接，每次复制128页并让出工作线程。仅成功发布完整、可独立打开的文件后返回成功；发布之后刷盘失败时可以存在目标，此时异常的 `HasCommittedChanges` 为 true，调用方不得把该文件当成已确认成功。失败仅清理本次临时文件。

## 构建与验证

源码、SQLite锁文件、稳定ABI和测试放在 `Native~`。`Plugins` 只接收有可核对来源的产物，构建前校验产物和核心源码哈希。SDK / NDK / CMake 路径由使用方明确提供；脚本不会下载工具链。

```sh
python3 Native~/build/build.py --cmake /absolute/path/to/cmake \
  --target macos --output /absolute/build/macos
python3 Native~/build/install.py /absolute/build/macos
```

Android 使用 `--target android --ndk /absolute/path/to/ndk`；iOS设备使用 `--target ios`。构建包含源校验、编译、host原生测试及构建清单。macOS还生成dSYM；发布存档应保留完整构建目录和清单。Android库支持16 KiB页对齐、私有静态C++运行库，无额外libc++_shared部署依赖。iOS只链接同一份核心静态库，SQLite符号统一改名为ufengine前缀，禁止再嵌入第二份本模块引擎。

替换原生库使用临时文件原子发布；**更新后重启Unity**。不能覆盖编辑器已映射的dylib文件内容，也不能以脚本域重载替代原生库重载。

### Windows x86_64

产物位于 `Plugins/Windows/x86_64/uiframe_sqlite.dll`，供 Windows x64 Editor 和 x64 Player 使用，不启用 Win32 或 ARM64。使用与其他平台相同的 SQLite 3.53.4 源码和共享核心，不需要另外放入官方 `sqlite3.dll`。当前 DLL 面向 Windows 10/11（同时遵守宿主 Unity 的最低系统要求），C++运行库静态链接，仅依赖 Windows 系统 DLL。

Windows 本机构建需要 Python 3、CMake 3.22+、Visual Studio 2022 的“使用 C++ 的桌面开发”和 Windows SDK。在本目录运行：

```powershell
py .\Native~\build\build.py --cmake "C:\Program Files\CMake\bin\cmake.exe" --target windows-x64 --output C:\Build\UIFrameSqlite\windows-x64
py .\Native~\build\install.py C:\Build\UIFrameSqlite\windows-x64
```

构建脚本在 Windows 上执行原生事务、故障注入、中文路径与只读文件、DLL卸载等测试，测试失败不发布新清单。安装前关闭使用旧 DLL 的 Unity；Windows 不允许替换已加载的 DLL，安装脚本不会绕过文件锁。DLL与来源清单一同提交，PDB保留在构建目录用于定位崩溃，不作为Unity插件导入。

在 macOS/Linux 上可显式提供 LLVM-MinGW 交叉工具链：

```sh
python3 Native~/build/build.py --cmake /absolute/path/to/cmake \
  --target windows-x64 --llvm-mingw /absolute/path/to/llvm-mingw \
  --output /absolute/build/windows-x64
python3 Native~/build/install.py /absolute/build/windows-x64
```

交叉构建只编译 Windows 测试，不执行它们；清单的 `native_tests` 明确记录为 `built-not-run`。`verify_windows.py`检查x64架构、完整C ABI以及系统库依赖，不代表运行验收。C ABI路径和错误消息固定使用UTF-8，Windows文件访问使用宽字符路径；所有客户端释放后工作线程停止，重新打开客户端启动新的工作线程。原生宿主必须释放数据库、结果和客户端之后才卸载DLL。

离线验证入口：

```sh
python3 Native~/tests/process_recovery.py /absolute/build/macos/libuiframe_sqlite.dylib
python3 Native~/tests/schema_contract.py /absolute/build/macos/libuiframe_sqlite.dylib
python3 Native~/tests/benchmark.py /absolute/build/macos/libuiframe_sqlite.dylib
python3 Native~/tests/validate_managed.py \
  --mono-bin /absolute/path/to/MonoBleedingEdge/bin \
  --nunit /absolute/path/to/nunit.framework.dll \
  --native-directory /absolute/build/macos
```

Unity Test Runner选择 `UIFrame.Sqlite.Tests.Editor`。内存检查另用 `build.py --target macos --sanitize`，该产物不能安装到Unity。详见 [验证记录](Validation.md)、[ABI数据格式](Native~/include/WireFormat.md) 和 [第三方声明](ThirdPartyNotices.md)。

独立工程 IL2CPP 验证（不会把测试脚本加入业务工程）：

```sh
python3 Native~/tests/prepare_unity_project.py \
  --project /absolute/new-smoke-project --unity-version 6000.3.19f1
/absolute/Unity -batchmode -nographics -quit \
  -projectPath /absolute/new-smoke-project -buildTarget Android \
  -executeMethod BuildSmoke.Android -sqliteSdk /absolute/sdk \
  -sqliteNdk /absolute/ndk -sqliteJdk /absolute/jdk -logFile /absolute/android.log
```

iOS使用相同准备步骤及 `-buildTarget iOS -executeMethod BuildSmoke.Ios`；导出位于测试工程的 `Build/iOS`。可用Xcode在关闭签名的情况下验证编译链接，真机运行则需要应用签名和设备。Android产物为 `Build/SqliteSmoke.apk`。两者均使用IL2CPP、High裁剪和Release构建；设备运行成功后日志包含 `UIFRAME_SQLITE_DEVICE_SMOKE_PASSED` 与构建ID。该测试不会申请照片权限或访问用户相册。

Windows需在装有 Windows IL2CPP 支持的 Windows Editor 中运行同一个独立工程：

```powershell
py .\Native~\tests\prepare_unity_project.py --project C:\Build\SqliteSmoke --unity-version 6000.3.19f1
& "C:\Program Files\Unity\Hub\Editor\6000.3.19f1\Editor\Unity.exe" -batchmode -nographics -quit -projectPath C:\Build\SqliteSmoke -buildTarget Win64 -executeMethod BuildSmoke.Windows -logFile C:\Build\windows-build.log
& "C:\Build\SqliteSmoke\Build\Windows\SqliteSmoke.exe" -batchmode -nographics -logFile C:\Build\windows-player.log
```

成功需同时满足构建成功、Player退出码为0、Player日志出现上述成功标记及当前构建ID；单有EXE或退出码不足以证明验证通过。Windows Editor 中还需运行 `UIFrame.Sqlite.Tests.Editor`，覆盖托管映射与异常传播。独立工程没有测试程序集，Editor测试在包含本包测试程序集的宿主工程执行。
