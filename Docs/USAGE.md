# UIFrame 各系统用法与注意点

本文按系统说明用法，以及接入时必须遵守的边界。更细的红点与对象池见 [RedDot.md](RedDot.md)、[Pool.md](Pool.md)。

---

## 1. 启动与关闭

### 用法

推荐顺序：

```csharp
UI.Init(package);                    // 或 Init() / Init(packageName)
if (UI.ConfigureURPCameraStack() == null)
{
    // URP Stack 失败时 UI Camera 是孤立 Overlay，界面不可见
    throw new InvalidOperationException("UI Camera Stack 配置失败。");
}

UI.Register<MainPanel>("MainPanel", UIGroup.Scene);
GamePool.Instance.Init(package, persistRoot); // 若需要对象池

var panel = await UI.Push<MainPanel>();
if (panel == null)
{
    // 加载失败
}

// 退出时：先 UI，再池
UI.Shutdown();
GamePool.Instance.Release();
```

### 注意

- 先 `Init`，再 `Register` / 打开面板。
- 退出顺序必须是 **`UI.Shutdown()` → 再释放对象池**。面板的 `OnDestroyPanel` 可能还要还池。
- `Shutdown` 会销毁 Root、打开中与缓存面板，并释放 YooAsset Handle；**注册表会保留**，可再次 `Init`。
- 不要只检查“启动完成”日志：相机 Stack、首屏 `Push` 返回值都要校验。
- 宿主需在退出 Play / `OnApplicationQuit` / `OnDestroy` 里主动 Teardown；框架本身不注册 Editor PlayMode 退出钩子。

---

## 2. 面板核心（层级、打开、关闭、生命周期）

### Canvas 层与打开 API

```text
CanvasRoot
├── Window   ← UI.Push
├── Hud      ← UI.Hud
├── Mask     ← 框架内部（Popup 遮罩）
├── Popup    ← UI.Popup
├── Tips     ← UI.Tips / UI.Toast
└── Guide    ← UI.Guide
```

| API | 层 | 语义 |
|-----|----|------|
| `UI.Hud` | Hud | 常驻 HUD，不进窗口栈，`Back` 关不掉 |
| `UI.Push` | Window | 窗口栈；新 Push 会 Pause 旧窗，并关掉全部 Popup |
| `UI.Popup` | Popup | 弹窗栈；显示 Mask，可点遮罩关闭 |
| `UI.Tips` | Tips | 同类型单实例，不排队、不定时 |
| `UI.Toast` | Tips | 多实例 + 队列 + 可选自动关闭 |
| `UI.Guide` | Guide | 最上层引导，不进栈 |

### UIGroup（卸载分组，不是显示层）

| Group | 用途 |
|-------|------|
| `Scene` | 默认。切场景用 `UI.CloseGroup(UIGroup.Scene)` |
| `Hud` | HUD 组；`CloseGroup(Scene)` 不会关它 |
| `Persistent` | 全局常驻 |

```csharp
UI.Register<MainHud>("MainHud", UIGroup.Hud);
UI.Register<BagPanel>("Bag", UIGroup.Scene, cache: true);
UI.CloseGroup(UIGroup.Scene);                 // 默认进缓存
UI.CloseGroup(UIGroup.Scene, destroy: true);  // 销毁并释放 Handle
```

### 生命周期时序

首次打开：

```text
加载 / Instantiate
→ OnCreate          // 只一次：绑按钮
→ ApplyArgs
→ 激活并挂到对应层
→ OnOpen(args)      // 每次显示：刷数据、开异步
```

缓存后再开：只走 `ApplyArgs` → `OnOpen`，**不再** `OnCreate`。

关闭：

```text
OnClose             // 取消本次显示态、还临时对象
→ 默认隐藏进缓存
→ destroy: true 时 OnDestroyPanel + Destroy + Release Handle
```

### OpenCancellationToken

```csharp
protected override void OnOpen(MyArgs args)
{
    BindAsync(args, OpenCancellationToken).Forget();
}

async UniTask BindAsync(MyArgs args, CancellationToken ct)
{
    var cancelled = await PrepareAsync(ct).SuppressCancellationThrow();
    if (cancelled) return;
    // 使用本次传入的 args，不要事后读可能被覆盖的 Args
}
```

关闭、重新打开、销毁都会取消上一次 Open 作用域；Pause/Resume 不会取消。

### 注意

- **严禁** `Destroy(panel.gameObject)`。只能 `UI.Close` / `UI.Destroy` / `CloseSelf` / `CloseAndDestroySelf`。
- 按钮监听放 `OnCreate`（或 LoopScroll 的 `OnLoopScrollCreated`）；数据刷新放 `OnOpen`。
- 同类型 `Hud/Push/Popup/Tips/Guide` 单实例；再次打开会复用并再次 `OnOpen`，**不会先 `OnClose`**。
- 同类型加载中再次 Open：合并为一次加载，后一次 Args/Mode 生效。
- **`Tips` 与 `Toast` 不要用同一面板类型**（通道不同，可能同时存在两套实例）。
- `Back()` 只关 Popup / Window；Hud / Tips / Guide / Toast 需显式 Close。
- 正式包必须 `UI.Register`；Editor 未注册时用类名兜底，真机不会。
- 默认 `cache: true`：Close 只隐藏，不释放内存；要释放用 `destroy: true` 或 `ClearCache()`。

---

## 3. URP Camera Stack

### 用法

```csharp
UI.Init(package);
UI.ConfigureURPCameraStack();                 // Base = Camera.main
// 或
UI.ConfigureURPCameraStack(baseCamera, uiCamera, uiLayer);

UI.DisableURPCameraStack();                   // 仅从 Stack 移除
```

### 设计约定（不要当成 bug）

- UI Camera 固定为 Overlay。
- 只负责加入 / 移出 Base Camera Stack。
- **不修改** Base Camera 的其它配置。
- `Disable` 后 Overlay 不再独立渲染，界面会消失；这是预期行为。

### 注意

- `ConfigureURPCameraStack` 返回 `null` 时不要继续当启动成功。
- 框架硬依赖 URP。

---

## 4. 屏幕方向

### 用法

```csharp
// UI.Init 只会检测当前横竖并同步 Canvas 参考分辨率，
// 不会改 Screen.orientation。

ScreenOrientationManager.SetPortrait();
ScreenOrientationManager.SetLandscape();
ScreenOrientationManager.Push(GameScreenOrientation.Landscape);
ScreenOrientationManager.Pop();
ScreenOrientationManager.ResetTo(GameScreenOrientation.Portrait);
ScreenOrientationManager.SyncCanvasLayoutNow();
```

### 注意

- 只有显式 `Set / Push / Pop / ResetTo` 才会写系统方向。
- `UI.Shutdown` **不恢复**系统方向（进程退出场景下不需要恢复）。
- `Shutdown` 会清空方向栈与事件订阅；业务若自己订阅了 `CanvasLayoutChanged`，不要假设 Shutdown 后还在。

---

## 5. SafeArea

### 用法

```csharp
// 挂在内容节点上，不要挂 Layer / 全屏背景 / Mask / Guide 根
fitter.SetPads(left: true, right: true, bottom: true, top: true);

// 原生壳已扣过顶底时
fitter.SetPads(left: true, right: true, bottom: false, top: false);

ScreenSafeArea.SetOverride(rect); // Editor / 测试模拟
ScreenSafeArea.Refresh();
```

### 注意

- `ScreenSafeArea.Current` 只读缓存；由 `UIFrameRoot` 在转屏、尺寸变化、焦点恢复时刷新，并再连刷两帧。
- 父节点必须是铺满 Canvas 的 Stretch，否则可能套两次安全区。
- `Shutdown` 清缓存但不主动通知 Fitter；组件 `OnDisable` 会解绑事件。

---

## 6. Tips / Toast

### 用法

```csharp
UI.ConfigureTips(maxVisible: 3, maxQueued: 8, defaultDuration: 2f);

await UI.Tips<NetStatusPanel>();                      // 常驻状态条
await UI.Toast<HintToast, string>("保存成功");
await UI.Toast<HintToast, string>("保存成功", 1.5f);
await UI.Toast<StickyToast>(duration: 0f);            // 常驻到手动关
```

### 注意

- 可见满了只入队，出队后才加载；队列满丢掉最旧等待项。
- `duration <= 0` 不自动关。
- Toast 关掉后按类型进闲置列表复用 `OnOpen`；`Register(..., cache: false)` 时关闭会 Destroy。
- 世界飘字（伤害数字）用 `UIItem` + 对象池，不要用 Toast。
- 不要把 `UIPanel` 放进 `GameObjectPoolService`。

---

## 7. 红点（RedDot）

详见 [RedDot.md](RedDot.md)。

### 用法摘要

```csharp
RedDot.Set("Mail/Inbox", 3);          // 只设叶子
int total = RedDot.Get("Mail");       // 父节点自动聚合
RedDot.Bind("Mail", OnChanged);
RedDot.Unbind("Mail", OnChanged);
RedDot.Clear();                       // 切号：清数据，保留监听
RedDot.Remove("Activity/Summer");     // 删子树
```

`RedDotView`：挂在常驻宿主上，`Target` 不能是自身或祖先。

### 注意

- 必须在主线程调用。
- 用绝对数量 `Set`，不要加减累计。
- 父节点不能直接 `Set`。
- Play 模式 LateUpdate 自动 Flush；业务通常不要手动 Flush。
- 登出 / 切号调 `Clear`；功能卸载调 `Remove`。

---

## 8. 对象池（Game.Pooling）

详见 [Pool.md](Pool.md)。

### 用法摘要

```csharp
var pool = new GameObjectPoolService(package, poolRoot);
await pool.PrewarmAsync("PlayerItem", 32, options, ct);

if (pool.TrySpawn("PlayerItem", parent, out PlayerItem item))
{
    // ...
    pool.DespawnImmediate(item.gameObject);
}
```

循环列表必须：异步 Prepare / Prewarm，同步 `TrySpawn` / `DespawnImmediate`。

### 注意

- 与 `UIPanel` 缓存是两套所有权：**UIPanel 不要进这个池**。
- 退出时先 `UI.Shutdown`，再 `Dispose` / `GamePool.Release`。
- 禁止业务直接 `Destroy` 池化实例。
- 主线程与集合检查看 `UIFrameSafety`。

---

## 9. 循环列表（LoopScroll）

### 用法

```csharp
public sealed class RankPanel : UILoopScrollBase<RankArgs>
{
    protected override void OnLoopScrollCreated()
    {
        closeBtn.onClick.AddListener(OnClickClose); // 只绑一次
    }

    protected override void OnOpen(RankArgs args)
    {
        BindAsync(args, OpenCancellationToken).Forget();
    }

    async UniTask BindAsync(RankArgs args, CancellationToken ct)
    {
        SetPool(GamePool.Instance.Service);
        var cancelled = await PrepareCellsAsync(
            new GameObjectPoolOptions(group: PoolGroup.UI), ct)
            .SuppressCancellationThrow();
        if (cancelled) return;

        ScrollRect.totalCount = args.Ranks.Count;
        ScrollRect.RefillCells();
    }

    public override void ProvideData(Transform item, int index)
    {
        item.GetComponent<RankCell>().Bind(Args.Ranks[index]);
    }
}
```

多 Prefab 列表继承 `UILoopScrollMultiBase`，实现 `GetCellLocation(int)`，并 `PrepareCellsAsync(locations)`。

### 注意

- `OnCreate` 已 sealed；额外初始化覆写 `OnLoopScrollCreated`。
- 覆写 `OnClose` / `OnDestroyPanel` 必须 `base.`，否则 Cell 不还池。
- 列表异步必须用 `OpenCancellationToken`，不要只用 `destroyCancellationToken`（缓存关闭不会取消后者）。
- Cell 无 Sprite 的 Image 时，尺寸会回退到 RectTransform；有正数 LayoutElement 仍优先。

---

## 10. 编辑器代码生成 / 绑定

### 用法

1. 选中 UI Prefab 根 → Inspector「生成脚本」。
2. 子节点点「添加到 XXX」记录绑定。
3. 「写入脚本」生成 `.Gen.cs`，编译后自动挂组件并回填引用。
4. Prefab Stage 里改完后要 **保存 Prefab**。

### 注意

- `.Gen.cs` 是生成文件，不要手改。
- Prefab Stage 若未保存就关闭，磁盘可能没有组件/引用。
- 绑定失败时宁可报错，也不要依赖宽松猜节点；重名节点要小心。
- Host 匹配依赖类型名；避免同名不同类型脚本挂同一对象。

---

## 11. 安全开关

```csharp
UIFrameSafety.ThreadChecks = true;      // 主线程检查
UIFrameSafety.CollectionChecks = true;  // 池重复归还检查
```

默认 Editor / Development 打开，Release 关闭。要在正式包抓问题，在 `Init`、建池、调红点之前打开。

---

## 12. 推荐宿主模板

```csharp
UI.Init(package);
if (UI.ConfigureURPCameraStack() == null) throw ...;

UI.Register<MainHud>("MainHud", UIGroup.Hud);
UI.Register<HomePanel>("Home", UIGroup.Scene);
GamePool.Instance.Init(package, transform);

await UI.Hud<MainHud>();
var home = await UI.Push<HomePanel>();
if (home == null) throw ...;

// 切场景
UI.CloseGroup(UIGroup.Scene, destroy: true);

// 退出
UI.Shutdown();
GamePool.Instance.Release();
```

---

## 快速对照：什么时候用哪个

| 需求 | 用 |
|------|----|
| 主界面 / 功能页 | `Push` |
| 确认框 / 二级弹窗 | `Popup` |
| 血条 / 摇杆 / 货币栏 | `Hud` |
| 网络状态条等单实例 Tips | `Tips` |
| 飘字 / 多条自动消失提示 | `Toast` |
| 新手引导遮罩层 | `Guide` |
| 列表 Cell / 特效多实例 | `GameObjectPool` + `UIItem` |
| 未读角标 | `RedDot` / `RedDotView` |
