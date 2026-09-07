# EventSystem 使用说明与注意事项

宿主层事件总线，**独立于 UIFrame**。面板打开/关闭、弹窗返回值仍走 `UI.Push` / `UI.Popup<..., TResult>`，不要用事件代替。

与 Unity `EventSystems.EventSystem` 重名时写 `Game.EventSystem`。

---

## 1. 定义事件

热路径用 **`readonly struct`**，避免 class 装箱和共享可变引用。

```csharp
public readonly struct BagChanged
{
    public readonly int ItemId;
    public readonly int Count;

    public BagChanged(int itemId, int count)
    {
        ItemId = itemId;
        Count = count;
    }
}
```

### 注意

- **禁止**把大块 class / 可变对象当高频事件参数。
- `Post<T>` **只接受 struct**。需要从网络线程发事件时，载荷必须是值类型。
- 一种业务变化用一个事件类型；不要做字符串频道或优先级。

---

## 2. 订阅与退订

### 推荐：带 Owner（面板、系统、MonoBehaviour）

销毁时调一次 `UnsubscribeAll(this)`，该 Owner 下所有类型都会摘掉。

```csharp
EventSystem.Subscribe<BagChanged>(this, OnBagChanged);
EventSystem.UnsubscribeAll(this);
```

### 无 Owner（全局系统）

必须自己拿着 `EventHandle` 退订。不要用 lambda 相等去退订——总线不认 `Action` 引用匹配。

```csharp
EventHandle handle = EventSystem.Subscribe<BagChanged>(OnBagChanged);
EventSystem.Unsubscribe(handle);          // 成功 true；同一句柄再退订 false
```

### 注意

- `Subscribe` / `Unsubscribe` / `UnsubscribeAll` 必须在**主线程**。
- `handler` 为 null 会抛 `ArgumentNullException`。
- 同一 handler 可以订多次，会派发多次；退订靠句柄，不靠委托相等。
- 不 `UnsubscribeAll` 就把对象 Destroy，监听会泄漏，Owner 对象也无法回收。
- 发布过程中 **可以** `Unsubscribe` / `UnsubscribeAll`：尚未轮到的监听会被跳过。这是合法的销毁路径。

---

## 3. 派发：Publish 与 Post

| API | 线程 | 时机 | 载荷 |
|-----|------|------|------|
| `Publish<T>(in T evt)` | 仅主线程 | 立即、同步 | class / struct 均可 |
| `Post<T>(in T evt)` | 任意线程 | 本帧 `LateUpdate`（或手动 `DrainPosted`） | **仅 struct** |

```csharp
EventSystem.Publish(new BagChanged(itemId, count));   // 主线程立刻通知

// 网络 / 工作线程
EventSystem.Post(new BagChanged(itemId, count));
```

无订阅时 `Publish` 直接返回。监听按订阅顺序调用。

发布当中新订阅的 handler **收不到本次**，从下一次起生效。

### 注意

- `Subscribe` / `Publish` 不需要 Dispatcher；只有跨线程或延后派发的 `Post` 需要先 `EnsureDispatcher()`。未创建就 `Post` 会抛 `EventSystemException`，事件不会入队。
- 回调里**禁止**再 `Publish` **同一类型**，会抛 `EventSystemException`。要再发就 `Post`。
- 回调里 `Publish` **其它类型**可以（不同桶）。不要借此绕成深递归。
- 回调里**禁止** `Clear<T>` / `ClearAll`，会抛 `EventSystemException`。
- Editor / Development：普通 handler 异常会打日志并继续后面的监听；**`EventSystemException` 会中断本次派发**（用错就暴露）。
- Release：不包 per-handler `try`。handler 必须自保；抛错会打断后续监听，但总线会复位内部状态。
- `ThreadChecks` 默认 Editor/Dev 开、Release 关。关掉后后台 `Publish` **不会报错，但是数据竞争**。正式包也必须主线程 `Publish`。
- `Post` 每帧最多 Drain **1024** 条，单类型最多 **256**。超出的下一帧继续。前几种事件占满额度时，后面的类型会延后。
- 正式游戏不要依赖 `DrainPosted`；那是测试或需要立刻刷完队列时用的。平时靠 Dispatcher 的 `LateUpdate`。

---

## 4. 清理与诊断

```csharp
EventSystem.Clear<BagChanged>();     // 只清一种类型的监听和 Post 队列
EventSystem.ClearAll();              // 全部监听 + 全部 Post 队列
EventSystem.ListenerCount<BagChanged>();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
EventSystem.DumpListenerCounts();    // 每种类型的监听数打到 Console
#endif
```

### 注意

- `Clear` / `ClearAll` 必须在主线程，且不能在任何类型的 `Publish` 过程中调用。
- `ClearAll` 后句柄全部失效；需要再玩时重新 Subscribe。`ClearAll` **不停** Dispatcher。

---

## 5. 会抛什么

| 情况 | 异常 |
|------|------|
| `handler == null` | `ArgumentNullException` |
| 非主线程 `Publish` / `Subscribe` / `Clear`（`ThreadChecks` 开） | `EventSystemException` |
| 派发中再次 `Publish` 同类型 | `EventSystemException` |
| 派发中 `Clear` / `ClearAll` | `EventSystemException` |
| 未 `EnsureDispatcher` 就 `Post` | `EventSystemException` |

用错不要吞掉这些异常，按日志改调用方式。

---

## 6. 和 UIFrame 的边界

| 场景 | 用什么 |
|------|--------|
| 打开/关闭面板、弹窗要返回值 | `UI.Push` / `UI.Popup<..., TResult>` |
| 红点数量 | `RedDot.Set` / `Bind` |
| 背包变了、任务完成、多模块广播 | `EventSystem` |
| 列表面板刷 Cell | `ProvideData`，不要对每个 Cell 发全局事件 |
