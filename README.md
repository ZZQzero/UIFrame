# UIFrame

基于 URP、UGUI、YooAsset 和 UniTask 的轻量 Unity UI 框架，提供面板生命周期、层级栈、缓存、异步加载、URP Camera Stack、屏幕方向管理、SafeArea 和编辑器绑定代码生成。

## 环境要求

- Unity 6000.0 或更高版本
- UniTask 2.5.10 或更高版本
- YooAsset 3.0.5 或更高版本
- UGUI 2.0.0 或更高版本
- Universal RP 17.3.0 或更高版本
- Input System 1.19.0 或更高版本，Unity Audio 内置模块（均已声明包依赖）

## 安装

通过 Unity Package Manager 的 **Add package from git URL** 添加：

```text
https://github.com/ZZQzero/UIFrame.git
```

如果项目尚未安装 UniTask 和 YooAsset，请先在 `Packages/manifest.json` 中配置 OpenUPM：

```json
{
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": [
        "com.cysharp",
        "com.tuyoogame"
      ]
    }
  ],
  "dependencies": {
    "com.cysharp.unitask": "2.5.10",
    "com.tuyoogame.yooasset": "3.0.5",
    "com.zzq.uiframe": "https://github.com/ZZQzero/UIFrame.git"
  }
}
```

各系统详细用法与注意点见 [USAGE.md](Docs/USAGE.md)。场景见 [Scene.md](Docs/Scene.md)，红点见 [RedDot.md](Docs/RedDot.md)，对象池见 [Pool.md](Docs/Pool.md)。Timer / 音频 / 输入 / 事件 / FSM 见 `Docs/` 下对应文档。

## 快速开始

```csharp
using System;
using Cysharp.Threading.Tasks;
using UIFrame;
using YooAsset;

public sealed class MainPanel : UIPanel<UINone>
{
    protected override void OnOpen(UINone args)
    {
    }
}

public static class UIStartup
{
    public static async UniTask StartAsync(ResourcePackage package)
    {
        UI.Init(package);
        UI.ConfigureURPCameraStack();

        UI.Register<MainPanel>("MainPanel");
        await UI.Push<MainPanel>();
    }
}
```

不再使用时调用（若还用了对象池，先 UI 再释池）：

```csharp
UI.Shutdown();
GamePool.Shutdown();
```

Hud / Push / Popup / Tips / Guide 按面板 **Type** 去重。同一类型正在加载时再次 Open，不会发起第二次加载，而是合并进这次请求：后一次的 `Args` 和 `Mode` 覆盖前一次；已取消请求保持取消，须等待旧请求结束后再重试。两次 `await` 拿到同一块面板，参数以最后一次为准。已经打开的同类型会走已有实例（`ApplyArgs`），不会再加载。**Toast** 是多实例通道，不走这条合并规则。

主线程检查和托管池重复归还检查由 `UIFrameSafety` 控制。默认 Editor / Development 打开，Release 关闭。QA 要在正式包里抓线程错误时，在 `Init`、建池、调用红点之前设 `UIFrameSafety.ThreadChecks = true`。`CollectionChecks` 只作用于之后新建的托管池。

## URP Camera Stack

`UI.Init()` 后 Canvas 默认为 `Screen Space Camera`，并绑定常驻 UI Camera；同时开启 `Vertex Color Always In Gamma Color Space`。此时 UI Camera 可能是孤立 Overlay，界面不可见，需要配置 Stack：

```csharp
UI.ConfigureURPCameraStack(); // Base = Camera.main；失败会抛
```

也可指定 Base / 外部 UI Camera：

```csharp
UI.ConfigureURPCameraStack(baseCamera, existingUICamera);
```

行为约定：

- 只把 UI Camera 设为 `Overlay` 并加入 Base Camera Stack
- **不改动** Base Camera 的 cullingMask、renderType 等
- `Disable` 只从 Stack 移除 UI Camera；Canvas 仍保持 `Screen Space Camera` 与 UI Camera 引用
- UI Camera 创建后常驻，不销毁

```csharp
UI.DisableURPCameraStack();
```

## SafeArea

层和面板根保持铺满；把 `SafeAreaFitter` 挂在**内容节点**上（顶栏、按钮、列表），不要挂在 Layer、全屏背景或 Mask/Guide 上。

```csharp
// 原生壳已把 Unity 视图放在顶底栏之间时，关掉 Top/Bottom，避免扣两次
fitter.SetPads(left: true, right: true, bottom: false, top: false);
```

Editor 可用 Device Simulator，或 `ScreenSafeArea.SetOverride(rect)` 模拟。只认 Unity 窗口里的 `Screen.safeArea`，不要用原生 dp/pt 再对一套。

`ScreenSafeArea.Current` 只读缓存。Root 在转屏、Canvas 尺寸变化、重新获得焦点时 `Refresh`，并再连刷两帧以等待 `safeArea` 晚到。

## Tips / Toast / Guide

| API | 层 | 语义 |
|-----|----|------|
| `UI.Tips` | Tips | 同类型单实例，不排队、不定时 |
| `UI.Toast` | Tips | 多实例 + 队列 + 可选自动关闭 |
| `UI.Guide` | Guide | 最上层引导，不进窗口/弹窗栈 |

`UI.Toast<TPanel>` 仍是 Tips 层上的 `UIPanel`，同类型可以同时有多份。时长、并发上限和等待队列在 `UIManager` 里，动画和排版做在面板 Prefab（Tips 根上也可以自己挂 LayoutGroup）。

```csharp
UI.ConfigureTips(maxVisible: 3, maxQueued: 8, defaultDuration: 2f);

await UI.Tips<NetStatusPanel>();                         // 常驻状态条
await UI.Toast<HintToast, string>("保存成功");
await UI.Toast<HintToast, string>("保存成功", duration: 1.5f);
await UI.Toast<StickyToast>(duration: 0f);               // 常驻，点关闭或 CloseSelf
await UI.Guide<NewbieGuidePanel>();
```

可见满了只把 `{type, args, duration}` 入队，**出队后才加载**。有人关掉（到时或手动）再开下一条。队列满丢掉最旧等待项。`duration` 为空用默认秒数，`<= 0` 表示不自动关。

关掉后实例按类型进闲置列表（容量和 `maxVisible` 同级），再开同一类型会 `ApplyArgs` + `OnOpen`，`OnCreate` 只第一次。位移动画请在 `OnOpen` 开头自己复位。`Register(..., cache: false)` 时关闭会 Destroy。`maxVisible` 为 0 时新的 `Toast` 立即返回 `null`，已在排队的等待也会被取消。

句柄仍归 `UILoader`，不要把 `UIPanel` 放进 `GameObjectPoolService`。世界坐标飘字（伤害数字）用 `UIItem` + 对象池，不是 Toast。

`Get<TPanel>()` 对 Toast 返回该类型当前最上面一条；`Close<TPanel>()` 关掉该类型所有可见 Toast，并取消仍在排队的同类型请求。

## 循环列表

列表面板继承 `UILoopScrollBase<TArgs>` 或 `UILoopScrollMultiBase<TArgs>`，只实现 `ProvideData`。Inspector 填 Cell 的 YooAsset location，打开前注入对象池并异步准备。列表内异步请用 `OpenCancellationToken`（缓存关闭会取消；`destroyCancellationToken` 不会）：

```csharp
public sealed class PlayerListPanel : UILoopScrollBase<UINone>
{
    protected override void OnOpen(UINone args)
    {
        BindAsync(OpenCancellationToken).Forget();
    }

    async UniTaskVoid BindAsync(CancellationToken ct)
    {
        SetPool(gamePool);
        var cancelled = await PrepareCellsAsync(
            new GameObjectPoolOptions(group: PoolGroup.UI),
            ct).SuppressCancellationThrow();
        if (cancelled) return;

        ScrollRect.totalCount = players.Count;
        ScrollRect.RefillCells();
    }

    public override void ProvideData(Transform item, int index)
    {
        item.GetComponent<PlayerItem>().Bind(players[index]);
    }
}
```

Cell 由 `LoopScrollPoolSource` 同步 `TrySpawn` / `DespawnImmediate`。不要把 `UIPanel` 放进这个池。多 Prefab 列表实现 `GetCellLocation(int index)`，并 `PrepareCellsAsync` 传入所有 location。无 Sprite 的 Image 时，尺寸会回退到 RectTransform；有正数 LayoutElement 仍优先。

## 目录

- `Docs`：USAGE / Scene / Timer / Audio / Input / Event / Fsm / RedDot / Pool
- `Runtime/Core`：面板 API、生命周期、栈与缓存、Tips / Toast / Guide、循环列表基类
- `Runtime/Scene`：`GameScene` 场景加载与激活
- `Runtime/Timer`：`GameTimer` 调度
- `Runtime/Audio`：`GameAudio`
- `Runtime/Input`：`GameInput`
- `Runtime/Pooling`：托管对象池与 GameObjectPoolService
- `Runtime/LoopScroll`：循环列表组件
- `Runtime/Load`：YooAsset 异步加载
- `Runtime/Root`：运行时 Canvas 与 UI 层
- `Runtime/Screen`：屏幕方向、Canvas 布局同步、SafeArea
- `Editor`：面板脚本与绑定代码生成工具

## License

[MIT](LICENSE)

## 错误与生命周期契约

- 普通 Close / Destroy / ClearCache 的业务回调失败会传播给调用方。失败后不自动重试，也不继续依赖成功关闭的 Resume 或队列推进。
- OnClose 或打开生命周期取消失败时，面板不进入正常缓存，实例与首次异常仍由管理器保留；同类型重新 Open 会明确报错。定位原因后可显式 Destroy 释放，销毁不会重跑失败的 OnClose。
- Window / Popup 只有关闭成功后才移出导航栈。关闭中或失败的实例会阻止相关 Back、遮罩点击及新的导航操作；独立 Hud 不受影响。显式 Destroy 成功后统一更新遮罩并恢复上一窗口。
- CloseGroup 在处理成员前检查同组关闭中／失败状态，普通关闭不会遗漏失败成员；窗口按栈底到栈顶的顺序关闭，避免恢复本组内接下来还要关闭的窗口。显式销毁按同一关闭流程收尾。关闭中或失败的 Toast 继续占用展示名额，销毁成功后才释放。
- 关闭／显式销毁中的 OnDestroyPanel 若失败，必要对象／句柄清理仍执行，但失败登记不会消失；重复 Destroy 不会伪装成成功或重跑销毁回调，应排查错误后 Shutdown。Shutdown 从关闭回调内触发时，当前关闭负责在回调退出后完成自身销毁，原异常仍向外传播。
- ClearCache 首次失败即停止；尚未处理的对象仍在缓存中。最终 Shutdown 单独记录错误并收尾。
- 取消回调失败仍会 Dispose 当前 CTS，并终结结果等待。必要收尾再失败时记录次级异常，调用方仍收到首异常及其堆栈。
- 方向参数必须是已定义枚举；空栈 Pop 抛错，主动重置请使用 ResetTo。Initialize 后首次显式 Set 即使方向相同也会应用设备配置。
- LanguageManager.Format 的格式错误会抛出带 key、语言、模板及参数数量的异常；非法语言值不会写入状态或 PlayerPrefs。缺翻译的既有内容降级策略保留，在 Editor / Development 中告警。
- UIFrame 拥有自己的 EventSystem；Init 前发现现有 EventSystem（包括禁用对象）会拒绝初始化，不自动接管或删除。
- UIFrameSafety 的 ThreadChecks 只覆盖显式接入该检查的模块，不会为 UI 或 GameScene 自动切线程；这些 API 仍要求主线程调用。

## 回归测试

包内 `Tests/PlayMode` 和 `Tests/Editor` 覆盖失败传播、缓存保留、结果终结、真实输入、预加载互斥、精确绑定和生成器错误输出。宿主 manifest 的 `testables` 加入 `com.zzq.uiframe` 后可用 Unity Test Runner 运行。测试不会自动修复配置或忽略失败断言。
