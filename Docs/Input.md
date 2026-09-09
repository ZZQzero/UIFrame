# GameInput

入口：`Game.Input.GameInput`。绑定：`Assets/InputSystem_Actions.inputactions`。配置：`Assets/Input/Config/DefaultInputRuntimeConfig.asset`。生命周期由 `Launch` 的 `Init` / `Shutdown` 管理。

```csharp
using Game.Input;
```

---

## Init / Shutdown

```csharp
GameInput.Init(transform, inputConfig);

if (GameInput.IsInited)
{
    GameInput.Shutdown();
}
```

不走 Launch 时可用 Asset 重载（默认 Player / UI Map，保留 UI Map，灵敏度 1）：

```csharp
GameInput.Init(persistRoot, inputActionAsset);
```

- 先 Init 再读。未 Init、重复 Init、重复 Shutdown 抛 `InputStateException`。
- `Init` / `Shutdown` / `PushUi` / `PopUi` / `SetGameplayEnabled` / `Find` / 写 `LookSensitivity` 只能在主线程。
- `persistRoot` 必须非空且 `activeInHierarchy`。Init 只检查宿主还活着，不挂节点。
- `gameplayMap` 必须有 `Move`，否则 Init 失败。Look / Jump / Attack / Sprint / Navigate / Cancel 缺失时打 Warning，轮询返回 default。
- Init 会 Clone 一份 Action Asset，之后只改克隆。不要销毁输入资源。

---

## 玩法轮询 `GameInput.Player`

每帧从静态入口读，不要在 `Update` 里 `FindAction`，不要跨 Shutdown 缓存 `Player`。

```csharp
void Update()
{
    if (!GameInput.IsInited)
    {
        return;
    }

    Vector2 move = GameInput.Player.Move;
    Vector2 look = GameInput.Player.Look; // 已乘 LookSensitivity
    bool jump = GameInput.Player.JumpPressed;
    bool attack = GameInput.Player.AttackPressed;
    bool sprint = GameInput.Player.SprintHeld;
}
```

| API | 动作 | 缺失时 |
|-----|------|--------|
| `Move` | Move（必选） | Init 失败 |
| `Look` | Look | `Vector2.zero` |
| `JumpPressed` | Jump | `false` |
| `AttackPressed` | Attack | `false` |
| `SprintHeld` | Sprint | `false` |

`LookSensitivity` 默认来自配置，运行时可改，必须大于 0。

不在 `Player` 上的动作，在 `Start` 里 `Find` 一次并自己缓存：

```csharp
InputAction interact;

void Start()
{
    interact = GameInput.Find(PlayerActions.Map, PlayerActions.Interact);
}

void Update()
{
    if (interact.WasPressedThisFrame())
    {
        Interact();
    }
}
```

Map / Action 不存在抛 `KeyNotFoundException`；名为空抛 `ArgumentException`。常量见 `PlayerActions` / `UiActions`。

---

## UI 轮询 `GameInput.UI`

```csharp
if (GameInput.UI.CancelPressed)
{
    ClosePanel();
}

Vector2 navigate = GameInput.UI.Navigate;
```

默认 `keepUiMapEnabled = true`：玩法期间 UI Map 仍开，HUD 点击和 `Cancel` 可用。**Navigate 只在 `PushUi` 之后启用**，避免 WASD / 左摇杆同时打到 Move。

若 EventSystem 直接用源 Asset 而不是本运行时克隆，玩法期间需自行关掉源 Asset 的 Navigate。

---

## 开界面 / 暂停

两套正交状态。玩法 Map 实际 Enable 当且仅当 `IsGameplayEnabled && UiLockCount == 0`。不要用暂停代替开商店。

```csharp
InputLayerHandle layer;

void OnOpenShop()
{
    layer = GameInput.PushUi();
}

void OnCloseShop()
{
    if (GameInput.IsInited)
    {
        GameInput.TryPopUi(layer);
    }

    layer = default;
}

void OnPause(bool paused)
{
    GameInput.SetGameplayEnabled(!paused);
}
```

| API | 行为 |
|-----|------|
| `PushUi()` | 压一层 UI 锁，关玩法 Map，开 Navigate |
| `PopUi(handle)` | 必须弹栈顶，否则抛 `InputStateException` |
| `TryPopUi(handle)` | 不是栈顶或已 Shutdown 后的旧 handle 返回 false |
| `SetGameplayEnabled(false)` | 不改锁栈，只关玩法 Map |
| `IsGameplayEnabled` | 暂停开关，不是 Map 是否已 Enable |
| `UiLockCount` | 当前 UI 锁层数 |

嵌套面板各 `PushUi` 一次，后开的先 Pop。`InputLayerHandle` 是进程内单调代次；Shutdown 后再 Init，上一局的 handle 不能弹新锁。关闭时用 `TryPopUi`，避免重复 Shutdown 后 `PopUi` 抛异常。

---

## 配置 `InputRuntimeConfig`

菜单：`Game/Input Runtime Config`。

| 字段 | 默认 | 含义 |
|------|------|------|
| actions | `InputSystem_Actions` | 源 Asset |
| gameplayMap | `Player` | 玩法 Map，必须存在且含 Move |
| uiMap | `UI` | 可留空，则不启用 UI Map |
| keepUiMapEnabled | true | 玩法期间保持 UI Map Enable（Navigate 仍只在有 UI 锁时开） |
| lookSensitivity | 1 | `Player.Look` 倍率 |

加常用动作：在 `.inputactions` 里加绑定，并在 `PlayerControls` 里加字段。一次性冷路径用 `Find`。

---

## 注意

- 在 `namespace Game` 里不要写裸 `Input.`，会撞到 `Game.Input`。旧 Input Manager 用 `UnityEngine.Input`。
- 不要用 `Input.GetKey` / `GetAxis`。
- 开 UI 时不要手动把 Move 写成零，用 `PushUi`。
- 本机只有一份 `GameInput.Player`，不要给每个单位各订一份输入。
