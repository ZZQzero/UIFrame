# 图片与备份实现验证记录

日期：2026-09-29。此记录区分已经执行的检查与完整方案的未完成部分，不构成全平台兼容承诺。

## 工具链与宿主

- Unity 6000.3.19f1，macOS ARM64。
- 宿主 Android 最低 API 25，自动选择目标 SDK；导出 Gradle 工程使用 compileSdk 37。
- Android Java 源码独立验证使用 Android SDK 36、JDK 25 的 Java 8 源码 / 字节码目标；Unity Android 构建链使用 JDK 17、Gradle 9.1。
- Xcode 27.0，iPhoneOS 27.0 SDK；原生源码按 arm64 / iOS 15 目标检查，宿主最低 iOS 版本为 15。
- Python 3.9.6，仅标准库。
- Android 设备列表为空；未执行 Android / iOS 真机交互验收。

## 已执行的检查

| 检查 | 结果与证据 |
| --- | --- |
| Unity C# 编译 | Editor、Android 和 iOS 平台切换后无新增编译错误 |
| Java 原生编译 | `GalleryBridge.java`、`GalleryActivity.java` 用 Android SDK 编译通过 |
| iOS 原生源码 | Clang Objective-C++ / ARC 语法检查通过；有一处兼容无 UIScene 宿主所用 windows API 的弃用警告 |
| Unity 原生导入设置 | `.mm` 仅 iOS；`.androidlib` 仅 Android；均不作为 Editor 原生插件加载 |
| iOS 工程导出 | 成功，原生源码及 Photos、PhotosUI、ImageIO、UniformTypeIdentifiers、Network 框架已逐项检查 |
| Android Gradle 工程导出 | 成功；已比较导出 Java 与包内 Java 一致，位置为 `src/main/java`，并检查 JNI keep 规则 |
| 服务端回归 | 8 项通过；含真实本机 HTTP、断点、幂等、账号隔离、校验失败、内容去重和存储重开 |
| Unity 初始集成回归 | 12 项全部通过，包含真实服务上传并下载逐字节验证 |
| Unity 最终扩展回归 | 14 项全部通过、零跳过；显式选中真实 HTTP 用例一并执行，包含 Android 权限配置开关与重复执行检查；job `7cc0cf96079e4d26a51697eb03778bfa` |

导出工程位于宿主 `Builds/MediaValidation-iOS` 和 `Builds/MediaValidation-AndroidProject`，不作为包源代码提交。构建 job：iOS `build-0684789a51`，Android 工程 `build-31da08afe4`。

首次 Android 完整 APK 构建进入 Gradle 阶段后长期未返回，已停止该次构建子进程并恢复 Unity；没有把它记为通过。iOS 的成功是 Xcode 工程导出，不代表已完成签名、完整 Xcode 编译或设备安装。

## 已落实的功能

- 系统单选 / 多选、原生窗口忙碌语义与取消，选图后暂存文件。
- 图片纹理、选择集合、独立导出文件的所有权与释放。
- 普通目录枚举与分页快照；移动端授权目录句柄与枚举。
- 照片库授权查询 / 请求、相册与图片元数据快照。
- 持久备份批次、文件预算、账号绑定、分片上传、服务端确认、去重和下载校验。
- 单向备份，不自动删除手机原图或服务器已提交副本。
- 应用运行期间的自动发现、首次历史基线、Wi-Fi 条件检查和显式有限重试。
- Editor 调试窗口、UGUI 示例、可独立启动的本机参考服务器。

## 仍未完成的完整方案项

1. **后台发现新照片**：仍未接入系统照片变化触发的后台扫描，`SupportsBackgroundDiscovery=false`。本阶段已增加原生后台传输源码，真机行为需按下方矩阵验证。
2. **图库变化通知与缓存**：当前是显式元数据快照刷新，尚未实现原生变更通知、游标代次失效事件和共享有界缩略图缓存。
3. **授权变化联动**：权限请求与读取失败可观察；范围缩小后的队列自动暂停和重新确认仍需要业务接入，当前自动循环遇到来源权限错误会结束。
4. **完整移动端验证**：系统窗口、部分授权、云端照片、设备内存峰值、Activity 重建、目录授权重启恢复、相册大数据量和后台限制均未获真机验证。
5. **发布构建**：Android 完整 APK、混淆发布运行和 iOS 完整 Xcode 链接 / 签名 / 安装尚待完成。
6. **服务端生产化**：当前服务为单进程本机参考实现；生产认证、配额、过期会话回收、部署与容灾仍需项目方案。

因此当前交付包含基础图片能力、前台备份及可选原生后台传输源码，不是实施计划 P0–P4、B0–B6 全项验收完成。实际接入见 [Gallery.md](Gallery.md)，完整目标见 [GalleryImplementationPlan.md](GalleryImplementationPlan.md)。

## 原生后台传输增量验证（2026-09-29）

- Android 新增 `BackupBridge.java` / `BackupJobService.java`，独立 Java 编译通过；使用系统 JobScheduler、持久调度、网络条件和 AndroidKeyStore，无 AndroidX 依赖。
- iOS 新增 `UIFrameBackup.mm`，按 arm64 / iOS 15、ARC 编译检查通过；通过 Unity 官方 AppDelegateListener 接收后台 URLSession 事件，使用 Keychain 保存提交凭据。
- 本机服务回归从 8 项扩展为 **12 项，全部通过**：增加大于 4 MiB 的完整上传、重复提交与下载、错误账号 / 长度 / SHA、上传中断保留原有分片，以及慢速上传期间登记下一任务 / 放弃会话后禁止迟到提交。测试使用真实本机 HTTP。
- Unity 回归扩展为 **20 项，全部通过**（Android 活跃目标）：含重新打开时原生文件不被清理、取消等待文件释放、暂停期间禁止恢复、交接意图中断恢复、提交前持久标记与不重复提交、显式重试；job `c80488f853924207af9f992feefd8400`。原生生命周期测试使用可控桥接替身验证 C# 所有权，不能替代系统后台执行测试。
- 最终 Android Gradle 工程导出成功（`build-3d3683c9fb`），源码与 Manifest 逐字节一致，后台服务权限和 JNI keep 规则已检查。最终 iOS 工程导出成功（`build-ed428f8b18`），源码逐字节一致，Security 框架、ARC、编译 Sources 条目及 Unity 回调头路径已检查；导出后的 `.mm` 再次通过 Clang 检查。
- 验证完成后已恢复 StandaloneOSX 活跃目标及原 Android 导出开关，临时集成服务已停止。
- 真实原生系统调度、Keychain / Keystore 运行、后台网络流量和锁屏回调尚未在设备上验证。未声称 APK 安装或 Xcode 完整链接通过。

### 真机验收步骤

1. 手机与电脑网络互通，启动参考服务并配置局域网地址、开发 HTTP 或 HTTPS；iOS 配置局域网访问说明。运行移动端 GalleryDemo，填写运行时凭据。
2. 选取多张图片（含大文件），点击备份，确认显示提交状态。切换到其他应用 / 锁屏；检查服务器已提交列表。回到应用刷新，已完成文件必须能下载并核对 SHA-256。
3. 在只允许非计费网络时切换 Wi-Fi / 蜂窝，确认平台条件有效。iOS 允许非计费有线网络，与 Android 的严格 Wi-Fi 条件不同。
4. 上传中暂停、恢复、取消；确认释放前暂存文件仍在，恢复不会产生两条同任务上传，取消不删除服务器已有副本。
5. 分别测试系统回收、Android 强制停止 / 重启、iOS 用户划掉应用，再次打开并 ProcessAsync。记录实际调度延迟，不要求强制退出后继续上传。
6. 令牌失效应 NeedsAttention；更新令牌后 Retry / ProcessAsync。退出账号先 PauseAsync 等待完成，再关闭服务；切换账号使用独立目录。
7. 测试服务器中断、错误校验、暂存文件删除、原生记录损坏。不得显示假完成；错误必须可见，普通失败不静默业务重试。

