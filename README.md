# UIFrame

基于 URP、UGUI、YooAsset 和 UniTask 的轻量 Unity UI 框架，提供面板生命周期、层级栈、缓存、异步加载、URP Camera Stack、屏幕方向管理、SafeArea 和编辑器绑定代码生成。

## 环境要求

- Unity 6000.0 或更高版本
- UniTask 2.5.10 或更高版本
- YooAsset 3.0.5 或更高版本
- UGUI 2.0.0 或更高版本
- Universal RP 17.3.0 或更高版本

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

各系统详细用法与注意点见 [USAGE.md](Docs/USAGE.md)。红点见 [RedDot.md](Docs/RedDot.md)，对象池见 [Pool.md](Docs/Pool.md)。

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
        if (UI.ConfigureURPCameraStack() == null)
        {
            throw new InvalidOperationException("UI Camera Stack 配置失败。");
        }

        UI.Register<MainPanel>("MainPanel");
        var panel = await UI.Push<MainPanel>();
        if (panel == null)
        {
            throw new InvalidOperationException("MainPanel 打开失败。");
        }
    }
}
```

不再使用时调用（若还用了对象池，先 UI 再释池）：

```csharp
UI.Shutdown();
// GamePool.Instance.Release(); // 宿主侧对象池
```

Hud / Push / Popup / Tips / Guide 按面板 **Type** 去重。同一类型正在加载时再次 Open，不会发起第二次加载，而是合并进这次请求：后一次的 `Args` 和 `Mode` 覆盖前一次，并把已取消扳回未取消。两次 `await` 拿到同一块面板，参数以最后一次为准。已经打开的同类型会走已有实例（`ApplyArgs`），不会再加载。**Toast** 是多实例通道，不走这条合并规则。

主线程检查和托管池重复归还检查由 `UIFrameSafety` 控制。默认 Editor / Development 打开，Release 关闭。QA 要在正式包里抓线程错误时，在 `Init`、建池、调用红点之前设 `UIFrameSafety.ThreadChecks = true`。`CollectionChecks` 只作用于之后新建的托管池。

## URP Camera Stack

`UI.Init()` 后 Canvas 默认为 `Screen Space Camera`，并绑定常驻 UI Camera；同时开启 `Vertex Color Always In Gamma Color Space`。此时 UI Camera 可能是孤立 Overlay，界面不可见，需要配置 Stack：

```csharp
if (UI.ConfigureURPCameraStack() == null)
{
    // Base = Camera.main，复用已有 UI Camera；失败时检查主相机 / URP
}
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

- `Docs`：USAGE / RedDot / Pool 使用说明
- `Runtime/Core`：面板 API、生命周期、栈与缓存、Tips / Toast / Guide、循环列表基类
- `Runtime/Pooling`：托管对象池与 GameObjectPoolService
- `Runtime/LoopScroll`：循环列表组件
- `Runtime/Load`：YooAsset 异步加载
- `Runtime/Root`：运行时 Canvas 与 UI 层
- `Runtime/Screen`：屏幕方向、Canvas 布局同步、SafeArea
- `Editor`：面板脚本与绑定代码生成工具

## License

[MIT](LICENSE)
